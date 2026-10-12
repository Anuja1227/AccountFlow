using AccountOpeningTracker.Api.Ai;
using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Controllers;

public sealed record RequestAttachmentDto(int Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAt);

public sealed record CaseRequestDto(
    int Id, string CseUserName, string CseName, string Details, string Status, DateTime SubmittedAt,
    string? ReferenceId, string? LoggedByName, DateTime? LoggedAt, List<RequestAttachmentDto> Attachments);

/// <summary>
/// Stage 1 intake: "CSE sends request to Operations". A CSE sends the request
/// details and attachments; Operations sees them under Incoming requests and
/// makes the entry in the tracker (POST /api/accounts with SourceRequestId).
/// </summary>
[ApiController]
[Authorize]
[Route("api/requests")]
public class RequestsController : ControllerBase
{
    public const int MaxDetailsLength = 4000;

    private readonly ApplicationDbContext _db;
    private readonly IAccessService _access;
    private readonly AiService _ai;

    public RequestsController(ApplicationDbContext db, IAccessService access, AiService ai)
    {
        _db = db;
        _access = access;
        _ai = ai;
    }

    private static bool IsOperations(UserAccessProfile u) =>
        u.InWorkspace(WorkspaceTypes.Operations) && u.Has(Permissions.CasesCreate);

    private static bool IsSubmittingCse(UserAccessProfile u) =>
        u.InWorkspace(WorkspaceTypes.Cse) && u.Has(Permissions.CasesSubmit);

    /// <summary>Operations sees every request; a CSE sees only their own.</summary>
    private IQueryable<CaseRequest> Scope(UserAccessProfile u)
    {
        var q = _db.CaseRequests.AsNoTracking();
        if (IsOperations(u)) return q;
        if (IsSubmittingCse(u)) return q.Where(r => r.CseUserId == u.UserId);
        return q.Where(_ => false);
    }

    /// <summary>Projection that never loads file bytes.</summary>
    private static IQueryable<CaseRequestDto> Project(IQueryable<CaseRequest> q) =>
        q.Select(r => new CaseRequestDto(
            r.Id, r.CseUserName, r.CseName, r.Details, r.Status, r.SubmittedAt, r.ReferenceId, r.LoggedByName, r.LoggedAt,
            r.Attachments.OrderBy(a => a.Id)
                .Select(a => new RequestAttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedAt)).ToList()));

    private static CaseRequestDto ToDto(CaseRequest r) => new(
        r.Id, r.CseUserName, r.CseName, r.Details, r.Status, r.SubmittedAt, r.ReferenceId, r.LoggedByName, r.LoggedAt,
        r.Attachments.OrderBy(a => a.Id)
            .Select(a => new RequestAttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedAt)).ToList());

    // POST api/requests  (multipart/form-data: details, files[])
    [HttpPost]
    [HasPermission(Permissions.CasesSubmit)]
    [RequestSizeLimit(Services.FileUploads.MaxRequestBytes)]
    public async Task<IActionResult> Submit([FromForm] string details, [FromForm] List<IFormFile>? files, CancellationToken ct)
    {
        var user = await _access.GetRequiredCurrentAsync();
        if (!IsSubmittingCse(user))
            return StatusCode(403, new { message = "Only CSE team members can send requests to Operations." });
        if (string.IsNullOrWhiteSpace(user.CseName))
            return StatusCode(403, new { message = "Your account is not linked to a CSE name. Ask an administrator to set it." });

        details = details?.Trim() ?? string.Empty;
        if (details.Length < 10) return BadRequest(new { message = "Describe the request (client, account type, channel and anything pending)." });
        if (details.Length > MaxDetailsLength) return BadRequest(new { message = $"Details must be at most {MaxDetailsLength} characters." });

        var (uploads, uploadError) = await Services.FileUploads.ReadAsync(files, ct);
        if (uploadError != null) return BadRequest(new { message = uploadError });

        var request = new CaseRequest
        {
            CseUserId = user.UserId,
            CseUserName = user.DisplayName,
            CseName = user.CseName!,
            Details = details,
        };
        foreach (var f in uploads)
        {
            request.Attachments.Add(new RequestAttachment
            {
                FileName = f.FileName,
                ContentType = f.ContentType,
                SizeBytes = f.Data.LongLength,
                Data = f.Data
            });
        }

        _db.CaseRequests.Add(request);
        await _db.SaveChangesAsync(ct);

        _db.AuditEvents.Add(new AuditEvent
        {
            UserId = user.UserId,
            UserName = user.DisplayName,
            Team = WorkspaceTypes.Cse,
            Action = "CSE sent request to Operations",
            Details = $"Request #{request.Id} with {request.Attachments.Count} attachment(s)."
        });
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(request));
    }

    // GET api/requests?status=Submitted
    [HttpGet]
    public async Task<ActionResult<List<CaseRequestDto>>> List([FromQuery] string? status)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var q = Scope(user);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        return Ok(await Project(q.OrderByDescending(r => r.SubmittedAt).Take(300)).ToListAsync());
    }

    // GET api/requests/5
    [HttpGet("{id:int}")]
    public async Task<ActionResult<CaseRequestDto>> Get(int id)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var r = await Project(Scope(user).Where(x => x.Id == id)).FirstOrDefaultAsync();
        return r == null ? NotFound(new { message = "Request not found." }) : Ok(r);
    }

    // GET api/requests/5/attachments/7  — view/download a file
    [HttpGet("{id:int}/attachments/{attachmentId:int}")]
    public async Task<IActionResult> Download(int id, int attachmentId)
    {
        var user = await _access.GetRequiredCurrentAsync();
        bool visible = await Scope(user).AnyAsync(r => r.Id == id);
        if (!visible)
        {
            // Also allowed for anyone who can see the case the request was logged into.
            var refId = await _db.CaseRequests.Where(r => r.Id == id).Select(r => r.ReferenceId).FirstOrDefaultAsync();
            var caseAccess = HttpContext.RequestServices.GetRequiredService<Services.CaseAccessService>();
            visible = refId != null && await caseAccess.CanViewCaseAsync(refId, user);
        }
        if (!visible) return NotFound();
        if (!user.Has(Permissions.CasesViewSensitive))
            return StatusCode(403, new { message = "Viewing documents requires permission to see client identifiers." });

        var file = await _db.RequestAttachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.CaseRequestId == id);
        if (file == null) return NotFound();
        return File(file.Data, file.ContentType, enableRangeProcessing: false);
    }

    // POST api/requests/5/extract — AI fills the Log-inward-request form from the request text (Operations)
    [HttpPost("{id:int}/extract")]
    [HasPermission(Permissions.AiUse)]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> Extract(int id, CancellationToken ct)
    {
        var user = await _access.GetRequiredCurrentAsync();
        if (!IsOperations(user)) return StatusCode(403, new { message = "Only Operations can log requests." });
        var r = await _db.CaseRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r == null) return NotFound(new { message = "Request not found." });

        try
        {
            var result = await _ai.ExtractRequestAsync(
                new ExtractRequestInput { Text = $"CSE: {r.CseName}\n{r.Details}" }, user, ct);
            // The CSE is known from the sender, not guessed.
            result.Fields["cse"] = r.CseName;
            return Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (AiUnavailableException ex) { return StatusCode(503, new { message = ex.Message, code = "ai_unavailable" }); }
        catch (AiOutputException ex) { return StatusCode(502, new { message = $"{ex.Message} Please fill the form manually.", code = "ai_invalid_output" }); }
    }
}
