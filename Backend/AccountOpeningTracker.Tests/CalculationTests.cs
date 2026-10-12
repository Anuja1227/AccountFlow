using AccountOpeningTracker.Api.Models;

namespace AccountOpeningTracker.Tests;

internal static class Make
{
    // 2026-10-05 is a Monday.
    public static readonly DateTime Monday = new(2026, 10, 5);

    public static Account Record(int id, string reference = "REF-1", DateTime? inward = null) => new()
    {
        Id = id,
        ReferenceId = reference,
        RequestId = $"REQ-{id}",
        ClientName = "Asha Verma",
        PanNo = "ABCDE1234F",
        Channel = "Physical",
        InwardDate = inward ?? Monday,
        Cse = "CSE One",
        Location = "Mumbai",
        Segment = "Retail",
    };
}

public class WorkingDaysTests
{
    [Fact]
    public void Monday_to_Friday_is_four_working_days() =>
        Assert.Equal(4, WorkingDaysHelper.CountWorkingDays(Make.Monday, Make.Monday.AddDays(4)));

    [Fact]
    public void Friday_to_next_Monday_skips_the_weekend() =>
        Assert.Equal(1, WorkingDaysHelper.CountWorkingDays(Make.Monday.AddDays(4), Make.Monday.AddDays(7)));

    [Fact]
    public void Same_day_is_zero() =>
        Assert.Equal(0, WorkingDaysHelper.CountWorkingDays(Make.Monday, Make.Monday));

    [Fact]
    public void Weekend_only_span_is_zero() =>
        Assert.Equal(0, WorkingDaysHelper.CountWorkingDays(Make.Monday.AddDays(4), Make.Monday.AddDays(6)));
}

public class TatAndSlaTests
{
    [Fact]
    public void Open_case_measures_gross_tat_and_aging_to_today_and_subtracts_hold_days()
    {
        var a = Make.Record(1);
        a.QueryHoldDays = 2;
        TatHelper.CalculateTat(a, Make.Monday, Make.Monday.AddDays(7), 7); // next Monday

        Assert.Equal(5, a.GrossTatDays);
        Assert.Equal(3, a.NetTatDays);
        Assert.Equal(5, a.CurrentAgingDays);
        Assert.False(a.IsSlaBreached);
    }

    [Fact]
    public void Completed_case_stops_at_account_opening_date_and_has_no_aging()
    {
        var a = Make.Record(1);
        a.AccountOpeningDate = Make.Monday.AddDays(3);
        TatHelper.CalculateTat(a, Make.Monday, Make.Monday.AddDays(30), 7);

        Assert.Equal(3, a.GrossTatDays);
        Assert.Equal(0, a.CurrentAgingDays);
    }

    [Theory]
    [InlineData(9, 7, false)]  // Mon → next Wed = 7 working days: exactly at SLA, not breached
    [InlineData(10, 8, true)]  // Mon → next Thu = 8 working days: breached
    public void Sla_breaches_only_when_gross_tat_exceeds_threshold(int calendarDays, int expectedGross, bool breached)
    {
        var a = Make.Record(1);
        TatHelper.CalculateTat(a, Make.Monday, Make.Monday.AddDays(calendarDays), 7);
        Assert.Equal(expectedGross, a.GrossTatDays);
        Assert.Equal(breached, a.IsSlaBreached);
    }

    [Fact]
    public void Net_tat_never_goes_negative()
    {
        var a = Make.Record(1);
        a.QueryHoldDays = 50;
        TatHelper.CalculateTat(a, Make.Monday, Make.Monday.AddDays(2), 7);
        Assert.Equal(0, a.NetTatDays);
    }
}

public class RftTests
{
    [Fact]
    public void Case_without_queries_is_rft() =>
        Assert.Equal("RFT", RftHelper.DetermineRftStatus(new[] { Make.Record(1) }));

    [Theory]
    [InlineData("stage1")]
    [InlineData("discrepancy")]
    [InlineData("returned")]
    [InlineData("mofsl")]
    public void Any_qualifying_event_on_any_record_makes_the_case_nrft(string kind)
    {
        var first = Make.Record(1);
        var second = Make.Record(2, inward: Make.Monday.AddDays(2));
        var d = Make.Monday.AddDays(1);
        switch (kind)
        {
            case "stage1": first.Stage1QueryRaisedDate = d; break;
            case "discrepancy": first.DiscrepancyRaisedDate = d; break;
            case "returned": first.FormReturnedToCseDate = d; break;
            case "mofsl": second.MofslQueryRaisedDate = d; break;
        }
        Assert.Equal("NRFT", RftHelper.DetermineRftStatus(new[] { first, second }));
    }
}

