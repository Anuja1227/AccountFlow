using System.Text.RegularExpressions;
using AccountOpeningTracker.Api.Models;
using S = AccountOpeningTracker.Api.Models.WorkflowStatuses;

namespace AccountOpeningTracker.Api.Workflow;

/// <summary>What the AI concluded about a newly logged request.</summary>
public sealed record IntakeAssessment(
    bool Complete,
    double Confidence,
    List<string> MissingItems,
    string? QueryMessage,
    string Summary);

/// <summary>What the AI concluded about a CSE's reply to a query.</summary>
public sealed record ResponseAssessment(
    bool Resolved,
    double Confidence,
    List<string> OutstandingItems,
    string? FollowUpMessage,
    string Summary);

public sealed record PlannedStep(string Action, WorkflowInput Input);

public sealed record AutomationPlan(string Outcome, List<PlannedStep> Steps, string Explanation);

/// <summary>
/// Deterministic rules that turn an AI assessment into workflow steps.
/// The AI only supplies an assessment; this code decides whether anything
/// happens, using confidence thresholds and the case's current status. Every
/// step is still validated by the WorkflowEngine when it runs.
/// </summary>
public static class AutomationPolicy
{
    public const double ActOnIssuesConfidence = 0.6;
    public const double ActOnPassConfidence = 0.75;

    private static readonly Regex Pan = new("^[A-Z]{5}[0-9]{4}[A-Z]$", RegexOptions.Compiled);

