namespace AccountOpeningTracker.Api.Models;

/// <summary>
/// Recalculates every server-controlled field for all records that share a
/// Reference ID: RFT/NRFT, Original Inward Date, Gross/Net TAT, Current
/// Aging, SLA breach (overall and stage-wise), Stage 1–6 TAT and Current Stage. Uses the existing helpers so the
/// business rules live in one place.
/// </summary>
public static class CaseCalculator
{
    public static void Recalculate(
        IReadOnlyCollection<Account> relatedRecords,
        DateTime today,
        int slaThresholdWorkingDays)
    {
        if (relatedRecords.Count == 0) return;

        string rft = RftHelper.DetermineRftStatus(relatedRecords);
        DateTime originalInward = TatHelper.DetermineOriginalInwardDate(relatedRecords);
        var stageTats = StageTatHelper.Calculate(relatedRecords, today, slaThresholdWorkingDays);

        foreach (var record in relatedRecords)
        {
            record.RftStatus = rft;
            TatHelper.CalculateTat(record, originalInward, today, slaThresholdWorkingDays);
            record.CurrentStage = WorkflowHelper.DetermineCurrentStage(record);
            record.StageTats = stageTats;
        }
    }

    /// <summary>
    /// Refreshes time-dependent fields (TAT/aging/SLA) for a set of records
    /// in memory, grouped by Reference ID. Used on read so open cases age
    /// correctly without needing a write.
    /// </summary>
    public static void RefreshForRead(
        IEnumerable<Account> records,
        DateTime today,
        int slaThresholdWorkingDays)
    {
        foreach (var group in records.GroupBy(r => r.ReferenceId))
        {
            var list = group.ToList();
            DateTime originalInward = list
                .Select(r => r.OriginalInwardDate?.Date ?? r.InwardDate.Date)
                .Min();
            var stageTats = StageTatHelper.Calculate(list, today, slaThresholdWorkingDays);
            foreach (var record in list)
            {
                TatHelper.CalculateTat(record, originalInward, today, slaThresholdWorkingDays);
                record.StageTats = stageTats;
            }
        }
    }

    /// <summary>Resubmission Count = number of rows sharing the Reference ID, minus 1.</summary>
    public static int ResubmissionCount(int recordsForReference) =>
        Math.Max(0, recordsForReference - 1);

    /// <summary>The latest record of a case (the one workflow actions apply to).</summary>
    public static Account Latest(IEnumerable<Account> relatedRecords) =>
        relatedRecords
            .OrderByDescending(a => a.InwardDate)
            .ThenByDescending(a => a.Id == 0 ? int.MaxValue : a.Id)
            .First();

    /// <summary>
    /// Latest record of each case. Unsaved records (Id 0) count as newest.
    /// </summary>
    public static List<Account> LatestPerCase(IEnumerable<Account> records) =>
        records.GroupBy(a => a.ReferenceId).Select(g => Latest(g)).ToList();
}
