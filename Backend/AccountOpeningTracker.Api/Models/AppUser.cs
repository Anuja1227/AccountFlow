using Microsoft.AspNetCore.Identity;

namespace AccountOpeningTracker.Api.Models;

/// <summary>
/// Application user. Password hashing, lockout and security stamps are
/// handled by ASP.NET Core Identity; roles, teams and permissions use the
/// AccountFlow access model (see AccessModel.cs).
/// </summary>
public class AppUser : IdentityUser<int>
{
    public string FullName { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The CSE name as it appears on case records (Account.Cse). Used to
    /// restrict users with "assigned cases" access to their own cases.
    /// </summary>
    public string? CseName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    public List<UserRole> UserRoles { get; set; } = new();
    public List<UserTeam> UserTeams { get; set; } = new();
    public List<UserPermissionOverride> PermissionOverrides { get; set; } = new();
}
