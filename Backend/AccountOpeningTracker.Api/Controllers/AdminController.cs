using System.ComponentModel.DataAnnotations;
using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using AccountOpeningTracker.Api.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Controllers;

public sealed class OverrideInput
{
    [Required] public string Permission { get; set; } = string.Empty;
    [Required] public string Effect { get; set; } = OverrideEffects.Grant;
}

public class UserInput
{
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = string.Empty;
    [Required, StringLength(150)] public string FullName { get; set; } = string.Empty;
    [StringLength(150)] public string? CseName { get; set; }
    public bool IsActive { get; set; } = true;
    public List<int> RoleIds { get; set; } = new();
    public List<int> TeamIds { get; set; } = new();
    public List<OverrideInput> Overrides { get; set; } = new();
}

public sealed class CreateUserInput : UserInput
{
    [Required, StringLength(200, MinimumLength = 10)] public string Password { get; set; } = string.Empty;
}

public sealed class ResetPasswordInput
{
    [Required, StringLength(200, MinimumLength = 10)] public string NewPassword { get; set; } = string.Empty;
}

public sealed class StatusInput { public bool IsActive { get; set; } }

public sealed class TeamInput
{
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Required] public string WorkspaceType { get; set; } = WorkspaceTypes.Operations;
    [StringLength(500)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class RoleInput
{
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [StringLength(500)] public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public List<string> Permissions { get; set; } = new();
}

public sealed class SettingsInput
{
    [Range(SettingsService.MinSla, SettingsService.MaxSla)]
    public int SlaThresholdWorkingDays { get; set; }

    /// <summary>Automatic | SuggestOnly | Off. Optional.</summary>
    public string? AiAutomationMode { get; set; }
}

/// <summary>
/// Admin module: users, teams, roles, permissions, overrides, settings and
/// audit. Each endpoint requires the matching admin.* permission.
/// Guardrails: no self-deactivation, at least one active Administrator, and
/// nobody can hand out permissions they do not hold themselves.
/// </summary>
[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly IAccessService _access;
    private readonly ISettingsService _settings;

    public AdminController(ApplicationDbContext db, UserManager<AppUser> users, IAccessService access, ISettingsService settings)
    {
        _db = db;
        _users = users;
        _access = access;
        _settings = settings;
    }

    // ------------------------------------------------------------ catalog

    [HttpGet("permissions")]
    [HasPermission(Permissions.AdminRoles, Permissions.AdminUsers)]
    public async Task<IActionResult> GetPermissions() =>
        Ok(await _db.Permissions.AsNoTracking()
            .OrderBy(p => p.Group).ThenBy(p => p.Code)
            .Select(p => new { p.Code, p.Group, p.Description })
            .ToListAsync());

    // ------------------------------------------------------------ users

    [HttpGet("users")]
    [HasPermission(Permissions.AdminUsers)]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _db.Users.AsNoTracking()
            .OrderBy(u => u.FullName)
            .Select(u => new
            {
                u.Id, u.Email, u.FullName, u.CseName, u.IsActive, u.CreatedAt, u.LastLoginAt,
                LockedOut = u.LockoutEnd != null && u.LockoutEnd > DateTimeOffset.UtcNow,
                Roles = u.UserRoles.Select(r => new { r.RoleId, r.Role!.Name }).ToList(),
                Teams = u.UserTeams.Select(t => new { t.TeamId, t.Team!.Name, t.Team.WorkspaceType }).ToList(),
                OverrideCount = u.PermissionOverrides.Count
            })
            .ToListAsync();
        return Ok(users);
    }

    [HttpGet("users/{id:int}")]
    [HasPermission(Permissions.AdminUsers)]
    public async Task<IActionResult> GetUser(int id)
    {
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => new
            {
                u.Id, u.Email, u.FullName, u.CseName, u.IsActive, u.CreatedAt, u.LastLoginAt,
                RoleIds = u.UserRoles.Select(r => r.RoleId).ToList(),
                TeamIds = u.UserTeams.Select(t => t.TeamId).ToList(),
                Overrides = u.PermissionOverrides.Select(o => new { Permission = o.Permission!.Code, o.Effect }).ToList()
            })
            .FirstOrDefaultAsync();
        if (user == null) return NotFound(new { message = "User not found." });

        var profile = await _access.BuildProfileAsync(id);
        return Ok(new
        {
            user,
            effectivePermissions = profile?.Permissions.OrderBy(p => p).ToList() ?? new List<string>(),
            workspaces = profile?.Workspaces ?? Array.Empty<string>()
        });
    }

    [HttpPost("users")]
    [HasPermission(Permissions.AdminUsers)]
    public async Task<IActionResult> CreateUser(CreateUserInput input)
    {
        var actor = await _access.GetRequiredCurrentAsync();
        var email = input.Email.Trim();
        if (await _users.FindByEmailAsync(email) != null)
            return Conflict(new { message = "A user with this email already exists." });

        var error = await ValidateAssignmentsAsync(actor, input);
        if (error != null) return BadRequest(new { message = error });

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FullName = input.FullName.Trim(),
            CseName = string.IsNullOrWhiteSpace(input.CseName) ? null : input.CseName.Trim(),
            IsActive = input.IsActive
        };

        var result = await _users.CreateAsync(user, input.Password);
        if (!result.Succeeded)
            return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });

        await ApplyAssignmentsAsync(user.Id, input);
        Audit(actor, "Created user", $"User #{user.Id} ({email}) created with {input.RoleIds.Count} role(s) and {input.TeamIds.Count} team(s).");
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetUser), new { id = user.Id }, new { user.Id });
    }

    [HttpPut("users/{id:int}")]
    [HasPermission(Permissions.AdminUsers)]
    public async Task<IActionResult> UpdateUser(int id, UserInput input)
    {
        var actor = await _access.GetRequiredCurrentAsync();
        var user = await _users.FindByIdAsync(id.ToString());
        if (user == null) return NotFound(new { message = "User not found." });

        var error = await ValidateAssignmentsAsync(actor, input);
        if (error != null) return BadRequest(new { message = error });

        if (id == actor.UserId && !input.IsActive)
            return BadRequest(new { message = "You cannot deactivate your own account." });

        var email = input.Email.Trim();
        var other = await _users.FindByEmailAsync(email);
        if (other != null && other.Id != id)
            return Conflict(new { message = "A user with this email already exists." });

        // Simulate the change and make sure an active Administrator remains,
        // and that the acting admin keeps the ability to manage users.
        var adminRoleId = await AdminRoleIdAsync();
        bool willBeAdmin = input.IsActive && input.RoleIds.Contains(adminRoleId);
        if (!willBeAdmin && !await OtherActiveAdminExistsAsync(id, adminRoleId))
            return BadRequest(new { message = "At least one active Administrator must remain." });

        if (id == actor.UserId)
        {
            var permissions = await PermissionsForAsync(input.RoleIds, input.Overrides);
            if (!permissions.Contains(Permissions.AdminUsers))
                return BadRequest(new { message = "You cannot remove your own user-administration permission." });
        }

        bool deactivated = user.IsActive && !input.IsActive;
        user.Email = email;
        user.UserName = email;
        user.FullName = input.FullName.Trim();
        user.CseName = string.IsNullOrWhiteSpace(input.CseName) ? null : input.CseName.Trim();
        user.IsActive = input.IsActive;

        var update = await _users.UpdateAsync(user);
        if (!update.Succeeded)
            return BadRequest(new { message = string.Join(" ", update.Errors.Select(e => e.Description)) });

        await ApplyAssignmentsAsync(id, input);
        // Changing the security stamp signs the user out everywhere on their next request.
        await _users.UpdateSecurityStampAsync(user);

        Audit(actor, deactivated ? "Deactivated user" : "Updated user",
            $"User #{id}: {input.RoleIds.Count} role(s), {input.TeamIds.Count} team(s), {input.Overrides.Count} override(s), active={input.IsActive}.");
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("users/{id:int}/status")]
    [HasPermission(Permissions.AdminUsers)]
    public async Task<IActionResult> SetStatus(int id, StatusInput input)
    {
        var actor = await _access.GetRequiredCurrentAsync();
        var user = await _users.FindByIdAsync(id.ToString());
        if (user == null) return NotFound(new { message = "User not found." });
        if (id == actor.UserId && !input.IsActive)
            return BadRequest(new { message = "You cannot deactivate your own account." });

        if (!input.IsActive)
        {
            var adminRoleId = await AdminRoleIdAsync();
            bool isAdmin = await _db.AppUserRoles.AnyAsync(ur => ur.UserId == id && ur.RoleId == adminRoleId);
            if (isAdmin && !await OtherActiveAdminExistsAsync(id, adminRoleId))
                return BadRequest(new { message = "At least one active Administrator must remain." });
        }

        user.IsActive = input.IsActive;
        await _users.UpdateAsync(user);
        await _users.UpdateSecurityStampAsync(user);
        Audit(actor, input.IsActive ? "Activated user" : "Deactivated user", $"User #{id}.");
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("users/{id:int}/reset-password")]
    [HasPermission(Permissions.AdminUsers)]
    public async Task<IActionResult> ResetPassword(int id, ResetPasswordInput input)
    {
        var actor = await _access.GetRequiredCurrentAsync();
        var user = await _users.FindByIdAsync(id.ToString());
        if (user == null) return NotFound(new { message = "User not found." });

        // Validate against the password policy before removing the old password.
        foreach (var validator in _users.PasswordValidators)
        {
            var check = await validator.ValidateAsync(_users, user, input.NewPassword);
            if (!check.Succeeded)
                return BadRequest(new { message = string.Join(" ", check.Errors.Select(e => e.Description)) });
        }

        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, input.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });

        await _users.SetLockoutEndDateAsync(user, null);
        await _users.ResetAccessFailedCountAsync(user);
        Audit(actor, "Reset user password", $"User #{id}.");
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<int> AdminRoleIdAsync() =>
        await _db.AppRoles.Where(r => r.Name == DefaultRoles.Administrator).Select(r => r.Id).FirstAsync();

    private async Task<bool> OtherActiveAdminExistsAsync(int excludingUserId, int adminRoleId) =>
        await _db.AppUserRoles.AnyAsync(ur =>
            ur.RoleId == adminRoleId && ur.UserId != excludingUserId && ur.User!.IsActive);

    private async Task<HashSet<string>> PermissionsForAsync(List<int> roleIds, List<OverrideInput> overrides)
    {
        var rolePermissions = await _db.RolePermissions.AsNoTracking()
            .Where(rp => roleIds.Contains(rp.RoleId) && rp.Role!.IsActive)
            .Select(rp => rp.Permission!.Code)
            .ToListAsync();
        return AccessService.CombinePermissions(rolePermissions, overrides.Select(o => (o.Permission, o.Effect)));
    }

    private async Task<string?> ValidateAssignmentsAsync(UserAccessProfile actor, UserInput input)
    {
        input.RoleIds = input.RoleIds.Distinct().ToList();
        input.TeamIds = input.TeamIds.Distinct().ToList();

        var roleCount = await _db.AppRoles.CountAsync(r => input.RoleIds.Contains(r.Id));
        if (roleCount != input.RoleIds.Count) return "One or more roles do not exist.";

        var teamCount = await _db.Teams.CountAsync(t => input.TeamIds.Contains(t.Id));
        if (teamCount != input.TeamIds.Count) return "One or more teams do not exist.";

        foreach (var o in input.Overrides)
        {
            if (!Permissions.AllCodes.Contains(o.Permission)) return $"Unknown permission '{o.Permission}'.";
            if (o.Effect != OverrideEffects.Grant && o.Effect != OverrideEffects.Deny)
                return "Override effect must be Grant or Deny.";
        }
        if (input.Overrides.GroupBy(o => o.Permission).Any(g => g.Count() > 1))
            return "Each permission can only have one override.";

        // No privilege escalation: the actor must hold every permission they hand out.
        var granted = await PermissionsForAsync(input.RoleIds, input.Overrides.Where(o => o.Effect == OverrideEffects.Grant).ToList());
        var missing = granted.Where(p => !actor.Has(p)).ToList();
        if (missing.Count > 0)
            return $"You cannot assign permissions you do not hold: {string.Join(", ", missing)}.";

        if (!string.IsNullOrWhiteSpace(input.CseName) && input.CseName.Length > 150)
            return "CSE name is too long.";

        return null;
    }

    private async Task ApplyAssignmentsAsync(int userId, UserInput input)
    {
        _db.AppUserRoles.RemoveRange(await _db.AppUserRoles.Where(r => r.UserId == userId).ToListAsync());
        _db.UserTeams.RemoveRange(await _db.UserTeams.Where(t => t.UserId == userId).ToListAsync());
        _db.UserPermissionOverrides.RemoveRange(await _db.UserPermissionOverrides.Where(o => o.UserId == userId).ToListAsync());
        await _db.SaveChangesAsync();

        foreach (var roleId in input.RoleIds) _db.AppUserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        foreach (var teamId in input.TeamIds) _db.UserTeams.Add(new UserTeam { UserId = userId, TeamId = teamId });

        var permissionIds = await _db.Permissions.ToDictionaryAsync(p => p.Code, p => p.Id);
        foreach (var o in input.Overrides)
            _db.UserPermissionOverrides.Add(new UserPermissionOverride
            {
                UserId = userId, PermissionId = permissionIds[o.Permission], Effect = o.Effect
            });

        await _db.SaveChangesAsync();
    }

    // ------------------------------------------------------------ teams

    [HttpGet("teams")]
    [HasPermission(Permissions.AdminTeams, Permissions.AdminUsers)]
    public async Task<IActionResult> GetTeams() =>
        Ok(await _db.Teams.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id, t.Name, t.WorkspaceType, t.Description, t.IsActive, t.CreatedAt,
                MemberCount = t.UserTeams.Count
            })
            .ToListAsync());

    [HttpPost("teams")]
    [HasPermission(Permissions.AdminTeams)]
    public async Task<IActionResult> CreateTeam(TeamInput input)
    {
        var actor = await _access.GetRequiredCurrentAsync();
        var error = await ValidateTeamAsync(input, null);
        if (error != null) return BadRequest(new { message = error });

        var team = new Team
        {
            Name = input.Name.Trim(), WorkspaceType = input.WorkspaceType,
            Description = input.Description?.Trim(), IsActive = input.IsActive
        };
        _db.Teams.Add(team);
        await _db.SaveChangesAsync();
        Audit(actor, "Created team", $"Team #{team.Id} '{team.Name}' ({team.WorkspaceType}).");
        await _db.SaveChangesAsync();
        return Ok(new { team.Id });
    }

    [HttpPut("teams/{id:int}")]
    [HasPermission(Permissions.AdminTeams)]
    public async Task<IActionResult> UpdateTeam(int id, TeamInput input)
    {
        var actor = await _access.GetRequiredCurrentAsync();
        var team = await _db.Teams.FindAsync(id);
        if (team == null) return NotFound(new { message = "Team not found." });

        var error = await ValidateTeamAsync(input, id);
        if (error != null) return BadRequest(new { message = error });

        team.Name = input.Name.Trim();
        team.WorkspaceType = input.WorkspaceType;
        team.Description = input.Description?.Trim();
        team.IsActive = input.IsActive;
        Audit(actor, "Updated team", $"Team #{id} '{team.Name}' ({team.WorkspaceType}), active={team.IsActive}.");
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<string?> ValidateTeamAsync(TeamInput input, int? id)
    {
        if (!WorkspaceTypes.All.Contains(input.WorkspaceType))
            return $"Workspace type must be one of: {string.Join(", ", WorkspaceTypes.All)}.";
        var name = input.Name.Trim();
        if (await _db.Teams.AnyAsync(t => t.Name == name && t.Id != id))
            return "A team with this name already exists.";
        return null;
    }

    // ------------------------------------------------------------ roles

    [HttpGet("roles")]
    [HasPermission(Permissions.AdminRoles, Permissions.AdminUsers)]
    public async Task<IActionResult> GetRoles() =>
        Ok(await _db.AppRoles.AsNoTracking().OrderBy(r => r.Name)
            .Select(r => new
            {
                r.Id, r.Name, r.Description, r.IsSystemRole, r.IsActive, r.CreatedAt,
                Locked = r.Name == DefaultRoles.Administrator,
                Permissions = r.RolePermissions.Select(rp => rp.Permission!.Code).OrderBy(c => c).ToList(),
                UserCount = r.UserRoles.Count
            })
            .ToListAsync());

    [HttpPost("roles")]
    [HasPermission(Permissions.AdminRoles)]
    public async Task<IActionResult> CreateRole(RoleInput input)
    {
        var actor = await _access.GetRequiredCurrentAsync();
        var error = await ValidateRoleAsync(actor, input, null);
        if (error != null) return BadRequest(new { message = error });

        var role = new AppRole { Name = input.Name.Trim(), Description = input.Description.Trim(), IsActive = input.IsActive };
        var ids = await _db.Permissions.Where(p => input.Permissions.Contains(p.Code)).Select(p => p.Id).ToListAsync();
        foreach (var pid in ids) role.RolePermissions.Add(new RolePermission { PermissionId = pid });
        _db.AppRoles.Add(role);
        await _db.SaveChangesAsync();

        Audit(actor, "Created role", $"Role #{role.Id} '{role.Name}' with {ids.Count} permission(s).");
        await _db.SaveChangesAsync();
        return Ok(new { role.Id });
    }

    [HttpPut("roles/{id:int}")]
    [HasPermission(Permissions.AdminRoles)]
    public async Task<IActionResult> UpdateRole(int id, RoleInput input)
    {
        var actor = await _access.GetRequiredCurrentAsync();
        var role = await _db.AppRoles.Include(r => r.RolePermissions).FirstOrDefaultAsync(r => r.Id == id);
        if (role == null) return NotFound(new { message = "Role not found." });
        if (role.Name == DefaultRoles.Administrator)
            return BadRequest(new { message = "The Administrator role is locked and always holds every permission." });

        var error = await ValidateRoleAsync(actor, input, id);
        if (error != null) return BadRequest(new { message = error });

        role.Name = input.Name.Trim();
        role.Description = input.Description.Trim();
        role.IsActive = input.IsActive;

        var ids = await _db.Permissions.Where(p => input.Permissions.Contains(p.Code)).Select(p => p.Id).ToListAsync();
        role.RolePermissions.RemoveAll(rp => !ids.Contains(rp.PermissionId));
        foreach (var pid in ids.Where(pid => role.RolePermissions.All(rp => rp.PermissionId != pid)))
            role.RolePermissions.Add(new RolePermission { RoleId = id, PermissionId = pid });

        Audit(actor, "Updated role", $"Role #{id} '{role.Name}': {ids.Count} permission(s), active={role.IsActive}.");
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("roles/{id:int}")]
    [HasPermission(Permissions.AdminRoles)]
    public async Task<IActionResult> DeleteRole(int id)
    {
        var actor = await _access.GetRequiredCurrentAsync();
        var role = await _db.AppRoles.FirstOrDefaultAsync(r => r.Id == id);
        if (role == null) return NotFound(new { message = "Role not found." });
        if (role.Name == DefaultRoles.Administrator)
            return BadRequest(new { message = "The Administrator role cannot be deleted." });
        if (await _db.AppUserRoles.AnyAsync(ur => ur.RoleId == id))
            return BadRequest(new { message = "Remove this role from all users before deleting it, or deactivate it instead." });

        _db.AppRoles.Remove(role);
        Audit(actor, "Deleted role", $"Role #{id} '{role.Name}'.");
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<string?> ValidateRoleAsync(UserAccessProfile actor, RoleInput input, int? id)
    {
        input.Permissions = input.Permissions.Distinct().ToList();
        var unknown = input.Permissions.Where(p => !Permissions.AllCodes.Contains(p)).ToList();
        if (unknown.Count > 0) return $"Unknown permission(s): {string.Join(", ", unknown)}.";

        var name = input.Name.Trim();
        if (string.Equals(name, DefaultRoles.Administrator, StringComparison.OrdinalIgnoreCase) && id == null)
            return "That role name is reserved.";
        if (await _db.AppRoles.AnyAsync(r => r.Name == name && r.Id != id))
            return "A role with this name already exists.";

        var missing = input.Permissions.Where(p => !actor.Has(p)).ToList();
        if (missing.Count > 0) return $"You cannot grant permissions you do not hold: {string.Join(", ", missing)}.";
        return null;
    }

    // ------------------------------------------------------------ settings

    [HttpGet("settings")]
    [HasPermission(Permissions.AdminSettings, Permissions.AdminUsers, Permissions.AdminRoles, Permissions.AdminTeams)]
    public async Task<IActionResult> GetSettings() =>
        Ok(new
        {
            slaThresholdWorkingDays = await _settings.GetSlaThresholdAsync(),
            minSla = SettingsService.MinSla,
            maxSla = SettingsService.MaxSla,
            aiAutomationMode = await _settings.GetAutomationModeAsync(),
            aiAutomationModes = AutomationModes.All
        });

    [HttpPut("settings")]
    [HasPermission(Permissions.AdminSettings)]
    public async Task<IActionResult> UpdateSettings(SettingsInput input)
    {
        var actor = await _access.GetRequiredCurrentAsync();
        var before = await _settings.GetSlaThresholdAsync();
        await _settings.SetSlaThresholdAsync(input.SlaThresholdWorkingDays, actor.UserId);
        if (before != input.SlaThresholdWorkingDays)
            Audit(actor, "Changed SLA threshold", $"{before} → {input.SlaThresholdWorkingDays} working days.");

        if (!string.IsNullOrWhiteSpace(input.AiAutomationMode))
        {
            if (!AutomationModes.All.Contains(input.AiAutomationMode))
                return BadRequest(new { message = $"AI automation mode must be one of: {string.Join(", ", AutomationModes.All)}." });
            var beforeMode = await _settings.GetAutomationModeAsync();
            await _settings.SetAutomationModeAsync(input.AiAutomationMode, actor.UserId);
            if (beforeMode != input.AiAutomationMode)
                Audit(actor, "Changed AI automation mode", $"{beforeMode} → {input.AiAutomationMode}.");
        }
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ------------------------------------------------------------ audit

    [HttpGet("audit")]
    [HasPermission(Permissions.AuditView)]
    public async Task<IActionResult> GetAudit([FromQuery] string? referenceId, [FromQuery] int take = 200)
    {
        take = Math.Clamp(take, 1, 1000);
        var q = _db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(referenceId)) q = q.Where(e => e.ReferenceId == referenceId);
        var events = await q.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(take).ToListAsync();
        return Ok(events.Select(AuditEventDto.From));
    }

    [HttpGet("ai-usage")]
    [HasPermission(Permissions.AuditView)]
    public async Task<IActionResult> GetAiUsage([FromQuery] int take = 200)
    {
        take = Math.Clamp(take, 1, 1000);
        var logs = await _db.AiInteractionLogs.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt).Take(take).ToListAsync();
        var userIds = logs.Select(l => l.UserId).Distinct().ToList();
        var userNames = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);
        return Ok(logs.Select(l => new
        {
            l.Id, l.Feature, l.ReferenceId, l.Provider, l.Model, l.Success, l.Error, l.DurationMs, l.Accepted, l.CreatedAt,
            UserName = userNames.GetValueOrDefault(l.UserId, $"User #{l.UserId}")
        }));
    }

    private void Audit(UserAccessProfile actor, string action, string details) =>
        _db.AuditEvents.Add(new AuditEvent
        {
            UserId = actor.UserId,
            UserName = actor.DisplayName,
            Team = WorkspaceTypes.Admin,
            Action = action,
            Details = details.Length > 1000 ? details[..1000] : details
        });
}
