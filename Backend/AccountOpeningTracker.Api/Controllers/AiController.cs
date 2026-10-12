using AccountOpeningTracker.Api.Ai;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using AccountOpeningTracker.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AccountOpeningTracker.Api.Controllers;

public sealed class AiFeedbackRequest
{
    public long InteractionId { get; set; }
    public bool Accepted { get; set; }
}

/// <summary>
/// AI endpoints. All require ai.use plus the access the underlying workflow
/// step needs. Automatic runs happen inside the workflow (case logged, CSE
/// replied); these endpoints add smart intake, manual re-runs, applying or
/// dismissing suggestions, and the read-only briefing / MIS commentary.
/// </summary>
[ApiController]
[Route("api/ai")]
[HasPermission(Permissions.AiUse)]
[EnableRateLimiting("ai")]
public class AiController : ControllerBase
{
    private readonly AiService _ai;
    private readonly AiAutomationService _automation;
    private readonly CaseAccessService _caseAccess;
    private readonly IAccessService _access;

    public AiController(AiService ai, AiAutomationService automation, CaseAccessService caseAccess, IAccessService access)
    {
        _ai = ai;
        _automation = automation;
        _caseAccess = caseAccess;
        _access = access;
    }

    [HttpGet("status")]
    public async Task<ActionResult<AiHealth>> Status(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return Ok(await _ai.Provider.CheckAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            return Ok(new AiHealth(_ai.Provider.Name, _ai.Provider.Model, true, false, _ai.Provider.IsExternal, "The AI provider did not respond in time."));
        }
    }

    /// <summary>Smart intake: extract new-case fields from a pasted CSE message.</summary>
    [HttpPost("extract-request")]
    public Task<IActionResult> ExtractRequest(ExtractRequestInput input, CancellationToken ct) =>
        Run(async user => await _ai.ExtractRequestAsync(input, user, ct));

    /// <summary>Re-run the intake completeness check (Operations).</summary>
    [HttpPost("cases/{referenceId}/intake-check")]
    public Task<IActionResult> IntakeCheck(string referenceId, CancellationToken ct) =>
        Run(async user =>
        {
            await RequireOperationsOnCase(referenceId, user);
            var insight = await _automation.RunIntakeCheckAsync(referenceId, user.UserId, manual: true, ct)
                          ?? throw new ArgumentException("Nothing to check.");
            return AiInsightDto.From(insight);
        });

    /// <summary>Re-run the review of a CSE reply (Operations).</summary>
    [HttpPost("cases/{referenceId}/queries/{queryId:int}/review")]
    public Task<IActionResult> ReviewResponse(string referenceId, int queryId, CancellationToken ct) =>
        Run(async user =>
        {
            await RequireOperationsOnCase(referenceId, user);
            var insight = await _automation.RunResponseReviewAsync(referenceId, queryId, user.UserId, manual: true, ct)
                          ?? throw new ArgumentException("Only a CSE reply that is awaiting review can be checked.");
            return AiInsightDto.From(insight);
        });

    /// <summary>AI pre-check of the signed form / documents the CSE sent back (Operations).</summary>
    [HttpPost("cases/{referenceId}/document-check")]
    public Task<IActionResult> DocumentCheck(string referenceId, CancellationToken ct) =>
        Run(async user =>
        {
            await RequireOperationsOnCase(referenceId, user);
            if (!user.Has(Permissions.CasesViewSensitive))
                throw new UnauthorizedAccessException("Checking documents requires permission to see client identifiers.");
            var insight = await _automation.RunDocumentCheckAsync(referenceId, user.UserId, manual: true, ct)
                          ?? throw new ArgumentException("Nothing to check.");
            return AiInsightDto.From(insight);
        });

    /// <summary>Apply a pending suggestion (Suggest-only mode). Runs as the user, with their permissions.</summary>
    [HttpPost("insights/{id:long}/apply")]
    public async Task<IActionResult> Apply(long id)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var result = await _automation.ApplyAsync(id, user);
        return result.Success ? Ok(new { message = result.Value }) : StatusCode(result.StatusCode, new { message = result.Error });
    }

    [HttpPost("insights/{id:long}/used")]
    public async Task<IActionResult> Used(long id)
    {
        var user = await _access.GetRequiredCurrentAsync();
        if (!(user.InWorkspace(WorkspaceTypes.Operations) && user.Has(Permissions.WorkflowOperations)))
            return StatusCode(403, new { message = "Only Operations can act on AI suggestions." });
        return await _automation.MarkUsedAsync(id, user) ? NoContent() : NotFound();
    }

    [HttpPost("insights/{id:long}/dismiss")]
    public async Task<IActionResult> Dismiss(long id)
    {
        var user = await _access.GetRequiredCurrentAsync();
        if (!(user.InWorkspace(WorkspaceTypes.Operations) && user.Has(Permissions.WorkflowOperations)))
            return StatusCode(403, new { message = "Only Operations can dismiss AI suggestions." });
        return await _automation.DismissAsync(id, user) ? NoContent() : NotFound();
    }

    [HttpPost("case-summary")]
    public Task<IActionResult> CaseSummary(CaseSummaryRequest request, CancellationToken ct) =>
        Run(async user => await _ai.SummarizeCaseAsync(request, user, ct));

    [HttpPost("mis-insights")]
    [HasPermission(Permissions.ReportsView)]
    public Task<IActionResult> MisInsights(MisInsightsRequest request, CancellationToken ct) =>
        Run(async user => await _ai.MisInsightsAsync(request, user, ct));

    [HttpPost("feedback")]
    public async Task<IActionResult> Feedback(AiFeedbackRequest request)
    {
        var user = await _access.GetRequiredCurrentAsync();
        return await _ai.RecordFeedbackAsync(request.InteractionId, request.Accepted, user) ? NoContent() : NotFound();
    }

    private async Task RequireOperationsOnCase(string referenceId, UserAccessProfile user)
    {
        if (!await _caseAccess.CanViewCaseAsync(referenceId, user)) throw new KeyNotFoundException("Case not found.");
        if (!(user.InWorkspace(WorkspaceTypes.Operations) && user.Has(Permissions.WorkflowOperations)))
            throw new UnauthorizedAccessException("Only Operations can run this check.");
    }

    private async Task<IActionResult> Run(Func<UserAccessProfile, Task<object>> action)
    {
        var user = await _access.GetRequiredCurrentAsync();
        try
        {
            return Ok(await action(user));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { message = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (AiUnavailableException ex) { return StatusCode(503, new { message = ex.Message, code = "ai_unavailable" }); }
        catch (AiOutputException ex) { return StatusCode(502, new { message = $"{ex.Message} Please try again or continue without AI.", code = "ai_invalid_output" }); }
    }
}
