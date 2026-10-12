namespace AccountOpeningTracker.Api.Models;

/// <summary>
/// Workspace types a team can belong to. A team's workspace type decides
/// which workflow responsibilities its members carry.
/// </summary>
public static class WorkspaceTypes
{
    public const string Operations = "Operations";
    public const string Cse = "CSE";
    public const string Mofsl = "MOFSL";
    public const string Mis = "MIS";
    public const string Admin = "Admin";

    public static readonly string[] All = { Operations, Cse, Mofsl, Mis, Admin };

    /// <summary>Workspaces that own workflow actions on cases.</summary>
    public static readonly string[] WorkflowTeams = { Operations, Cse, Mofsl };
}

public class Team
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string WorkspaceType { get; set; } = WorkspaceTypes.Operations;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<UserTeam> UserTeams { get; set; } = new();
}

public class AppRole
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>System roles are seeded; they cannot be deleted.</summary>
    public bool IsSystemRole { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<RolePermission> RolePermissions { get; set; } = new();
    public List<UserRole> UserRoles { get; set; } = new();
}

public class Permission
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class RolePermission
{
    public int RoleId { get; set; }
    public AppRole? Role { get; set; }
    public int PermissionId { get; set; }
    public Permission? Permission { get; set; }
}

public class UserRole
{
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int RoleId { get; set; }
    public AppRole? Role { get; set; }
}

public class UserTeam
{
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int TeamId { get; set; }
    public Team? Team { get; set; }
}

public static class OverrideEffects
{
    public const string Grant = "Grant";
    public const string Deny = "Deny";
}

/// <summary>
/// Per-user exception to role permissions. Deny always wins over a grant.
/// </summary>
public class UserPermissionOverride
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int PermissionId { get; set; }
    public Permission? Permission { get; set; }
    public string Effect { get; set; } = OverrideEffects.Grant;
}

public class SystemSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? UpdatedByUserId { get; set; }
}

public static class SettingKeys
{
    public const string SlaThresholdWorkingDays = "Sla.DefaultThresholdWorkingDays";
    public const string AiAutomationMode = "Ai.AutomationMode";
}
