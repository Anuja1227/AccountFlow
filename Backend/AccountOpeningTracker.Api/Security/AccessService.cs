using System.Security.Claims;
using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Security;

public sealed record TeamInfo(int Id, string Name, string WorkspaceType);

/// <summary>
/// Effective access for one user, computed from the database on each
/// request: active roles' permissions, plus per-user overrides
/// (deny wins), plus active team memberships.
/// </summary>
public sealed class UserAccessProfile
{
    public int UserId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string? CseName { get; init; }
    public bool IsActive { get; init; }
    public IReadOnlySet<string> Permissions { get; init; } = new HashSet<string>();
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<TeamInfo> Teams { get; init; } = Array.Empty<TeamInfo>();

    public bool Has(string permission) => IsActive && Permissions.Contains(permission);

    public bool HasAny(params string[] permissions) => permissions.Any(Has);

    public bool InWorkspace(string workspaceType) =>
        IsActive && Teams.Any(t => t.WorkspaceType == workspaceType);

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Email : FullName;

    /// <summary>Workspaces the user can open in the UI.</summary>
    public IReadOnlyList<string> Workspaces
    {
        get
        {
            var list = new List<string>();
            foreach (var ws in WorkspaceTypes.WorkflowTeams)
                if (InWorkspace(ws)) list.Add(ws);
            if (Has(Models.Permissions.ReportsView)) list.Add(WorkspaceTypes.Mis);
            if (HasAny(Models.Permissions.AdminPermissions) || Has(Models.Permissions.AuditView))
                list.Add(WorkspaceTypes.Admin);
            return list;
        }
    }
}

public interface IAccessService
{
    /// <summary>Profile of the signed-in user, cached for the request. Null if anonymous.</summary>
    Task<UserAccessProfile?> GetCurrentAsync();

    Task<UserAccessProfile> GetRequiredCurrentAsync();

    Task<UserAccessProfile?> BuildProfileAsync(int userId);
}

public sealed class AccessService : IAccessService
{
    private readonly ApplicationDbContext _db;
    private readonly IHttpContextAccessor _http;
    private UserAccessProfile? _current;
    private bool _loaded;

    public AccessService(ApplicationDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    public async Task<UserAccessProfile?> GetCurrentAsync()
    {
        if (_loaded) return _current;

        var principal = _http.HttpContext?.User;
        var idValue = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal?.Identity?.IsAuthenticated == true &&
            int.TryParse(idValue, out var userId))
        {
            _current = await BuildProfileAsync(userId);
        }

        _loaded = true;
        return _current;
    }

    public async Task<UserAccessProfile> GetRequiredCurrentAsync() =>
        await GetCurrentAsync() ??
        throw new UnauthorizedAccessException("Not signed in.");

    public async Task<UserAccessProfile?> BuildProfileAsync(int userId)
    {
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Email, u.FullName, u.CseName, u.IsActive })
            .FirstOrDefaultAsync();

        if (user == null) return null;

        var roles = await _db.AppUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && ur.Role!.IsActive)
            .Select(ur => new { ur.RoleId, ur.Role!.Name })
            .ToListAsync();

        var roleIds = roles.Select(r => r.RoleId).ToList();

        var rolePermissions = await _db.RolePermissions.AsNoTracking()
            .Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission!.Code)
            .Distinct()
            .ToListAsync();

        var overrides = await _db.UserPermissionOverrides.AsNoTracking()
            .Where(o => o.UserId == userId)
            .Select(o => new { o.Permission!.Code, o.Effect })
            .ToListAsync();

        var effective = CombinePermissions(
            rolePermissions, overrides.Select(o => (o.Code, o.Effect)));

        var teams = await _db.UserTeams.AsNoTracking()
            .Where(ut => ut.UserId == userId && ut.Team!.IsActive)
            .Select(ut => new TeamInfo(ut.TeamId, ut.Team!.Name, ut.Team.WorkspaceType))
            .ToListAsync();

        return new UserAccessProfile
        {
            UserId = user.Id,
            Email = user.Email ?? string.Empty,
            FullName = user.FullName,
            CseName = user.CseName,
            IsActive = user.IsActive,
            Permissions = effective,
            Roles = roles.Select(r => r.Name).OrderBy(n => n).ToList(),
            Teams = teams.OrderBy(t => t.Name).ToList()
        };
    }

    /// <summary>
    /// Pure helper used by tests: combine role permissions and overrides.
    /// </summary>
    internal static HashSet<string> CombinePermissions(
        IEnumerable<string> rolePermissions,
        IEnumerable<(string Code, string Effect)> overrides)
    {
        var effective = new HashSet<string>(rolePermissions, StringComparer.Ordinal);
        var list = overrides.ToList();
        foreach (var o in list.Where(o => o.Effect == OverrideEffects.Grant)) effective.Add(o.Code);
        foreach (var o in list.Where(o => o.Effect == OverrideEffects.Deny)) effective.Remove(o.Code);
        return effective;
    }
}
