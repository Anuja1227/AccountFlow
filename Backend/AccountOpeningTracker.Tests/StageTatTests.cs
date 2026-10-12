using AccountOpeningTracker.Api.Models;

namespace AccountOpeningTracker.Tests;

/// <summary>Stage 1–6 TAT and stage-wise SLA (requirements: auto-calculated fields).</summary>
public class StageTatTests
{
    private static readonly DateTime Mon = Make.Monday; // 2026-10-05

    private static StageTat Get(List<StageTat> list, int stage) => list.Single(t => t.Stage == $"Stage {stage}");

    [Fact]
    public void Completed_case_measures_each_stage_and_stages_1_to_5_add_up_to_gross_tat()
    {
        var a = Make.Record(1);
        a.FormPreparedDate = Mon.AddDays(2);            // Wed
        a.PhysicalFormSubmittedToCseDate = Mon.AddDays(2);
        a.SignedFormReceivedDate = Mon.AddDays(7);      // next Mon
        a.SubmittedToMofslDate = Mon.AddDays(9);        // Wed
        a.AccountOpeningDate = Mon.AddDays(11);         // Fri
        a.CommunicationSentDate = Mon.AddDays(14);      // Mon
        a.LatestStatus = WorkflowStatuses.CaseClosed;

        var tats = StageTatHelper.Calculate(new[] { a }, Mon.AddDays(20), 7);

        Assert.Equal(2, Get(tats, 1).Days);
        Assert.Equal(3, Get(tats, 2).Days);
        Assert.Equal(2, Get(tats, 3).Days);
        Assert.Null(Get(tats, 4).Days);                 // no discrepancy: Stage 4 not needed
        Assert.Equal(2, Get(tats, 5).Days);
        Assert.Equal(1, Get(tats, 6).Days);
        Assert.All(tats, t => Assert.False(t.InProgress));
        Assert.All(tats, t => Assert.False(t.IsSlaBreached));

        TatHelper.CalculateTat(a, Mon, Mon.AddDays(20), 7);
        Assert.Equal(a.GrossTatDays, tats.Take(5).Sum(t => t.Days ?? 0));
    }

    [Fact]
    public void Current_stage_runs_to_today_and_flags_a_stage_wise_breach()
    {
        var a = Make.Record(1);
        a.FormPreparedDate = Mon.AddDays(1);            // Tue
        a.LatestStatus = WorkflowStatuses.PhysicalFormSubmitted;

        var tats = StageTatHelper.Calculate(new[] { a }, Mon.AddDays(14), 7); // Mon two weeks later

        Assert.Equal(1, Get(tats, 1).Days);
        Assert.False(Get(tats, 1).IsSlaBreached);
        var s2 = Get(tats, 2);
        Assert.True(s2.InProgress);
        Assert.Equal(9, s2.Days);
        Assert.True(s2.IsSlaBreached);
        Assert.Null(Get(tats, 3).Days);
    }

    [Fact]
    public void Stage_4_comes_from_the_resubmission_record_of_the_same_reference()
    {
        var first = Make.Record(1);
        first.FormPreparedDate = Mon.AddDays(1);
        first.SignedFormReceivedDate = Mon.AddDays(3);  // Thu
        first.DiscrepancyRaisedDate = Mon.AddDays(4);   // Fri

        var second = Make.Record(2, inward: Mon.AddDays(9)); // Wed
        second.EntryType = "Discrepancy Resolution";
        second.DiscrepancyResolutionReceivedDate = Mon.AddDays(9);
        second.SubmittedToMofslDate = Mon.AddDays(11);  // Fri
        second.LatestStatus = WorkflowStatuses.SubmittedToMofsl;

        var tats = StageTatHelper.Calculate(new[] { first, second }, Mon.AddDays(11), 7);

        Assert.Equal(4, Get(tats, 3).Days);             // Thu → next Wed
        Assert.Equal(2, Get(tats, 4).Days);             // Wed → Fri
        Assert.True(Get(tats, 5).InProgress);
        Assert.Equal(0, Get(tats, 5).Days);
    }

    [Fact]
    public void Stage_threshold_is_the_configurable_sla()
    {
        var a = Make.Record(1);
        a.FormPreparedDate = Mon.AddDays(7);            // 5 working days in Stage 1
        a.LatestStatus = WorkflowStatuses.PhysicalFormSubmitted;

        Assert.False(Get(StageTatHelper.Calculate(new[] { a }, Mon.AddDays(7), 7), 1).IsSlaBreached);
        Assert.True(Get(StageTatHelper.Calculate(new[] { a }, Mon.AddDays(7), 4), 1).IsSlaBreached);
    }

    [Fact]
    public void Mis_pipeline_reports_average_days_in_stage_and_stage_wise_breaches()
    {
        var slow = Make.Record(1, "REF-1");
        slow.FormPreparedDate = Mon.AddDays(14);        // 10 working days in Stage 1
        slow.LatestStatus = WorkflowStatuses.PhysicalFormSubmitted;

        var fast = Make.Record(2, "REF-2");
        fast.FormPreparedDate = Mon.AddDays(9);         // 7 working days in Stage 1 (at the SLA, not over)
        fast.LatestStatus = WorkflowStatuses.PhysicalFormSubmitted;

        var records = new List<Account> { slow, fast };
        CaseCalculator.RefreshForRead(records, Mon.AddDays(14), 7);
        var report = MisCalculator.Build(records, new MisFilters(null, null, null), 7);

        var stage1 = report.StagePipeline.Single(s => s.Stage == "Stage 1");
        Assert.Equal(2, stage1.CasesMeasured);
        Assert.Equal(8.5, stage1.AverageStageTatDays);
        Assert.Equal(1, stage1.StageSlaBreachCases);
        Assert.Equal(1, report.Summary.StageSlaBreachCases);
    }
}

public class CurrentStageTests
{
    [Fact]
    public void Current_stage_is_derived_from_stage_dates()
    {
        var a = Make.Record(1);
        a.SignedFormReceivedDate = Make.Monday.AddDays(3);
        Assert.Equal("Stage 3", WorkflowHelper.DetermineCurrentStage(a));
    }

    [Fact]
    public void A_manual_override_wins_until_cleared()
    {
        var a = Make.Record(1);
        a.SignedFormReceivedDate = Make.Monday.AddDays(3);
        a.CurrentStage = "Stage 2";
        a.IsCurrentStageManuallyOverridden = true;
        Assert.Equal("Stage 2", WorkflowHelper.DetermineCurrentStage(a));

        a.IsCurrentStageManuallyOverridden = false;
        Assert.Equal("Stage 3", WorkflowHelper.DetermineCurrentStage(a));
    }
}
