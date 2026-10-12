using System.ComponentModel.DataAnnotations;
using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AccountOpeningTracker.Api.Controllers;

public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
}

public sealed class ChangePasswordRequest
{
    [Required] public string CurrentPassword { get; set; } = string.Empty;
    [Required, StringLength(200, MinimumLength = 10)] public string NewPassword { get; set; } = string.Empty;
}

public sealed record MeResponse(
    int Id,
    string Email,
    string FullName,
    string? CseName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<TeamInfo> Teams,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Workspaces);

/// <summary>
/// Cookie-based sign-in using ASP.NET Core Identity. The auth cookie is
/// HttpOnly + SameSite=Strict, so it is never readable by JavaScript.
/// There is no public registration endpoint: Admin creates users.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly UserManager<AppUser> _users;
    private readonly IAccessService _access;
    private readonly ApplicationDbContext _db;

    public AuthController(SignInManager<AppUser> signIn, UserManager<AppUser> users, IAccessService access, ApplicationDbContext db)
    {
        _signIn = signIn;
        _users = users;
        _access = access;
        _db = db;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        const string invalid = "Invalid email or password.";
        var user = await _users.FindByEmailAsync(request.Email.Trim());
        if (user == null) return Unauthorized(new { message = invalid });

        // Verify the password first so account state is only revealed to someone who knows it.
        var result = await _signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (result.IsLockedOut)
            return Unauthorized(new { message = "Too many failed attempts. The account is locked for 15 minutes.", code = "locked" });
        if (!result.Succeeded)
            return Unauthorized(new { message = invalid });

        if (!user.IsActive)
            return Unauthorized(new { message = "This account is inactive. Contact your administrator.", code = "inactive" });

        await _signIn.SignInAsync(user, isPersistent: request.RememberMe);

        user.LastLoginAt = DateTime.UtcNow;
        await _users.UpdateAsync(user);

        _db.AuditEvents.Add(new AuditEvent { UserId = user.Id, UserName = user.FullName, Action = "Signed in" });
        await _db.SaveChangesAsync();

        var profile = await _access.BuildProfileAsync(user.Id);
        return Ok(ToMe(profile!));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<MeResponse>> Me()
    {
        var profile = await _access.GetCurrentAsync();
        if (profile == null || !profile.IsActive) return Unauthorized();
        return Ok(ToMe(profile));
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var profile = await _access.GetRequiredCurrentAsync();
        var user = await _users.FindByIdAsync(profile.UserId.ToString());
        if (user == null) return Unauthorized();

        var result = await _users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });

        await _signIn.RefreshSignInAsync(user);
        return NoContent();
    }

    internal static MeResponse ToMe(UserAccessProfile p) => new(
        p.UserId, p.Email, p.FullName, p.CseName, p.Roles, p.Teams,
        p.Permissions.OrderBy(x => x).ToList(), p.Workspaces);
}
