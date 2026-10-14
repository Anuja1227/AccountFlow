using AccountOpeningTracker.Api.Models;

namespace AccountOpeningTracker.Tests;

public class OverviewCalculatorTests
{
    private static readonly DateTime Friday = Make.Monday.AddDays(4);

    private static List<Account> Sample()
    {
        // C1: opened (closed) - not pending with anyone.
        var c1 = Make.Record(1, "C1");
        c1.AccountOpeningDate = Make.Monday.AddDays(2);
        c1.LatestStatus = WorkflowStatuses.AccountOpened;

        // C2: two records; the older one was with CSE, the latest is with Operations.
        var c2a = Make.Record(2, "C2");
        c2a.ActionBy = WorkspaceTypes.Cse;
        c2a.Stage1QueryRaisedDate = Make.Monday;
        var c2b = Make.Record(3, "C2", Make.Monday.AddDays(1));
        c2b.EntryType = "Resubmission";
        c2b.ActionBy = WorkspaceTypes.Operations;

        // C3: with CSE, Stage 3 discrepancy and a MOFSL query (both sides).
        var c3 = Make.Record(4, "C3");
        c3.ActionBy = WorkspaceTypes.Cse;
        c3.DiscrepancyRaisedDate = Make.Monday.AddDays(1);
        c3.MofslQueryRaisedDate = Make.Monday.AddDays(2);

        // C4: with MOFSL, different location, MOFSL query only.
        var c4 = Make.Record(5, "C4", Make.Monday.AddDays(3));
        c4.ActionBy = WorkspaceTypes.Mofsl;
        c4.MofslQueryRaisedDate = Make.Monday.AddDays(3);
        c4.Location = "Pune";

        var all = new List<Account> { c1, c2a, c2b, c3, c4 };
        foreach (var g in all.GroupBy(a => a.ReferenceId))
            CaseCalculator.Recalculate(g.ToList(), Friday, 7);
        return all;
    }

    private static List<CaseQuery> Queries() => new()
    {
        // To CSE: answered after 6 hours.
        new() { ReferenceId = "C2", FromTeam = WorkspaceTypes.Operations, ToTeam = WorkspaceTypes.Cse, Status = QueryStatuses.Resolved,
                RaisedAt = Make.Monday.AddHours(10), RespondedAt = Make.Monday.AddHours(16) },
        // To CSE: still waiting.
        new() { ReferenceId = "C3", FromTeam = WorkspaceTypes.Operations, ToTeam = WorkspaceTypes.Cse, Status = QueryStatuses.Open,
                RaisedAt = Make.Monday.AddDays(1) },
        // MOFSL to Operations: answered after 2 hours.
        new() { ReferenceId = "C4", FromTeam = WorkspaceTypes.Mofsl, ToTeam = WorkspaceTypes.Operations, Status = QueryStatuses.Responded,
                RaisedAt = Make.Monday.AddDays(3).AddHours(9), RespondedAt = Make.Monday.AddDays(3).AddHours(11) },
        // A query on a case outside the filter (see the filter test).
        new() { ReferenceId = "OTHER", FromTeam = WorkspaceTypes.Operations, ToTeam = WorkspaceTypes.Cse, Status = QueryStatuses.Open,
                RaisedAt = Make.Monday },
    };

    private static TeamPending Team(OverviewReport r, string team) => r.Teams.Single(t => t.Team == team);

    [Fact]
    public void Open_cases_are_counted_once_under_the_latest_records_owner()
    {
        var report = OverviewCalculator.Build(Sample(), Queries(), new MisFilters(null, null, null), 7);

        Assert.Equal(new[] { "Operations", "CSE", "MOFSL" }, report.Teams.Select(t => t.Team));
        Assert.Equal(1, Team(report, "Operations").PendingCases); // C2 (latest record), not C1 (opened)
        Assert.Equal(1, Team(report, "CSE").PendingCases);        // C3 only; C2's older CSE record is ignored
        Assert.Equal(1, Team(report, "MOFSL").PendingCases);      // C4
        Assert.Equal(3, report.Teams.Sum(t => t.PendingCases));
        Assert.Equal(report.Summary.OpenCases, report.Teams.Sum(t => t.PendingCases));
    }

