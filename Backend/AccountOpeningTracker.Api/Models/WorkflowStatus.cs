
namespace AccountOpeningTracker.Api.Models;

/// <summary>
/// The single "Latest Status" master list from the requirements document,
/// each mapped to its stage.
/// </summary>
public static class WorkflowStatuses
{
    // Stage 1
    public const string RequestReceived = "Request Received from CSE";
    public const string UnderReviewByOperations = "Under Review by Operations";
    public const string QueryRaisedMissingInfo = "Query Raised to CSE — Missing Information";
    public const string ResubmittedByCse = "Resubmitted by CSE";

    // Stage 2
    public const string FormUnderPreparation = "Application Form Under Preparation";
    public const string PhysicalFormSubmitted = "Physical Form Submitted to CSE";
    public const string DigitalFormSent = "Digital Form Sent to Client";

    // Stage 3
    public const string SignedFormReceived = "Signed Form Received from CSE";
    public const string UnderReviewSignedForm = "Under Review — Signed Form";
    public const string DiscrepancyRaised = "Discrepancy Raised to CSE";
    public const string FormReturned = "Form Returned to CSE";

    // Stage 4
    public const string DiscrepancyResolutionReceived = "Discrepancy Resolution Received";
    public const string ResubmittedFormUnderReview = "Resubmitted Form Received — Under Review";
    public const string ReadyForMofsl = "Form Found in Order — Ready for MOFSL Submission";

    // Stage 5
    public const string SubmittedToMofsl = "Submitted to MOFSL";
    public const string QueryRaisedByMofsl = "Query Raised by MOFSL";
    public const string MofslQueryResolved = "Query Resolved — Resubmitted to MOFSL";

    // Stage 6
    public const string AccountOpened = "Account Opened";
    public const string CaseClosed = "Communication Sent — Case Closed";

    // Exceptions
    public const string OnHold = "On Hold";
    public const string Rejected = "Rejected";
    public const string CancelledByClient = "Cancelled by Client";

    public static readonly string[] All =
    {
        RequestReceived,
        UnderReviewByOperations,
        QueryRaisedMissingInfo,
        ResubmittedByCse,
        FormUnderPreparation,
        PhysicalFormSubmitted,
        DigitalFormSent,
        SignedFormReceived,
        UnderReviewSignedForm,
        DiscrepancyRaised,
        FormReturned,
        DiscrepancyResolutionReceived,
        ResubmittedFormUnderReview,
        ReadyForMofsl,
        SubmittedToMofsl,
        QueryRaisedByMofsl,
        MofslQueryResolved,
        AccountOpened,
        CaseClosed,
        OnHold,
        Rejected,
        CancelledByClient
    };

    /// <summary>Statuses after which no further workflow action is possible.</summary>
    public static readonly string[] Terminal = { CaseClosed, Rejected, CancelledByClient };

    public static bool IsTerminal(string status) => Terminal.Contains(status);

    /// <summary>Open = not terminal and account not yet opened.</summary>
    public static bool IsOpen(Account a) =>
        !IsTerminal(a.LatestStatus) && !a.AccountOpeningDate.HasValue;

    public static string StageOf(string status)
    {
        int index = Array.IndexOf(All, status);
        return index switch
        {
            >= 0 and <= 3 => "Stage 1",
            >= 4 and <= 6 => "Stage 2",
            >= 7 and <= 10 => "Stage 3",
            >= 11 and <= 13 => "Stage 4",
            >= 14 and <= 16 => "Stage 5",
            >= 17 and <= 18 => "Stage 6",
            >= 19 => "Exception",
            _ => "Unknown"
        };
    }
}
