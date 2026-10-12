
namespace AccountOpeningTracker.Api.Models;

public static class WorkflowValidationHelper
{
    private static readonly string[] AllowedAccountTypes =
    {
        "Individual", "HUF", "Corporate", "NRI", "Minor"
    };

    private static readonly string[] AllowedChannels =
    {
        "Physical", "Digital"
    };

    private static readonly string[] AllowedActionBy =
    {
        "Operations", "CSE", "MOFSL"
    };

    private static readonly string[] AllowedDiscrepancyTypes =
    {
        "Document Missing",
        "Signature Mismatch",
        "Incomplete Details",
        "Other"
    };

    private static readonly string[] AllowedReviewOutcomes =
    {
        "Found in Order", "Still Pending"
    };

    public static string? Validate(
        string? accountType,
        string? channel,
        string? actionBy,
        string? discrepancyType,
        string? stage4ReviewOutcome,
        DateTime? inwardDate,
        DateTime? accountOpeningDate,
        DateTime? discrepancyRaisedDate,
        DateTime? discrepancyResolutionReceivedDate,
        DateTime? submittedToMofslDate,
        DateTime? mofslQueryRaisedDate,
        DateTime? mofslQueryResolvedDate)
    {
        if (!IsAllowed(AllowedAccountTypes, accountType))
            return "Invalid Account Type.";

        if (!IsAllowed(AllowedChannels, channel))
            return "Invalid Channel.";

        if (!IsAllowed(AllowedActionBy, actionBy))
            return "Invalid Action By value.";

        if (!IsOptionalAllowed(AllowedDiscrepancyTypes, discrepancyType))
            return "Invalid Discrepancy Type.";

        if (!IsOptionalAllowed(AllowedReviewOutcomes, stage4ReviewOutcome))
            return "Invalid Stage 4 Review Outcome.";

        if (!inwardDate.HasValue || inwardDate.Value == default)
            return "Inward Date is required.";

        if (accountOpeningDate.HasValue &&
            accountOpeningDate.Value.Date < inwardDate.Value.Date)
            return "Account Opening Date cannot be before Inward Date.";

        if (discrepancyRaisedDate.HasValue &&
            discrepancyRaisedDate.Value.Date < inwardDate.Value.Date)
            return "Discrepancy Raised Date cannot be before Inward Date.";

        if (discrepancyResolutionReceivedDate.HasValue &&
            discrepancyRaisedDate.HasValue &&
            discrepancyResolutionReceivedDate.Value.Date <
            discrepancyRaisedDate.Value.Date)
            return "Discrepancy Resolution Date cannot be before the Discrepancy Raised Date.";

        if (submittedToMofslDate.HasValue &&
            submittedToMofslDate.Value.Date < inwardDate.Value.Date)
            return "Submitted to MOFSL Date cannot be before Inward Date.";

        if (mofslQueryResolvedDate.HasValue &&
            mofslQueryRaisedDate.HasValue &&
            mofslQueryResolvedDate.Value.Date <
            mofslQueryRaisedDate.Value.Date)
            return "MOFSL Query Resolved Date cannot be before Query Raised Date.";

        return null;
    }

    private static bool IsAllowed(
        string[] allowedValues,
        string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            allowedValues.Contains(
                value.Trim(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsOptionalAllowed(
        string[] allowedValues,
        string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
            IsAllowed(allowedValues, value);
    }
}