    [Fact]
    public void Oldest_pending_case_and_aging_come_from_current_aging()
    {
        var report = OverviewCalculator.Build(Sample(), Queries(), new MisFilters(null, null, null), 7);

        var cse = Team(report, "CSE");
        Assert.Equal("C3", cse.OldestReferenceId);
        Assert.Equal(4, cse.OldestAgingDays); // Monday -> Friday
        Assert.Equal(4, cse.AverageAgingDays);
        Assert.Equal(0, cse.OverSlaCases);

        var mofsl = Team(report, "MOFSL");
        Assert.Equal(1, mofsl.OldestAgingDays); // Thursday -> Friday
    }

    [Fact]
    public void Over_sla_counts_pending_cases_beyond_the_threshold()
    {
        var all = Sample();
        foreach (var g in all.GroupBy(a => a.ReferenceId))
            CaseCalculator.Recalculate(g.ToList(), Friday, 2); // SLA of 2 working days

        var report = OverviewCalculator.Build(all, Queries(), new MisFilters(null, null, null), 2);

        Assert.Equal(1, Team(report, "CSE").OverSlaCases);   // C3: 4 days
        Assert.Equal(1, Team(report, "Operations").OverSlaCases); // C2: 4 days from the original inward date
        Assert.Equal(0, Team(report, "MOFSL").OverSlaCases); // C4: 1 day
    }

    [Fact]
    public void Queries_are_counted_by_recipient_and_sender_with_reply_time()
    {
        var report = OverviewCalculator.Build(Sample(), Queries(), new MisFilters(null, null, null), 7);

        var cse = Team(report, "CSE");
        Assert.Equal(2, cse.QueriesReceived); // the OTHER case is not in the records, so not counted
        Assert.Equal(1, cse.OpenQueries);
        Assert.Equal(0, cse.QueriesRaised);
        Assert.Equal(6.0, cse.AverageReplyHours!.Value);

        var ops = Team(report, "Operations");
        Assert.Equal(1, ops.QueriesReceived);
        Assert.Equal(0, ops.OpenQueries); // Responded: waiting on MOFSL, not on Operations
        Assert.Equal(2, ops.QueriesRaised);
        Assert.Equal(2.0, ops.AverageReplyHours!.Value);

        var mofsl = Team(report, "MOFSL");
        Assert.Equal(0, mofsl.QueriesReceived);
        Assert.Null(mofsl.AverageReplyHours);
        Assert.Equal(1, mofsl.QueriesRaised);
    }

    [Fact]
    public void Rework_is_attributed_cse_side_mofsl_side_or_both()
    {
        var rework = OverviewCalculator.Build(Sample(), Queries(), new MisFilters(null, null, null), 7).Rework;

        Assert.Equal(4, rework.TotalCases);
        Assert.Equal(1, rework.RftCases);          // C1
        Assert.Equal(3, rework.NrftCases);
        Assert.Equal(1, rework.CseSideOnlyCases);  // C2 (Stage 1)
        Assert.Equal(1, rework.MofslSideOnlyCases); // C4
        Assert.Equal(1, rework.BothSidesCases);    // C3
        Assert.Equal(rework.NrftCases, rework.CseSideOnlyCases + rework.MofslSideOnlyCases + rework.BothSidesCases);
    }

    [Fact]
    public void Filters_limit_cases_and_their_queries()
    {
        var report = OverviewCalculator.Build(Sample(), Queries(), new MisFilters(null, "Pune", null), 7);

        Assert.Equal(1, report.Summary.TotalCases);
        Assert.Equal(1, Team(report, "MOFSL").PendingCases);
        Assert.Equal(0, Team(report, "CSE").PendingCases);
        Assert.Equal(0, Team(report, "CSE").QueriesReceived);
        Assert.Equal(1, Team(report, "Operations").QueriesReceived);
    }
}
