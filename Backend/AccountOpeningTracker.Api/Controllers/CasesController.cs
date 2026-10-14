using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using AccountOpeningTracker.Api.Services;
using AccountOpeningTracker.Api.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Controllers;

/// <summary>Case detail, workflow actions and inter-team queries.</summary>
[ApiController]
[Authorize]
[Route("api/cases")]
public class CasesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IAccessService _access;
    private readonly CaseAccessService _caseAccess;
    private readonly CaseService _cases;
    private readonly AiAutomationService _automation;
    private readonly ILogger<CasesController> _log;

    public CasesController(ApplicationDbContext db, IAccessService access, CaseAccessService caseAccess, CaseService cases,
        AiAutomationService automation, ILogger<CasesController> log)
    {
        _db = db;
        _access = access;
        _caseAccess = caseAccess;
        _cases = cases;
        _automation = automation;
        _log = log;
    }

    // GET: api/cases/REF-001
    [HttpGet("{referenceId}")]
    public async Task<ActionResult<CaseDetailDto>> Get(string referenceId)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var detail = await _cases.GetCaseDetailAsync(referenceId, user);
        return detail == null ? NotFound(new { message = "Case not found." }) : Ok(detail);
    }

    // PUT: api/cases/REF-001/details — general information (Operations)
    [HttpPut("{referenceId}/details")]
    [HasPermission(Permissions.CasesEdit)]
    public async Task<IActionResult> UpdateDetails(string referenceId, UpdateCaseDetailsRequest request)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var result = await _cases.UpdateDetailsAsync(referenceId, request, user);
        if (!result.Success) return StatusCode(result.StatusCode, new { message = result.Error });
        return Ok(await _cases.GetCaseDetailAsync(referenceId, user));
    }

    // POST: api/cases/REF-001/actions/raise-stage1-query  ("action" is a reserved route name, hence actionCode)
    [HttpPost("{referenceId}/actions/{actionCode}")]
    public async Task<IActionResult> RunAction(string referenceId, string actionCode, WorkflowInput input, CancellationToken ct)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var result = await _cases.ExecuteActionAsync(referenceId, actionCode, input, user);
        if (!result.Success) return StatusCode(result.StatusCode, new { message = result.Error });
        await AfterActionAsync(referenceId, actionCode, input, user, ct);
        return Ok(await _cases.GetCaseDetailAsync(referenceId, user));
    }

    // POST: api/cases/REF-001/actions/cse-send-signed-form/upload
    // multipart/form-data: "input" (the same JSON as the plain action) + "files"
    [HttpPost("{referenceId}/actions/{actionCode}/upload")]
    [RequestSizeLimit(FileUploads.MaxRequestBytes)]
    public async Task<IActionResult> RunActionWithFiles(string referenceId, string actionCode,
        [FromForm] string? input, [FromForm] List<IFormFile>? files, CancellationToken ct)
    {
        WorkflowInput parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(input)
                ? new WorkflowInput()
                : System.Text.Json.JsonSerializer.Deserialize<WorkflowInput>(input,
                      new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new WorkflowInput();
        }
        catch (System.Text.Json.JsonException)
        {
            return BadRequest(new { message = "Invalid action input." });
        }

        var (uploads, uploadError) = await FileUploads.ReadAsync(files, ct);
        if (uploadError != null) return BadRequest(new { message = uploadError });

        var user = await _access.GetRequiredCurrentAsync();
        var result = await _cases.ExecuteActionAsync(referenceId, actionCode, parsed, user, uploads);
        if (!result.Success) return StatusCode(result.StatusCode, new { message = result.Error });
        await AfterActionAsync(referenceId, actionCode, parsed, user, ct, uploads.Count);
        return Ok(await _cases.GetCaseDetailAsync(referenceId, user));
    }

    private async Task AfterActionAsync(string referenceId, string actionCode, WorkflowInput input, UserAccessProfile user, CancellationToken ct,
        int fileCount = 0)
    {
        // AI automation #3: review the CSE's reply → accept or follow up.
        if (actionCode == ActionCodes.CseRespond && input.QueryId.HasValue)
        {
            try
            {
                await _automation.RunResponseReviewAsync(referenceId, input.QueryId.Value, user.UserId, manual: false, ct,
                    documentsAttached: fileCount > 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "AI response review failed for {Ref}", referenceId);
            }
        }

        // AI pre-check of returned documents (flags issues only; Operations decides).
        if (fileCount > 0 && actionCode is ActionCodes.CseSendSignedForm or ActionCodes.RecordSignedForm or ActionCodes.CseRespond)
        {
            try
            {
                await _automation.RunDocumentCheckAsync(referenceId, user.UserId, manual: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "AI document check failed for {Ref}", referenceId);
            }
        }
    }

    // GET: api/cases/queries?box=inbox|outbox|all&status=Open
    [HttpGet("queries")]
    public async Task<ActionResult<List<QueryDto>>> Queries([FromQuery] string? box, [FromQuery] string? status)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var teams = user.Workspaces.ToList();

        var q = _caseAccess.ScopeQueries(_db.CaseQueries.AsNoTracking().Include(x => x.Messages), user);
        if (string.Equals(box, "inbox", StringComparison.OrdinalIgnoreCase))
            q = q.Where(x => teams.Contains(x.ToTeam));
        else if (string.Equals(box, "outbox", StringComparison.OrdinalIgnoreCase))
            q = q.Where(x => teams.Contains(x.FromTeam));
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(x => x.Status == status);

        var queries = await q.OrderByDescending(x => x.LastOpenedAt).ThenByDescending(x => x.Id).Take(500).ToListAsync();

        var refs = queries.Select(x => x.ReferenceId).Distinct().ToList();
        var records = await _db.Accounts.AsNoTracking().Where(a => refs.Contains(a.ReferenceId)).ToListAsync();
        var latestByRef = CaseCalculator.LatestPerCase(records).ToDictionary(a => a.ReferenceId);

        bool sensitive = user.Has(Permissions.CasesViewSensitive);
        return Ok(queries.Select(x => QueryDto.From(x, latestByRef.GetValueOrDefault(x.ReferenceId), sensitive)).ToList());
    }
}

