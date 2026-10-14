namespace AccountOpeningTracker.Api.Models;

/// <param name="Team">Operations, CSE or MOFSL (the case's current Action By).</param>
/// <param name="PendingCases">Open cases whose latest record is with this team.</param>
/// <param name="OverSlaCases">Of those, cases beyond the overall SLA.</param>
/// <param name="AverageAgingDays">Average current aging (working days since the original inward date) of those cases.</param>
/// <param name="OldestReferenceId">The pending case with the highest aging.</param>
/// <param name="QueriesReceived">Queries addressed to this team.</param>
/// <param name="OpenQueries">Queries addressed to this team still waiting for its reply.</param>
/// <param name="QueriesRaised">Queries this team raised.</param>
/// <param name="AverageReplyHours">Average time from a query being raised to this team's first reply.</param>
public sealed record TeamPending(
    string Team,
    int PendingCases,
    int OverSlaCases,
    double AverageAgingDays,
    string? OldestReferenceId,
    int OldestAgingDays,
    int QueriesReceived,
    int OpenQueries,
    int QueriesRaised,
    double? AverageReplyHours);

/// <summary>
/// Rework attribution from the requirements document: RFT "attributes rework
/// to its true source (CSE-side vs. MOFSL-side)".
/// CSE-side: Stage 1 query or Stage 3 discrepancy / return. MOFSL-side: Stage 5 MOFSL query.
/// </summary>
public sealed record ReworkAttribution(
    int TotalCases,
    int RftCases,
    int NrftCases,
    int CseSideOnlyCases,
    int MofslSideOnlyCases,
    int BothSidesCases);

public sealed record OverviewReport(
    MisFilters Filters,
    int SlaThresholdWorkingDays,
    MisMetrics Summary,
    List<TeamPending> Teams,
    ReworkAttribution Rework,
    List<MisStage> StagePipeline);

/// <summary>
/// Overall Summary Dashboard ("Organization-wide KPIs for management reporting").
/// Reuses the MIS arithmetic for the KPIs and the stage pipeline, and adds
/// who each open case is waiting on. A case is a Reference ID; its latest
/// record decides the current owner, so resubmissions are not double counted.
/// </summary>
public static class OverviewCalculator
{
    public static readonly string[] Teams = { WorkspaceTypes.Operations, WorkspaceTypes.Cse, WorkspaceTypes.Mofsl };

    public static OverviewReport Build(
        IReadOnlyCollection<Account> records,
        IReadOnlyCollection<CaseQuery> queries,
        MisFilters filters,
        int slaThreshold)
    {
        var mis = MisCalculator.Build(records, filters, slaThreshold);

        var filtered = records.Where(a =>
                (string.IsNullOrWhiteSpace(filters.Cse) || a.Cse == filters.Cse) &&
                (string.IsNullOrWhiteSpace(filters.Location) || a.Location == filters.Location) &&
                (string.IsNullOrWhiteSpace(filters.Segment) || a.Segment == filters.Segment))
            .ToList();
        var references = filtered.Select(a => a.ReferenceId).ToHashSet();
        var caseQueries = queries.Where(q => references.Contains(q.ReferenceId)).ToList();

        var open = CaseCalculator.LatestPerCase(filtered).Where(WorkflowStatuses.IsOpen).ToList();

        var teams = Teams.Select(team =>
        {
            var pending = open.Where(a => string.Equals(a.ActionBy, team, StringComparison.OrdinalIgnoreCase)).ToList();
            var oldest = pending.OrderByDescending(a => a.CurrentAgingDays).ThenBy(a => a.InwardDate).FirstOrDefault();
            var received = caseQueries.Where(q => q.ToTeam == team).ToList();
            var replied = received.Where(q => q.RespondedAt.HasValue).ToList();

            return new TeamPending(
                team,
                pending.Count,
                pending.Count(a => a.IsSlaBreached),
                pending.Count == 0 ? 0 : Math.Round(pending.Average(a => (double)a.CurrentAgingDays), 1),
                oldest?.ReferenceId,
                oldest?.CurrentAgingDays ?? 0,
                received.Count,
                received.Count(q => q.Status == QueryStatuses.Open),
                caseQueries.Count(q => q.FromTeam == team),
                replied.Count == 0
                    ? null
                    : Math.Round(replied.Average(q => Math.Max(0, (q.RespondedAt!.Value - q.RaisedAt).TotalHours)), 1));
        }).ToList();

        return new OverviewReport(filters, slaThreshold, mis.Summary, teams, Rework(filtered), mis.StagePipeline);
    }

    public static ReworkAttribution Rework(IReadOnlyCollection<Account> rows)
    {
        var cases = rows.GroupBy(a => a.ReferenceId).ToList();
        bool Cse(IEnumerable<Account> g) => g.Any(a =>
            a.Stage1QueryRaisedDate.HasValue || a.DiscrepancyRaisedDate.HasValue || a.FormReturnedToCseDate.HasValue);
        bool Mofsl(IEnumerable<Account> g) => g.Any(a => a.MofslQueryRaisedDate.HasValue);

        int nrft = cases.Count(g => RftHelper.DetermineRftStatus(g) == "NRFT");
        return new ReworkAttribution(
            cases.Count,
            cases.Count - nrft,
            nrft,
            cases.Count(g => Cse(g) && !Mofsl(g)),
            cases.Count(g => Mofsl(g) && !Cse(g)),
            cases.Count(g => Cse(g) && Mofsl(g)));
    }
}