public class CaseCalculatorTests
{
    [Fact]
    public void Original_inward_is_the_earliest_across_records_and_never_resets()
    {
        var original = Make.Record(1, inward: Make.Monday);
        var resubmission = Make.Record(2, inward: Make.Monday.AddDays(3));
        resubmission.EntryType = "Resubmission";
        var records = new List<Account> { original, resubmission };

        CaseCalculator.Recalculate(records, Make.Monday.AddDays(4), 7);

        Assert.All(records, r => Assert.Equal(Make.Monday, r.OriginalInwardDate));
        Assert.Equal(4, resubmission.GrossTatDays); // counted from the first receipt
    }

    [Fact]
    public void Resubmission_count_is_records_minus_one()
    {
        Assert.Equal(0, CaseCalculator.ResubmissionCount(1));
        Assert.Equal(2, CaseCalculator.ResubmissionCount(3));
    }

    [Fact]
    public void Nrft_on_one_record_is_applied_to_all_records_of_the_case()
    {
        var a = Make.Record(1);
        var b = Make.Record(2, inward: Make.Monday.AddDays(1));
        a.DiscrepancyRaisedDate = Make.Monday;
        CaseCalculator.Recalculate(new List<Account> { a, b }, Make.Monday.AddDays(2), 7);
        Assert.Equal("NRFT", b.RftStatus);
    }

    [Fact]
    public void Latest_record_prefers_newest_inward_then_highest_id_and_treats_unsaved_as_newest()
    {
        var a = Make.Record(5, inward: Make.Monday);
        var b = Make.Record(3, inward: Make.Monday);
        var unsaved = Make.Record(0, inward: Make.Monday);
        Assert.Same(a, CaseCalculator.Latest(new[] { a, b }));
        Assert.Same(unsaved, CaseCalculator.Latest(new[] { a, b, unsaved }));
    }
}

public class MisCalculatorTests
{
    private static List<Account> Sample()
    {
        // Case 1: RFT, opened on Wednesday (2 working days).
        var c1 = Make.Record(1, "C1");
        c1.AccountOpeningDate = Make.Monday.AddDays(2);
        c1.LatestStatus = WorkflowStatuses.AccountOpened;

        // Case 2: NRFT with a resubmission record, still open.
        var c2a = Make.Record(2, "C2");
        c2a.DiscrepancyRaisedDate = Make.Monday.AddDays(1);
        var c2b = Make.Record(3, "C2", Make.Monday.AddDays(2));
        c2b.EntryType = "Discrepancy Resolution";
        c2b.Cse = "CSE Two";

        // Case 3: MOFSL query, different location/segment.
        var c3 = Make.Record(4, "C3");
        c3.MofslQueryRaisedDate = Make.Monday.AddDays(1);
        c3.Location = "Pune";
        c3.Segment = "HNI";

        var all = new List<Account> { c1, c2a, c2b, c3 };
        foreach (var g in all.GroupBy(a => a.ReferenceId))
            CaseCalculator.Recalculate(g.ToList(), Make.Monday.AddDays(4), 7);
        return all;
    }

    [Fact]
    public void Distinguishes_distinct_cases_from_records()
    {
        var report = MisCalculator.Build(Sample(), new MisFilters(null, null, null), 7);
        Assert.Equal(4, report.Summary.TotalRecords);
        Assert.Equal(3, report.Summary.TotalCases);
        Assert.Equal(1, report.Summary.ResubmissionRecords);
    }

    [Fact]
    public void Rft_percentage_is_per_case()
    {
        var s = MisCalculator.Build(Sample(), new MisFilters(null, null, null), 7).Summary;
        Assert.Equal(1, s.RftCases);
        Assert.Equal(2, s.NrftCases);
        Assert.Equal(33.33, s.RftPercentage);
        Assert.Equal(1, s.CseSideReworkCases);
        Assert.Equal(1, s.MofslSideReworkCases);
    }

    [Fact]
    public void Average_tat_uses_one_value_per_case()
    {
        var s = MisCalculator.Build(Sample(), new MisFilters(null, null, null), 7).Summary;
        // C1 = 2 (opened Wed); C2 latest = 4 (from original Monday to Friday); C3 = 4.
        Assert.Equal(Math.Round((2 + 4 + 4) / 3.0, 2), s.AverageGrossTatDays);
        Assert.Equal(1, s.CompletedCases);
        Assert.Equal(2, s.OpenCases);
    }

    [Fact]
    public void Filters_apply_to_summary_breakdowns_and_pipeline()
    {
        var report = MisCalculator.Build(Sample(), new MisFilters(null, "Pune", null), 7);
        Assert.Equal(1, report.Summary.TotalCases);
        Assert.Single(report.ByLocation);
        Assert.Equal(1, report.StagePipeline.Sum(s => s.CaseCount));
    }

    [Fact]
    public void Breakdowns_group_by_cse_location_and_segment()
    {
        var report = MisCalculator.Build(Sample(), new MisFilters(null, null, null), 7);
        Assert.Contains(report.ByCse, b => b.Name == "CSE One");
        Assert.Contains(report.ByLocation, b => b.Name == "Pune" && b.Metrics.MofslQueryCases == 1);
        Assert.Contains(report.BySegment, b => b.Name == "HNI");
    }
}
