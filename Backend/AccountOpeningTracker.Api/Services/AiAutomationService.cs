using System.Text.Json;
using AccountOpeningTracker.Api.Ai;
using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using AccountOpeningTracker.Api.Workflow;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Services;

/// <summary>
/// Runs the AI automations and records what happened as an AiCaseInsight:
///  1. Intake check after a request is logged → raises the Stage 1 query to
///     CSE itself, or moves a complete request to form preparation.
///  2. Response review after the CSE replies to a query → accepts a complete
///     reply (and moves a Stage 1 case on to form preparation) or sends the
///     follow-up listing what is still outstanding.
/// Mode (Admin → Settings): Automatic = act; SuggestOnly = record a one-click
/// recommendation; Off = do nothing automatically.
/// The AI only produces an assessment. AutomationPolicy (plain code) decides
/// the steps and the WorkflowEngine validates each one, so business rules hold.
/// </summary>
public sealed class AiAutomationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApplicationDbContext _db;
    private readonly AiService _ai;
    private readonly CaseService _cases;
    private readonly ISettingsService _settings;
    private readonly ILogger<AiAutomationService> _log;

    public AiAutomationService(ApplicationDbContext db, AiService ai, CaseService cases, ISettingsService settings, ILogger<AiAutomationService> log)
    {
        _db = db;
        _ai = ai;
        _cases = cases;
        _settings = settings;
        _log = log;
    }

    /// <param name="manual">True when a person clicked "Run AI check": runs even if mode is Off (as a suggestion).</param>
    public async Task<AiCaseInsight?> RunIntakeCheckAsync(string referenceId, int triggeredByUserId, bool manual, CancellationToken ct)
    {
        var mode = await EffectiveModeAsync(manual);
        if (mode == null) return null;

        var records = await _db.Accounts.AsNoTracking().Where(a => a.ReferenceId == referenceId).ToListAsync(ct);
        if (records.Count == 0) return null;
        var latest = CaseCalculator.Latest(records);

        var required = AutomationPolicy.MissingRequiredFields(latest);
        var insight = NewInsight(latest, InsightKinds.IntakeCheck, mode);

        IntakeAssessment assessment;
        try
        {
            (assessment, _) = await _ai.AssessIntakeAsync(latest, required, triggeredByUserId, ct);
        }
        catch (Exception ex) when (ex is AiUnavailableException or AiOutputException)
        {
            if (required.Count == 0)
            {
                return await SaveFailureAsync(insight, $"AI check could not run: {ex.Message}");
            }
            // Required-field rules do not need AI: still query the missing fields.
            assessment = new IntakeAssessment(false, 0, new List<string>(), null, $"AI unavailable ({ex.Message}); used required-field rules only.");
        }

        var plan = AutomationPolicy.PlanIntake(assessment, latest, required);
        insight.Outcome = plan.Outcome;
        insight.Confidence = assessment.Confidence;
        insight.Summary = Truncate(insight.Mode == AutomationModes.Automatic ? $"{assessment.Summary} {plan.Explanation}" : assessment.Summary, 2000);
        insight.FindingsJson = JsonSerializer.Serialize(required.Concat(assessment.MissingItems).Distinct().ToList(), Json);

        return await ExecuteOrSuggestAsync(insight, plan, referenceId, ct);
    }

    public async Task<AiCaseInsight?> RunResponseReviewAsync(string referenceId, int queryId, int triggeredByUserId, bool manual, CancellationToken ct,
        bool documentsAttached = false)
    {
        var mode = await EffectiveModeAsync(manual);
        if (mode == null) return null;

        var query = await _db.CaseQueries.AsNoTracking().Include(q => q.Messages)
            .FirstOrDefaultAsync(q => q.Id == queryId && q.ReferenceId == referenceId, ct);
        if (query == null || query.ToTeam != WorkspaceTypes.Cse || query.Status != QueryStatuses.Responded) return null;

        // A Stage 3 correction sent with documents is judged on the documents (the AI
        // pre-check), not on the reply text. Otherwise, in Automatic mode, a short reply
        // ("corrected form attached") could trigger a follow-up before the new documents
        // are checked, and the corrected form would never be re-checked.
        if (!manual && documentsAttached && query.QueryType is QueryTypes.Discrepancy or QueryTypes.FormReturned)
            return null;

        var records = await _db.Accounts.AsNoTracking().Where(a => a.ReferenceId == referenceId).ToListAsync(ct);
        if (records.Count == 0) return null;
        var latest = CaseCalculator.Latest(records);

        var insight = NewInsight(latest, InsightKinds.ResponseReview, mode);
        insight.QueryId = query.Id;

        ResponseAssessment assessment;
        try
        {
            (assessment, _) = await _ai.AssessResponseAsync(latest, query, triggeredByUserId, ct);
        }
        catch (Exception ex) when (ex is AiUnavailableException or AiOutputException)
        {
            return await SaveFailureAsync(insight, $"AI review could not run: {ex.Message} Operations should review the response.");
        }

        var allQueries = await _db.CaseQueries.AsNoTracking().Where(q => q.ReferenceId == referenceId).ToListAsync(ct);
        var plan = AutomationPolicy.PlanResponseReview(assessment, query, latest, allQueries);

        insight.Outcome = plan.Outcome;
        insight.Confidence = assessment.Confidence;
        insight.Summary = Truncate(insight.Mode == AutomationModes.Automatic ? $"{assessment.Summary} {plan.Explanation}" : assessment.Summary, 2000);
        insight.FindingsJson = JsonSerializer.Serialize(assessment.OutstandingItems, Json);

        return await ExecuteOrSuggestAsync(insight, plan, referenceId, ct);
    }

    /// <summary>Statuses in which documents returned by the CSE are being verified.</summary>
    public static readonly string[] DocumentCheckStatuses =
    {
        WorkflowStatuses.SignedFormReceived, WorkflowStatuses.UnderReviewSignedForm,
        WorkflowStatuses.DiscrepancyResolutionReceived, WorkflowStatuses.ResubmittedFormUnderReview
    };

    public const int MaxImagesPerCheck = 3;
    public const long MaxImageBytes = 3 * 1024 * 1024;

    /// <summary>
    /// The files of the most recent upload: same workflow action (purpose and query) and saved
    /// together. Earlier uploads, e.g. the first signed form before a corrected one, are left out.
    /// </summary>
    public static List<CaseDocument> LatestUpload(IReadOnlyCollection<CaseDocument> docs)
    {
        if (docs.Count == 0) return new();
        var newest = docs.OrderByDescending(d => d.UploadedAt).ThenByDescending(d => d.Id).First();
        return docs.Where(d => d.Purpose == newest.Purpose && d.QueryId == newest.QueryId &&
                               d.UploadedAt >= newest.UploadedAt.AddSeconds(-30))
                   .OrderBy(d => d.Id).ToList();
    }

    /// <summary>
    /// AI pre-check of the signed form / documents the CSE sent back (Stage 3).
    /// Never acts on its own, whatever the mode: it records what it found and a
    /// drafted discrepancy that Operations can use, edit or ignore.
    /// </summary>
    public async Task<AiCaseInsight?> RunDocumentCheckAsync(string referenceId, int triggeredByUserId, bool manual, CancellationToken ct)
    {
        var mode = await EffectiveModeAsync(manual);
        if (mode == null) return null;

        var records = await _db.Accounts.AsNoTracking().Where(a => a.ReferenceId == referenceId).ToListAsync(ct);
        if (records.Count == 0) return null;
        var latest = CaseCalculator.Latest(records);
        if (!DocumentCheckStatuses.Contains(latest.LatestStatus))
        {
            if (manual) throw new ArgumentException("There are no returned documents waiting to be verified on this case.");
            return null;
        }

        // Only the files sent with the most recent upload (one upload = one workflow action).
        var all = await _db.CaseDocuments.AsNoTracking().Where(d => d.ReferenceId == referenceId)
            .Select(d => new CaseDocument { Id = d.Id, QueryId = d.QueryId, Purpose = d.Purpose, FileName = d.FileName, ContentType = d.ContentType, SizeBytes = d.SizeBytes, UploadedAt = d.UploadedAt })
            .ToListAsync(ct);
        if (all.Count == 0)
        {
            if (manual) throw new ArgumentException("No documents have been uploaded on this case yet.");
            return null;
        }
        var batch = LatestUpload(all);

        var form = ApplicationFormRules.Parse(
            await _db.ApplicationForms.AsNoTracking().Where(f => f.ReferenceId == referenceId).Select(f => f.DataJson).FirstOrDefaultAsync(ct));
        var queries = await _db.CaseQueries.AsNoTracking().Where(q => q.ReferenceId == referenceId).ToListAsync(ct);

        var insight = NewInsight(latest, InsightKinds.DocumentCheck, mode);
        insight.Model = _ai.Provider.VisionModel;

        var observations = new List<DocumentObservation>();
        var notChecked = new List<string>();
        var errors = new List<string>();
        // Each image, and each page of a PDF (rendered to an image), is read separately.
        var pages = new List<CaseDocument>();
        foreach (var doc in batch)
        {
            int slots = MaxImagesPerCheck - pages.Count;
            if (slots <= 0) { notChecked.Add($"{doc.FileName} (only {MaxImagesPerCheck} pages/images are checked)"); continue; }

            if (doc.ContentType == "application/pdf")
            {
                var full = await _db.CaseDocuments.AsNoTracking().FirstAsync(d => d.Id == doc.Id, ct);
                try
                {
                    var jpegs = await PdfPages.ToJpegsAsync(full.Data, slots, ct);
                    if (jpegs.Count == 0) { notChecked.Add($"{doc.FileName} (no pages)"); continue; }
                    for (int i = 0; i < jpegs.Count; i++)
                        pages.Add(new CaseDocument
                        {
                            Id = doc.Id,
                            FileName = jpegs.Count == 1 ? doc.FileName : $"{doc.FileName} page {i + 1}",
                            ContentType = "image/jpeg",
                            Data = jpegs[i],
                            SizeBytes = jpegs[i].LongLength
                        });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Could not render PDF {File} on {Ref}", doc.FileName, referenceId);
                    notChecked.Add($"{doc.FileName} (PDF could not be read — password-protected or damaged?)");
                }
                continue;
            }

            if (!doc.ContentType.StartsWith("image/")) { notChecked.Add(doc.FileName); continue; }
            if (doc.SizeBytes > MaxImageBytes) { notChecked.Add($"{doc.FileName} (larger than 3 MB)"); continue; }
            pages.Add(await _db.CaseDocuments.AsNoTracking().FirstAsync(d => d.Id == doc.Id, ct));
        }

        foreach (var page in pages)
        {
            try
            {
                observations.Add(await _ai.ReadDocumentImageAsync(latest, form, page, triggeredByUserId, ct));
            }
            catch (Exception ex) when (ex is AiUnavailableException or AiOutputException)
            {
                errors.Add($"{page.FileName}: {ex.Message}");
            }
        }

        if (observations.Count == 0 && errors.Count > 0)
            return await SaveFailureAsync(insight, $"AI document check could not run: {string.Join(" ", errors)} Operations should verify the documents manually.");
        notChecked.AddRange(errors.Select(e => e.Split(':')[0] + " (AI busy)"));

        var plan = DocumentCheckPolicy.Plan(observations, notChecked, latest, form, queries);
        insight.Outcome = plan.Outcome;
        insight.Confidence = observations.Count == 0 ? 0 : observations.Average(o => o.Confidence);
        insight.Summary = Truncate(plan.Summary, 2000);
        insight.FindingsJson = JsonSerializer.Serialize(plan.Findings, Json);
        insight.QueryId = plan.Steps.LastOrDefault()?.Input.QueryId;
        insight.RecommendedAction = plan.Steps.LastOrDefault()?.Action;
        insight.RecommendedInputJson = JsonSerializer.Serialize(plan.Steps, Json);
        if (plan.Steps.Count > 0)
        {
            insight.ActionStatus = "Pending";
            insight.ActionResult = $"Suggested: {Label(plan.Steps.Last().Action)} ({plan.DiscrepancyType}). Operations decides.";
        }
        else
        {
            insight.ActionStatus = "NotApplicable";
        }

        _db.AiCaseInsights.Add(insight);
        await _db.SaveChangesAsync(ct);
        return insight;
    }

    /// <summary>One-click apply of a suggestion, performed as (and permission-checked for) the user.</summary>
    public async Task<ServiceResult<string>> ApplyAsync(long insightId, UserAccessProfile user)
    {
        var insight = await _db.AiCaseInsights.FirstOrDefaultAsync(i => i.Id == insightId);
        if (insight == null) return ServiceResult<string>.Fail("Suggestion not found.", 404);
        if (insight.ActionStatus != "Pending") return ServiceResult<string>.Fail("This suggestion is no longer pending.", 409);

        var steps = JsonSerializer.Deserialize<List<PlannedStep>>(insight.RecommendedInputJson ?? "[]", Json) ?? new();
        var results = new List<string>();

        // A person may already have done an opening step by hand (e.g. "Start review"):
        // skip it instead of failing the whole suggestion.
        var current = await _db.Accounts.AsNoTracking().Where(a => a.ReferenceId == insight.ReferenceId).ToListAsync();
        var status = current.Count > 0 ? CaseCalculator.Latest(current).LatestStatus : "";
        if (steps.Count > 1 && steps[0].Action == ActionCodes.StartReview && status != WorkflowStatuses.RequestReceived)
            steps.RemoveAt(0);

        foreach (var step in steps)
        {
            step.Input.AiAssisted = true;
            var r = await _cases.ExecuteActionAsync(insight.ReferenceId, step.Action, step.Input, user);
            if (!r.Success)
            {
                insight.ActionResult = Truncate($"Stopped at '{step.Action}': {r.Error}", 500);
                await _db.SaveChangesAsync();
                return ServiceResult<string>.Fail(r.Error!, r.StatusCode);
            }
            results.Add(Label(step.Action));
        }

        insight.ActionStatus = "Applied";
        insight.AppliedByName = user.DisplayName;
        insight.AppliedAt = DateTime.UtcNow;
        insight.ActionResult = Truncate("Applied: " + string.Join(" → ", results), 500);
        await _db.SaveChangesAsync();
        return ServiceResult<string>.Ok(insight.ActionResult);
    }

    /// <summary>
    /// A person opened the AI's draft, edited it as needed and sent it through the
    /// normal action form: record that the suggestion was used (by them).
    /// </summary>
    public async Task<bool> MarkUsedAsync(long insightId, UserAccessProfile user)
    {
        var insight = await _db.AiCaseInsights.FirstOrDefaultAsync(i => i.Id == insightId);
        if (insight == null || insight.ActionStatus != "Pending") return false;
        insight.ActionStatus = "Applied";
        insight.AppliedByName = user.DisplayName;
        insight.AppliedAt = DateTime.UtcNow;
        insight.ActionResult = Truncate($"Reviewed and sent by {user.DisplayName}: {Label(insight.RecommendedAction ?? "")}", 500);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DismissAsync(long insightId, UserAccessProfile user)
    {
        var insight = await _db.AiCaseInsights.FirstOrDefaultAsync(i => i.Id == insightId);
        if (insight == null || insight.ActionStatus != "Pending") return false;
        insight.ActionStatus = "Dismissed";
        insight.AppliedByName = user.DisplayName;
        insight.AppliedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    // ------------------------------------------------------------------

    private async Task<string?> EffectiveModeAsync(bool manual)
    {
        var mode = await _settings.GetAutomationModeAsync();
        if (mode == AutomationModes.Off) return manual ? AutomationModes.SuggestOnly : null;
        return mode;
    }

    private AiCaseInsight NewInsight(Account latest, string kind, string mode) => new()
    {
        AccountId = latest.Id,
        ReferenceId = latest.ReferenceId,
        Kind = kind,
        Mode = mode,
        Provider = _ai.Provider.Name,
        Model = _ai.Provider.Model
    };

    private async Task<AiCaseInsight> SaveFailureAsync(AiCaseInsight insight, string message)
    {
        insight.Outcome = InsightOutcomes.Failed;
        insight.Summary = Truncate(message, 2000);
        insight.ActionStatus = "NotApplicable";
        _db.AiCaseInsights.Add(insight);
        await _db.SaveChangesAsync();
        return insight;
    }

    private async Task<AiCaseInsight> ExecuteOrSuggestAsync(AiCaseInsight insight, AutomationPlan plan, string referenceId, CancellationToken ct)
    {
        var meaningful = plan.Steps.Where(s => s.Action != ActionCodes.StartReview).ToList();
        insight.RecommendedAction = (meaningful.LastOrDefault() ?? plan.Steps.LastOrDefault())?.Action;
        insight.RecommendedInputJson = JsonSerializer.Serialize(plan.Steps, Json);

        if (plan.Steps.Count == 0)
        {
            insight.ActionStatus = "NotApplicable";
        }
        else if (insight.Mode == AutomationModes.Automatic)
        {
            var done = new List<string>();
            insight.ActionStatus = "Applied";
            foreach (var step in plan.Steps)
            {
                var r = await _cases.ExecuteSystemActionAsync(referenceId, step.Action, step.Input,
                    $"Automated by AI ({insight.Kind}, confidence {insight.Confidence:0.00}).");
                if (!r.Success)
                {
                    insight.ActionStatus = done.Count > 0 ? "Applied" : "Blocked";
                    done.Add($"stopped at '{Label(step.Action)}': {r.Error}");
                    _log.LogInformation("AI automation step {Action} on {Ref} was blocked: {Error}", step.Action, referenceId, r.Error);
                    break;
                }
                done.Add(Label(step.Action));
            }
            insight.AppliedByName = CaseService.AiActorName;
            insight.AppliedAt = DateTime.UtcNow;
            insight.ActionResult = Truncate("AI did: " + string.Join(" → ", done), 500);
        }
        else
        {
            insight.ActionStatus = "Pending";
            insight.ActionResult = "Suggested: " + string.Join(" → ", plan.Steps.Select(s => Label(s.Action)));
        }

        _db.AiCaseInsights.Add(insight);
        await _db.SaveChangesAsync(ct);
        return insight;
    }

    private static string Label(string action) => WorkflowEngine.Find(action)?.Label ?? action;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
