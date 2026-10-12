using System.Security.Claims;
using System.Threading.RateLimiting;
using AccountOpeningTracker.Api.Ai;
using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using AccountOpeningTracker.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();

// The React dev server proxies /api to this API (same origin, see Frontend/vite.config.ts).
// CORS remains for direct cross-origin calls from the dev server origin, with credentials.
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:5173" })
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection")));

// ---------------------------------------------------------------- identity
builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();

builder.Services
    .AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// Re-validate the security stamp often so deactivation / role changes take effect quickly.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "AccountFlow.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;

    // APIs answer 401/403 instead of redirecting to a login page.
    options.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };

    // Reject the cookie immediately when the user no longer exists or was deactivated.
    options.Events.OnValidatePrincipal = async ctx =>
    {
        await SecurityStampValidator.ValidatePrincipalAsync(ctx);
        if (ctx.Principal == null) return;

        var id = ctx.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var db = ctx.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        bool active = int.TryParse(id, out var userId) &&
                      await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsActive);
        if (!active)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    };
});

// ---------------------------------------------------------------- authorization
builder.Services.AddAuthorization(options =>
{
    // Every endpoint requires a signed-in user unless it opts out with [AllowAnonymous].
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("ai", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

// ---------------------------------------------------------------- application services
builder.Services.AddScoped<IAccessService, AccessService>();
builder.Services.AddScoped<CaseAccessService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<CaseService>();
builder.Services.AddScoped<DataSeeder>();

// ---------------------------------------------------------------- AI
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.Section));
builder.Services.AddHttpClient(AiProviderFactory.HttpClientName, (sp, client) =>
{
    var seconds = builder.Configuration.GetValue("Ai:TimeoutSeconds", 90);
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 600));
});
builder.Services.AddScoped<IAiProvider>(AiProviderFactory.Create);
builder.Services.AddScoped<AiService>();
builder.Services.AddScoped<AiAutomationService>();

var app = builder.Build();

// ---------------------------------------------------------------- database + seeding
using (var scope = app.Services.CreateScope())
{
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0 && app.Configuration.GetValue("Database:ApplyMigrationsOnStartup", false))
        {
            log.LogInformation("Applying {Count} pending migration(s): {Names}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync();
            pending.Clear();
        }

        if (pending.Count > 0)
        {
            log.LogError(
                "The database has {Count} pending migration(s) ({Names}). Run 'dotnet ef database update' " +
                "in Backend/AccountOpeningTracker.Api, then restart. Seeding was skipped.",
                pending.Count, string.Join(", ", pending));
        }
        else
        {
            await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync();
        }
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Database initialisation failed. Check the connection string and that SQL Server LocalDB is running.");
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program { }