/// <summary>Reference data for forms and filters.</summary>
[ApiController]
[Authorize]
[Route("api/lookups")]
public class LookupsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IAccessService _access;
    private readonly CaseAccessService _caseAccess;
    private readonly ISettingsService _settings;

    public LookupsController(ApplicationDbContext db, IAccessService access, CaseAccessService caseAccess, ISettingsService settings)
    {
        _db = db;
        _access = access;
        _caseAccess = caseAccess;
        _settings = settings;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var user = await _access.GetRequiredCurrentAsync();
        var scoped = _caseAccess.ScopeRecords(_db.Accounts.AsNoTracking(), user);

        var cses = await scoped.Where(a => a.Cse != "").Select(a => a.Cse).Distinct().OrderBy(x => x).ToListAsync();
        var locations = await scoped.Where(a => a.Location != "").Select(a => a.Location).Distinct().OrderBy(x => x).ToListAsync();
        var segments = await scoped.Where(a => a.Segment != "").Select(a => a.Segment).Distinct().OrderBy(x => x).ToListAsync();

        return Ok(new
        {
            statuses = WorkflowStatuses.All.Select(s => new { status = s, stage = WorkflowStatuses.StageOf(s) }),
            stages = MisCalculator.StageNames,
            entryTypes = new[] { "New", "Resubmission", "Discrepancy Resolution", "Modification" },
            accountTypes = new[] { "Individual", "HUF", "Corporate", "NRI", "Minor" },
            channels = new[] { "Physical", "Digital" },
            segmentOptions = new[] { "Retail", "HNI", "Ultra HNI", "Institutional", "Corporate" }
                .Union(segments).Distinct().ToList(),
            discrepancyTypes = WorkflowEngine.DiscrepancyTypes,
            cses,
            locations,
            segments,
            slaThresholdWorkingDays = await _settings.GetSlaThresholdAsync()
        });
    }
}
