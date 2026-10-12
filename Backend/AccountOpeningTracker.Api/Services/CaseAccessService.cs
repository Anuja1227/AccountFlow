using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Services;

/// <summary>
/// Case-level access. Applied to every case/record query on the server so
/// users only ever load cases they are allowed to see:
///  - cases.view.all      → every case
///  - cases.view.assigned → cases whose CSE equals the user's CSE name
///  - cases.view.mofsl    → cases with any record submitted to MOFSL
/// Query threads are further limited to the teams involved in them.
/// </summary>
public sealed class CaseAccessService
{
    private readonly ApplicationDbContext _db;

    public CaseAccessService(ApplicationDbContext db) => _db = db;

    public IQueryable<Account> ScopeRecords(IQueryable<Account> source, UserAccessProfile user)
    {
        if (user.Has(Permissions.CasesViewAll)) return source;

        bool assigned = user.Has(Permissions.CasesViewAssigned) && !string.IsNullOrWhiteSpace(user.CseName);
        bool mofsl = user.Has(Permissions.CasesViewMofsl);
        string cse = user.CseName?.Trim() ?? string.Empty;

        if (!assigned && !mofsl) return source.Where(_ => false);

        var all = _db.Accounts;
        return source.Where(a =>
            (assigned && all.Any(b => b.ReferenceId == a.ReferenceId && b.Cse == cse)) ||
            (mofsl && all.Any(b => b.ReferenceId == a.ReferenceId && b.SubmittedToMofslDate != null)));
    }

    public async Task<bool> CanViewCaseAsync(string referenceId, UserAccessProfile user) =>
        await ScopeRecords(_db.Accounts.AsNoTracking(), user)
            .AnyAsync(a => a.ReferenceId == referenceId);

    /// <summary>Which query threads the user may read.</summary>
    public static bool CanSeeQuery(CaseQuery q, UserAccessProfile user)
    {
        if (user.Has(Permissions.CasesViewAll)) return true;
        return user.Workspaces.Contains(q.FromTeam) || user.Workspaces.Contains(q.ToTeam);
    }

    public IQueryable<CaseQuery> ScopeQueries(IQueryable<CaseQuery> source, UserAccessProfile user)
    {
        var visibleRefs = ScopeRecords(_db.Accounts.AsNoTracking(), user).Select(a => a.ReferenceId);
        var scoped = source.Where(q => visibleRefs.Contains(q.ReferenceId));
        if (user.Has(Permissions.CasesViewAll)) return scoped;

        var teams = user.Workspaces.ToList();
        return scoped.Where(q => teams.Contains(q.FromTeam) || teams.Contains(q.ToTeam));
    }
}