    /// <summary>
    /// Required-field checks from the requirements' Data Entry list. These do
    /// not need AI and always count as missing information.
    /// </summary>
    public static List<string> MissingRequiredFields(Account a)
    {
        var missing = new List<string>();
        void Need(string? value, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) missing.Add(label);
        }
        Need(a.ClientName, "Client name");
        if (string.IsNullOrWhiteSpace(a.PanNo)) missing.Add("PAN");
        else if (!Pan.IsMatch(a.PanNo.Trim().ToUpperInvariant())) missing.Add("A valid PAN (format AAAAA9999A)");
        Need(a.AccountType, "Account type");
        Need(a.Channel, "Channel (Physical/Digital)");
        Need(a.Location, "Location");
        Need(a.Segment, "Segment");
        Need(a.RelationshipManager, "RM name");
        Need(a.Cse, "CSE name");
        return missing;
    }

    public static AutomationPlan PlanIntake(IntakeAssessment ai, Account latest, IReadOnlyList<string> requiredMissing)
    {
        var status = latest.LatestStatus;
        if (status != S.RequestReceived && status != S.UnderReviewByOperations)
            return new AutomationPlan(InsightOutcomes.Uncertain, new(), $"No action: the case is already at '{status}'.");

        var steps = new List<PlannedStep>();
        if (status == S.RequestReceived)
            steps.Add(new PlannedStep(ActionCodes.StartReview, new WorkflowInput()));

        var missing = requiredMissing.Concat(ai.MissingItems)
            .Select(m => m.Trim()).Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();

        bool incomplete = requiredMissing.Count > 0 || (!ai.Complete && ai.MissingItems.Count > 0 && ai.Confidence >= ActOnIssuesConfidence);
        if (incomplete)
        {
            steps.Add(new PlannedStep(ActionCodes.RaiseStage1Query, new WorkflowInput
            {
                Details = ComposeQuery(ai.QueryMessage, missing)
            }));
            return new AutomationPlan(InsightOutcomes.Incomplete, steps,
                $"Missing information found ({missing.Count} item(s)). Query raised to CSE.");
        }

        if (ai.Complete && ai.Confidence >= ActOnPassConfidence)
        {
            steps.Add(new PlannedStep(ActionCodes.StartFormPreparation, new WorkflowInput()));
            return new AutomationPlan(InsightOutcomes.Complete, steps,
                "All required information is present. Moved to application-form preparation.");
        }

        return new AutomationPlan(InsightOutcomes.Uncertain, steps,
            $"The AI was not confident enough to decide (confidence {ai.Confidence:0.00}). Left for Operations to review.");
    }

    /// <summary>
    /// After the CSE responds:
    ///  - Stage 1 (missing information): resolved → accept and, if nothing else is
    ///    open, move straight to form preparation; not resolved → follow up.
    ///  - Stage 3 (discrepancy / returned form): the AI may send a follow-up when the
    ///    reply clearly does not address the issue, but it never marks a physical
    ///    form "found in order" — Operations must look at the corrected form.
    /// </summary>
    public static AutomationPlan PlanResponseReview(ResponseAssessment ai, CaseQuery query, Account latest, IReadOnlyList<CaseQuery> allQueries)
    {
        var steps = new List<PlannedStep>();
        var status = latest.LatestStatus;

        if (query.Status != QueryStatuses.Responded)
            return new AutomationPlan(InsightOutcomes.Uncertain, steps, "No action: the query is no longer awaiting review.");

        var outstanding = ai.OutstandingItems.Select(i => i.Trim()).Where(i => i.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        bool notResolved = !ai.Resolved && outstanding.Count > 0 && ai.Confidence >= ActOnIssuesConfidence;
        bool resolved = ai.Resolved && outstanding.Count == 0 && ai.Confidence >= ActOnPassConfidence;

        if (query.QueryType == QueryTypes.MissingInformation && status == S.ResubmittedByCse)
        {
            if (resolved)
            {
                steps.Add(new PlannedStep(ActionCodes.OpsAcceptResponse, new WorkflowInput
                {
                    QueryId = query.Id,
                    Details = "AI review: the response provides everything that was asked for."
                }));
                bool otherOpen = allQueries.Any(q => q.Id != query.Id && q.QueryType == QueryTypes.MissingInformation && q.Status != QueryStatuses.Resolved);
                if (!otherOpen)
                {
                    steps.Add(new PlannedStep(ActionCodes.StartFormPreparation, new WorkflowInput()));
                    return new AutomationPlan(InsightOutcomes.Resolved, steps, "Response accepted; information complete, moved to form preparation.");
                }
                return new AutomationPlan(InsightOutcomes.Resolved, steps, "Response accepted; other queries are still open.");
            }
            if (notResolved)
            {
                steps.Add(new PlannedStep(ActionCodes.OpsFollowUp, new WorkflowInput { QueryId = query.Id, Details = ComposeFollowUp(ai.FollowUpMessage, outstanding) }));
                return new AutomationPlan(InsightOutcomes.NotResolved, steps, $"{outstanding.Count} item(s) still outstanding. Follow-up sent to CSE.");
            }
        }
        else if ((query.QueryType == QueryTypes.Discrepancy || query.QueryType == QueryTypes.FormReturned) &&
                 (status == S.DiscrepancyResolutionReceived || status == S.ResubmittedFormUnderReview))
        {
            if (notResolved)
            {
                steps.Add(new PlannedStep(ActionCodes.OpsFollowUp, new WorkflowInput { QueryId = query.Id, Details = ComposeFollowUp(ai.FollowUpMessage, outstanding) }));
                return new AutomationPlan(InsightOutcomes.NotResolved, steps, "The reply does not address the discrepancy. Follow-up sent to CSE.");
            }
            if (resolved)
                return new AutomationPlan(InsightOutcomes.Resolved, steps,
                    "The reply addresses the discrepancy. Operations should verify the corrected form and accept it.");
        }
        else
        {
            return new AutomationPlan(InsightOutcomes.Uncertain, steps, $"No action at status '{status}'.");
        }

        return new AutomationPlan(InsightOutcomes.Uncertain, steps,
            $"The AI was not confident enough to decide (confidence {ai.Confidence:0.00}). Left for Operations to review.");
    }

    internal static string ComposeQuery(string? aiMessage, List<string> missing)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(aiMessage)) lines.Add(aiMessage.Trim());
        else lines.Add("Please provide the following information so we can proceed with the account opening:");
        if (missing.Count > 0)
        {
            lines.Add("");
            lines.AddRange(missing.Select((m, i) => $"{i + 1}. {m}"));
        }
        var text = string.Join("\n", lines);
        return text.Length > WorkflowEngine.MaxTextLength ? text[..WorkflowEngine.MaxTextLength] : text;
    }

    internal static string ComposeFollowUp(string? aiMessage, List<string> outstanding)
    {
        var lines = new List<string>
        {
            string.IsNullOrWhiteSpace(aiMessage)
                ? "Thank you for your response. The following is still outstanding:"
                : aiMessage.Trim()
        };
        if (outstanding.Count > 0)
        {
            lines.Add("");
            lines.AddRange(outstanding.Select((m, i) => $"{i + 1}. {m}"));
        }
        var text = string.Join("\n", lines);
        return text.Length > WorkflowEngine.MaxTextLength ? text[..WorkflowEngine.MaxTextLength] : text;
    }
}
