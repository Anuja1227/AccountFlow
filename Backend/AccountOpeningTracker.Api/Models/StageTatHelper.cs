namespace AccountOpeningTracker.Api.Models;

/// <summary>Working days a case spent in one stage.</summary>
/// <param name="Days">Null when the case has not reached (or skipped) the stage.</param>
/// <param name="InProgress">True for the stage the case is in now (measured to today).</param>
/// <param name="IsSlaBreached">Stage-wise SLA: the stage's TAT exceeds the threshold.</param>
public sealed record StageTat(
    string Stage,
    DateTime? StartDate,
    DateTime? EndDate,
    int? Days,
    bool InProgress,
    bool IsSlaBreached);

/// <summary>
/// "Stage 1–6 TAT: working days elapsed within each individual stage" and
/// "SLA Breach (Overall + Stage-wise)" from the requirements document.
///
/// Each stage starts on the first date the requirements document records for
/// it, and ends when the next stage starts:
///   Stage 1  Original Inward Date
///   Stage 2  Form Prepared Date (or the physical/digital form date)
///   Stage 3  Signed Form Received Date
///   Stage 4  Discrepancy Resolution / Resubmitted Form Received Date (only if a discrepancy was raised)
///   Stage 5  Submitted to MOFSL Date
///   Stage 6  Account Opening Date, ending when the communication is sent
/// Because the working-day count excludes the start day, Stages 1–5 add up to
/// the Gross TAT. The stage the case is in now is measured to today. Dates
/// are taken across all records sharing the Reference ID, so resubmission
/// records are included. The same configurable SLA threshold applies to each stage.
/// </summary>
public static class StageTatHelper
{
    public static readonly string[] Stages = { "Stage 1", "Stage 2", "Stage 3", "Stage 4", "Stage 5", "Stage 6" };

    public static List<StageTat> Calculate(IReadOnlyCollection<Account> records, DateTime today, int slaThreshold)
    {
        if (records.Count == 0) return new();
        today = today.Date;

        DateTime? Earliest(params Func<Account, DateTime?>[] fields)
        {
            foreach (var f in fields)
            {
                var d = records.Select(f).Where(x => x.HasValue).Select(x => x!.Value.Date).DefaultIfEmpty().Min();
                if (d != default) return d;
            }
            return null;
        }

        var starts = new DateTime?[]
        {
            TatHelper.DetermineOriginalInwardDate(records),
            Earliest(a => a.FormPreparedDate, a => Min(a.PhysicalFormSubmittedToCseDate, a.DigitalFormSentToClientDate)),
            Earliest(a => a.SignedFormReceivedDate, a => Min(a.DiscrepancyRaisedDate, a.FormReturnedToCseDate)),
            Earliest(a => Min(a.DiscrepancyResolutionReceivedDate, a.ResubmittedFormReceivedDate)),
            Earliest(a => a.SubmittedToMofslDate, a => a.MofslQueryRaisedDate),
            Earliest(a => a.AccountOpeningDate),
        };
        var communicationSent = Earliest(a => a.CommunicationSentDate);

        var latest = CaseCalculator.Latest(records);
        bool stopped = WorkflowStatuses.IsTerminal(latest.LatestStatus);
        // A rejected / cancelled case stops counting on its last update.
        DateTime stopDate = stopped && latest.UpdatedAt.HasValue && latest.UpdatedAt.Value.Date < today
            ? latest.UpdatedAt.Value.Date
            : today;

        var result = new List<StageTat>(Stages.Length);
        for (int i = 0; i < Stages.Length; i++)
        {
            var start = starts[i];
            if (!start.HasValue)
            {
                result.Add(new StageTat(Stages[i], null, null, null, false, false));
                continue;
            }

            DateTime? end = null;
            for (int j = i + 1; j < Stages.Length && end == null; j++) end = starts[j];
            if (i == Stages.Length - 1) end = communicationSent;

            bool inProgress = false;
            if (end == null)
            {
                if (i == Stages.Length - 1 && latest.LatestStatus == WorkflowStatuses.CaseClosed)
                    end = start; // closed before this date was recorded
                else
                {
                    end = stopDate;
                    inProgress = !stopped;
                }
            }

            int days = Math.Max(0, WorkingDaysHelper.CountWorkingDays(start.Value, end.Value));
            result.Add(new StageTat(Stages[i], start, inProgress ? null : end, days, inProgress, days > slaThreshold));
        }
        return result;
    }

    private static DateTime? Min(DateTime? a, DateTime? b) =>
        a.HasValue && b.HasValue ? (a < b ? a : b) : a ?? b;
}
