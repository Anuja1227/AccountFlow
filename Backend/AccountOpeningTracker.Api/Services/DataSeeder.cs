using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Services;

/// <summary>
/// Idempotent startup seeding. Never deletes or modifies account records.
///
/// 1. Permission catalog (always kept in sync with code).
/// 2. Default teams and roles (created only if missing; Admin edits are preserved,
///    except that the Administrator role always holds every permission).
/// 3. First administrator: created only when no active Administrator exists AND
///    Bootstrap:AdminEmail / Bootstrap:AdminPassword are supplied through
///    user-secrets or environment variables. There is no default password.
/// 4. Optional demo users (Development only, DemoUsers:Enabled=true and a
///    DemoUsers:Password supplied through user-secrets).
/// </summary>
public sealed class DataSeeder
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _env;
    private readonly ILogger<DataSeeder> _log;

    public DataSeeder(
        ApplicationDbContext db,
        UserManager<AppUser> users,
        IConfiguration config,
        IHostEnvironment env,
        ILogger<DataSeeder> log)
    {
        _db = db;
        _users = users;
        _config = config;
        _env = env;
        _log = log;
    }

    public async Task SeedAsync()
    {
        var newPermissions = await SeedPermissionsAsync();
        await SeedTeamsAsync();
        await SeedRolesAsync(newPermissions);
        await BootstrapAdminAsync();
        await SeedDemoUsersAsync();
    }

    /// <returns>Codes of permissions added to the catalog in this run.</returns>
    private async Task<HashSet<string>> SeedPermissionsAsync()
    {
        var added = new HashSet<string>();
        var existing = await _db.Permissions.ToListAsync();
        foreach (var def in Permissions.Catalog)
        {
            var row = existing.FirstOrDefault(p => p.Code == def.Code);
            if (row == null)
            {
                _db.Permissions.Add(new Permission
                {
                    Code = def.Code, Group = def.Group, Description = def.Description
                });
                added.Add(def.Code);
            }
            else
            {
                row.Group = def.Group;
                row.Description = def.Description;
            }
        }
        await _db.SaveChangesAsync();
        return added;
    }

    private async Task SeedTeamsAsync()
    {
        if (await _db.Teams.AnyAsync()) return;

        foreach (var (name, type, description) in DefaultTeams.Seed)
        {
            _db.Teams.Add(new Team { Name = name, WorkspaceType = type, Description = description });
        }
        await _db.SaveChangesAsync();
    }

    private async Task SeedRolesAsync(HashSet<string> newPermissions)
    {
        var permissions = await _db.Permissions.ToDictionaryAsync(p => p.Code, p => p.Id);
        var anyRoles = await _db.AppRoles.AnyAsync();

        foreach (var (name, description, codes) in DefaultRoles.Seed)
        {
            var role = await _db.AppRoles
                .Include(r => r.RolePermissions)
                .FirstOrDefaultAsync(r => r.Name == name);

            if (role == null)
            {
                // Only create the non-admin defaults on a fresh install so an Admin
                // who deliberately deleted a default role does not see it return.
                if (anyRoles && name != DefaultRoles.Administrator) continue;

                role = new AppRole { Name = name, Description = description, IsSystemRole = true };
                foreach (var code in codes)
                    role.RolePermissions.Add(new RolePermission { PermissionId = permissions[code] });
                _db.AppRoles.Add(role);
            }
            else if (role.IsSystemRole && newPermissions.Count > 0 && name != DefaultRoles.Administrator)
            {
                // A permission introduced by an upgrade is granted once to the default
                // roles that include it; later Admin changes are never overridden.
                foreach (var code in codes.Where(newPermissions.Contains))
                {
                    if (role.RolePermissions.All(rp => rp.PermissionId != permissions[code]))
                        role.RolePermissions.Add(new RolePermission { PermissionId = permissions[code] });
                }
            }
            else if (name == DefaultRoles.Administrator)
            {
                // Administrator always holds the full catalog.
                role.IsSystemRole = true;
                role.IsActive = true;
                foreach (var code in codes)
                {
                    if (role.RolePermissions.All(rp => rp.PermissionId != permissions[code]))
                        role.RolePermissions.Add(new RolePermission { PermissionId = permissions[code] });
                }
            }
        }
        await _db.SaveChangesAsync();
    }

    private async Task BootstrapAdminAsync()
    {
        var adminRole = await _db.AppRoles.FirstAsync(r => r.Name == DefaultRoles.Administrator);

        bool hasActiveAdmin = await _db.AppUserRoles
            .AnyAsync(ur => ur.RoleId == adminRole.Id && ur.User!.IsActive);
        if (hasActiveAdmin) return;

        var email = _config["Bootstrap:AdminEmail"];
        var password = _config["Bootstrap:AdminPassword"];
        var name = _config["Bootstrap:AdminName"] ?? "System Administrator";

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            _log.LogWarning(
                "No active administrator exists. Set Bootstrap:AdminEmail and Bootstrap:AdminPassword " +
                "with 'dotnet user-secrets' (or environment variables Bootstrap__AdminEmail / " +
                "Bootstrap__AdminPassword) and restart the API to create the first administrator.");
            return;
        }

        var user = await _users.FindByEmailAsync(email);
        if (user == null)
        {
            user = new AppUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = name,
                IsActive = true
            };
            var result = await _users.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                _log.LogError("Could not create the bootstrap administrator: {Errors}",
                    string.Join("; ", result.Errors.Select(e => e.Description)));
                return;
            }
        }
        else
        {
            user.IsActive = true;
            await _users.UpdateAsync(user);
        }

        if (!await _db.AppUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == adminRole.Id))
            _db.AppUserRoles.Add(new UserRole { UserId = user.Id, RoleId = adminRole.Id });

        var adminTeam = await _db.Teams.FirstOrDefaultAsync(t => t.WorkspaceType == WorkspaceTypes.Admin);
        if (adminTeam != null &&
            !await _db.UserTeams.AnyAsync(ut => ut.UserId == user.Id && ut.TeamId == adminTeam.Id))
            _db.UserTeams.Add(new UserTeam { UserId = user.Id, TeamId = adminTeam.Id });

        _db.AuditEvents.Add(new AuditEvent
        {
            UserId = user.Id,
            UserName = "system",
            Action = "Bootstrap administrator provisioned",
            Details = $"User {user.Id} granted the Administrator role at startup."
        });

        await _db.SaveChangesAsync();

        _log.LogWarning(
            "Bootstrap administrator {Email} was provisioned. Remove Bootstrap:AdminPassword from your " +
            "secrets now and change the password after first sign-in.", email);
    }

    private async Task SeedDemoUsersAsync()
    {
        if (!_env.IsDevelopment() || !_config.GetValue("DemoUsers:Enabled", false)) return;

        var password = _config["DemoUsers:Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            _log.LogWarning("DemoUsers:Enabled is true but DemoUsers:Password is not set. Demo users were not created.");
            return;
        }

        var cseName = _config["DemoUsers:CseName"];
        if (string.IsNullOrWhiteSpace(cseName))
        {
            cseName = await _db.Accounts.AsNoTracking()
                .Where(a => a.Cse != "")
                .Select(a => a.Cse)
                .OrderBy(c => c)
                .FirstOrDefaultAsync() ?? "Demo CSE";
        }

        var demo = new[]
        {
            ("ops.demo@accountflow.local", "Demo Operations User", DefaultRoles.OperationsExecutive, WorkspaceTypes.Operations, (string?)null),
            ("cse.demo@accountflow.local", "Demo CSE User", DefaultRoles.Cse, WorkspaceTypes.Cse, cseName),
            ("mofsl.demo@accountflow.local", "Demo MOFSL User", DefaultRoles.MofslProcessor, WorkspaceTypes.Mofsl, null),
            ("mis.demo@accountflow.local", "Demo MIS Analyst", DefaultRoles.MisAnalyst, WorkspaceTypes.Mis, null),
        };

        foreach (var (email, name, roleName, workspace, cse) in demo)
        {
            if (await _users.FindByEmailAsync(email) != null) continue;

            var role = await _db.AppRoles.FirstOrDefaultAsync(r => r.Name == roleName);
            var team = await _db.Teams.FirstOrDefaultAsync(t => t.WorkspaceType == workspace && t.IsActive);
            if (role == null || team == null) continue;

            var user = new AppUser
            {
                UserName = email, Email = email, EmailConfirmed = true,
                FullName = name, CseName = cse, IsActive = true
            };
            var result = await _users.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                _log.LogWarning("Demo user {Email} not created: {Errors}", email,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
                continue;
            }

            _db.AppUserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
            _db.UserTeams.Add(new UserTeam { UserId = user.Id, TeamId = team.Id });
            await _db.SaveChangesAsync();
            _log.LogInformation("Development demo user {Email} created.", email);
        }
    }
}
