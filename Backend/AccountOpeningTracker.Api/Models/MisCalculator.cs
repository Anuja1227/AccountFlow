namespace AccountOpeningTracker.Api.Models;

public sealed record MisMetrics(
    int TotalRecords,
    int TotalCases,
    int ResubmissionRecords,
    int OpenCases,
    int CompletedCases,
    int RftCases,
    int NrftCases,
    double RftPercentage,
    double AverageGrossTatDays,
    double AverageNetTatDays,
    double AverageCurrentAgingDays,
    int SlaBreachCases,
    int SlaBreachRecords,
    int Stage1QueryCases,
    int Stage3DiscrepancyCases,
    int MofslQueryCases,
    int CseSideReworkCases,
    int MofslSideReworkCases,
    int StageSlaBreachCases);

public sealed record MisBreakdown(string Name, MisMetrics Metrics);

/// <param name="CaseCount">Cases currently at this stage.</param>
/// <param name="SlaBreachCases">Of those, cases breaching the overall SLA.</param>
/// <param name="CasesMeasured">Cases that have reached this stage (Stage 1–6 TAT available).</param>
/// <param name="AverageStageTatDays">Average working days spent in this stage.</param>
/// <param name="StageSlaBreachCases">Cases whose time in this stage exceeds the SLA (stage-wise SLA).</param>
public sealed record MisStage(
    string Stage,
    int CaseCount,
    double AverageAgingDays,
    int SlaBreachCases,
    int CasesMeasured = 0,
    double AverageStageTatDays = 0,
    int StageSlaBreachCases = 0);

public sealed record MisFilters(string? Cse, string? Location, string? Segment);

public sealed record MisReport(
    MisFilters Filters,
    int SlaThresholdWorkingDays,
    MisMetrics Summary,
    List<MisBreakdown> ByCse,
    List<MisBreakdown> ByLocation,
    List<MisBreakdown> BySegment,
    List<MisStage> StagePipeline);

/// <summary>
/// Deterministic MIS arithmetic. A case is a Reference ID; a record is a
/// row (resubmissions add records, not cases).
///  - Case-level metrics (RFT, TAT averages, SLA breaches, attribution) use
///    one value per case so resubmissions do not double-count.
///  - TAT averages use each case's latest record, which carries the
///    cumulative hold days and the account-opening date.
/// </summary>
public static class MisCalculator
{
    public static readonly string[] StageNames =
        { "Stage 1", "Stage 2", "Stage 3", "Stage 4", "Stage 5", "Stage 6", "Completed" };

    public static MisReport Build(
        IReadOnlyCollection<Account> records,
        MisFilters filters,
        int slaThreshold)
    {
        // Filters select cases by the dimension values on their records.
        var filtered = records.Where(a =>
                (string.IsNullOrWhiteSpace(filters.Cse) || a.Cse == filters.Cse) &&
                (string.IsNullOrWhiteSpace(filters.Location) || a.Location == filters.Location) &&
                (string.IsNullOrWhiteSpace(filters.Segment) || a.Segment == filters.Segment))
            .ToList();

        var latest = CaseCalculator.LatestPerCase(filtered);

        var pipeline = StageNames.Select(stage =>
        {
            var stageCases = latest.Where(a => a.CurrentStage == stage).ToList();
            var open = stageCases.Where(a => !a.AccountOpeningDate.HasValue).ToList();
            // Stage-wise TAT for every case that reached the stage (set by CaseCalculator).
            var measured = latest
                .Select(a => a.StageTats.FirstOrDefault(t => t.Stage == stage))
                .Where(t => t?.Days != null)
                .Select(t => t!)
                .ToList();
            return new MisStage(
                stage,
                stageCases.Count,
                open.Count == 0 ? 0 : Math.Round(open.Average(a => (double)a.CurrentAgingDays), 2),
                stageCases.Count(a => a.IsSlaBreached),
                measured.Count,
                measured.Count == 0 ? 0 : Math.Round(measured.Average(t => (double)t.Days!.Value), 2),
                measured.Count(t => t.IsSlaBreached));
        }).ToList();

        return new MisReport(
            filters,
            slaThreshold,
            Metrics(filtered),
            Breakdown(filtered, a => a.Cse),
            Breakdown(filtered, a => a.Location),
            Breakdown(filtered, a => a.Segment),
            pipeline);
    }

    private static List<MisBreakdown> Breakdown(List<Account> rows, Func<Account, string?> key) =>
        rows.GroupBy(a => string.IsNullOrWhiteSpace(key(a)) ? "Unassigned" : key(a)!.Trim())
            .Select(g => new MisBreakdown(g.Key, Metrics(g.ToList())))
            .OrderBy(b => b.Name)
            .ToList();

    public static MisMetrics Metrics(IReadOnlyCollection<Account> rows)
    {
        var cases = rows.GroupBy(a => a.ReferenceId).ToList();
        var latest = cases.Select(g => CaseCalculator.Latest(g)).ToList();
        int caseCount = cases.Count;

        bool Stage1(IEnumerable<Account> g) => g.Any(a => a.Stage1QueryRaisedDate.HasValue);
        bool Stage3(IEnumerable<Account> g) => g.Any(a => a.DiscrepancyRaisedDate.HasValue || a.FormReturnedToCseDate.HasValue);
        bool Mofsl(IEnumerable<Account> g) => g.Any(a => a.MofslQueryRaisedDate.HasValue);

        // RFT is re-derived from the stage dates rather than trusting stored values.
        int nrft = cases.Count(g => RftHelper.DetermineRftStatus(g) == "NRFT");
        int rft = caseCount - nrft;

        var open = latest.Where(WorkflowStatuses.IsOpen).ToList();

        return new MisMetrics(
            TotalRecords: rows.Count,
            TotalCases: caseCount,
            ResubmissionRecords: rows.Count - caseCount,
            OpenCases: open.Count,
            CompletedCases: latest.Count(a => a.AccountOpeningDate.HasValue),
            RftCases: rft,
            NrftCases: nrft,
            RftPercentage: caseCount == 0 ? 0 : Math.Round(rft * 100.0 / caseCount, 2),
            AverageGrossTatDays: latest.Count == 0 ? 0 : Math.Round(latest.Average(a => (double)a.GrossTatDays), 2),
            AverageNetTatDays: latest.Count == 0 ? 0 : Math.Round(latest.Average(a => (double)a.NetTatDays), 2),
            AverageCurrentAgingDays: open.Count == 0 ? 0 : Math.Round(open.Average(a => (double)a.CurrentAgingDays), 2),
            SlaBreachCases: latest.Count(a => a.IsSlaBreached),
            SlaBreachRecords: rows.Count(a => a.IsSlaBreached),
            Stage1QueryCases: cases.Count(Stage1),
            Stage3DiscrepancyCases: cases.Count(Stage3),
            MofslQueryCases: cases.Count(Mofsl),
            CseSideReworkCases: cases.Count(g => Stage1(g) || Stage3(g)),
            MofslSideReworkCases: cases.Count(Mofsl),
            // Stage-wise SLA: cases where any single stage took longer than the threshold.
            StageSlaBreachCases: latest.Count(a => a.StageTats.Any(t => t.IsSlaBreached)));
    }
}
