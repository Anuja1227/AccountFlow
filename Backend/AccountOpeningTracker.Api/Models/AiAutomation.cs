namespace AccountOpeningTracker.Api.Models;

/// <summary>How much the AI may do on its own. Set by Admin (Settings).</summary>
public static class AutomationModes
{
    /// <summary>AI acts by itself through the normal workflow actions (labelled and audited).</summary>
    public const string Automatic = "Automatic";

    /// <summary>AI only records a recommendation; a person applies it with one click.</summary>
    public const string SuggestOnly = "SuggestOnly";

    /// <summary>No automatic AI processing.</summary>
    public const string Off = "Off";

    public static readonly string[] All = { Automatic, SuggestOnly, Off };
}

public static class InsightKinds
{
    public const string IntakeCheck = "IntakeCheck";
    public const string ResponseReview = "ResponseReview";
    public const string DocumentCheck = "DocumentCheck";
}

public static class InsightOutcomes
{
    public const string Complete = "Complete";
    public const string Incomplete = "Incomplete";
    public const string Resolved = "Resolved";
    public const string NotResolved = "NotResolved";
    public const string Uncertain = "Uncertain";
    public const string Failed = "Failed";
    public const string IssuesFound = "IssuesFound";
    public const string NoIssuesFound = "NoIssuesFound";
}

/// <summary>
/// The result of one AI automation run on a case: what the AI found, what it
/// recommended, and what (if anything) the system did about it.
/// </summary>
public class AiCaseInsight
{
    public long Id { get; set; }
    public int AccountId { get; set; }
    public string ReferenceId { get; set; } = string.Empty;
    public string Kind { get; set; } = InsightKinds.IntakeCheck;
    /// <summary>The query reviewed (ResponseReview).</summary>
    public int? QueryId { get; set; }

    public string Outcome { get; set; } = InsightOutcomes.Uncertain;
    public double Confidence { get; set; }
    public string Summary { get; set; } = string.Empty;

    /// <summary>JSON array of finding strings.</summary>
    public string FindingsJson { get; set; } = "[]";

    /// <summary>Workflow action the AI recommends (null = none).</summary>
    public string? RecommendedAction { get; set; }

    /// <summary>JSON-serialised WorkflowInput for the recommended action.</summary>
    public string? RecommendedInputJson { get; set; }

    /// <summary>Mode in force when it ran.</summary>
    public string Mode { get; set; } = AutomationModes.Automatic;

    /// <summary>Pending (suggestion waiting) | Applied (by AI or person) | Dismissed | NotApplicable | Blocked</summary>
    public string ActionStatus { get; set; } = "NotApplicable";
    public string? ActionResult { get; set; }
    public string? AppliedByName { get; set; }
    public DateTime? AppliedAt { get; set; }

    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
