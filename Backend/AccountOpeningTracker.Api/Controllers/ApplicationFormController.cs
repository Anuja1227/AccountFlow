using System.Text.Json;
using AccountOpeningTracker.Api.Ai;
using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using AccountOpeningTracker.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Controllers;

public sealed record FormCaseInfo(
    string ReferenceId, string RequestId, string ClientName, string PanNo, string AccountType, string Channel,
    string Segment, string Location, string RelationshipManager, string Cse, string LatestStatus);

public sealed record ApplicationFormDto(
    string ReferenceId,
    bool Saved,
    string Status,
    Dictionary<string, string> Data,
    List<string> AiFilled,
    List<string> Missing,
    List<string> Errors,
    string? UpdatedByName,
    DateTime? UpdatedAt,
    DateTime? SentAt,
    bool CanEdit,
    FormCaseInfo Case,
    List<FormFieldDefinition> Fields);

public sealed class SaveApplicationFormRequest
{
    public Dictionary<string, string?> Data { get; set; } = new();
    public List<string>? AiFilled { get; set; }
}

/// <summary>Stage 2: the application form Operations prepares, checks and sends for signature.</summary>
[ApiController]
[Authorize]
[Route("api/cases/{referenceId}")]
public class ApplicationFormController : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApplicationDbContext _db;
    private readonly IAccessService _access;
    private readonly CaseAccessService _caseAccess;
    private readonly AiService _ai;

    public ApplicationFormController(ApplicationDbContext db, IAccessService access, CaseAccessService caseAccess, AiService ai)
    {
        _db = db;
        _access = access;
        _caseAccess = caseAccess;
        _ai = ai;
    }

    private static bool CanPrepare(UserAccessProfile u) =>
        u.InWorkspace(WorkspaceTypes.Operations) && u.Has(Permissions.WorkflowOperations);

    private async Task<(UserAccessProfile User, Account? Latest, IActionResult? Error)> LoadAsync(string referenceId)
    {
        var user = await _access.GetRequiredCurrentAsync();
        if (!await _caseAccess.CanViewCaseAsync(referenceId, user))
            return (user, null, NotFound(new { message = "Case not found." }));
        if (!user.Has(Permissions.CasesViewSensitive))
            return (user, null, StatusCode(403, new { message = "The application form contains client details you do not have permission to see." }));

        var records = await _db.Accounts.AsNoTracking().Where(a => a.ReferenceId == referenceId).ToListAsync();
        if (records.Count == 0) return (user, null, NotFound(new { message = "Case not found." }));
        return (user, CaseCalculator.Latest(records), null);
    }

    private static ApplicationFormDto ToDto(Account latest, ApplicationForm? form, UserAccessProfile user)
    {
        var data = ApplicationFormRules.Parse(form?.DataJson);
        var today = CaseService.Today;
        bool editable = CanPrepare(user) &&
                        latest.LatestStatus == WorkflowStatuses.FormUnderPreparation &&
                        form?.Status != ApplicationFormStatuses.Sent;
        return new ApplicationFormDto(
            latest.ReferenceId,
            form != null,
            form?.Status ?? ApplicationFormStatuses.Draft,
            data,
            ApplicationFormRules.ParseList(form?.AiFilledJson),
            ApplicationFormRules.Missing(data, latest.AccountType, today),
            ApplicationFormRules.FormatErrors(data, today),
            form?.UpdatedByName,
            form?.UpdatedAt,
            form?.SentAt,
            editable,
            new FormCaseInfo(latest.ReferenceId, latest.RequestId, latest.ClientName, latest.PanNo, latest.AccountType,
                latest.Channel, latest.Segment, latest.Location, latest.RelationshipManager, latest.Cse, latest.LatestStatus),
            ApplicationFormRules.FieldsFor(latest.AccountType).ToList());
    }

    // GET api/cases/REF/form
    [HttpGet("form")]
    public async Task<IActionResult> Get(string referenceId)
    {
        var (user, latest, error) = await LoadAsync(referenceId);
        if (error != null) return error;
        var form = await _db.ApplicationForms.AsNoTracking().FirstOrDefaultAsync(f => f.ReferenceId == referenceId);
        return Ok(ToDto(latest!, form, user));
    }

    // PUT api/cases/REF/form — save (draft; it may still be incomplete)
    [HttpPut("form")]
    public async Task<IActionResult> Save(string referenceId, SaveApplicationFormRequest request)
    {
        var (user, latest, error) = await LoadAsync(referenceId);
        if (error != null) return error;
        if (!CanPrepare(user))
            return StatusCode(403, new { message = "Only Operations can prepare the application form." });
        if (latest!.LatestStatus != WorkflowStatuses.FormUnderPreparation)
            return Conflict(new { message = $"The form can only be edited while the case is '{WorkflowStatuses.FormUnderPreparation}'." });

        var form = await _db.ApplicationForms.FirstOrDefaultAsync(f => f.ReferenceId == referenceId);
        if (form?.Status == ApplicationFormStatuses.Sent)
            return Conflict(new { message = "This form has already been sent for signature." });

        var data = ApplicationFormRules.Normalize(request.Data);
        var errors = ApplicationFormRules.FormatErrors(data, CaseService.Today);
        if (errors.Count > 0) return BadRequest(new { message = string.Join(" ", errors) });

        // Guardian fields only apply to minor accounts.
        if (!ApplicationFormRules.IsMinor(latest.AccountType))
            foreach (var k in ApplicationFormRules.Fields.Where(f => f.MinorOnly).Select(f => f.Key)) data.Remove(k);

        var aiFilled = (request.AiFilled ?? new()).Where(k => data.ContainsKey(k)).Distinct().ToList();
        bool created = form == null;
        form ??= new ApplicationForm { ReferenceId = referenceId };
        form.DataJson = JsonSerializer.Serialize(data, Json);
        form.AiFilledJson = JsonSerializer.Serialize(aiFilled, Json);
        form.UpdatedByUserId = user.UserId;
        form.UpdatedByName = user.DisplayName;
        form.UpdatedAt = DateTime.UtcNow;
        if (created) _db.ApplicationForms.Add(form);

        // Stage 2 "Form Prepared Date": the day Operations first saves the form.
        if (!latest.FormPreparedDate.HasValue)
        {
            _db.Accounts.Attach(latest);
            latest.FormPreparedDate = CaseService.Today;
            latest.UpdatedAt = DateTime.UtcNow;
        }

        var missing = ApplicationFormRules.Missing(data, latest.AccountType, CaseService.Today);
        _db.AuditEvents.Add(new AuditEvent
        {
            AccountId = latest.Id,
            ReferenceId = referenceId,
            UserId = user.UserId,
            UserName = user.DisplayName,
            Team = WorkspaceTypes.Operations,
            Action = created ? "Prepared application form" : "Edited application form",
            Details = (missing.Count == 0 ? "Form complete." : $"{missing.Count} required field(s) still empty.") +
                      (aiFilled.Count > 0 ? $" {aiFilled.Count} field(s) filled by AI and checked by the user." : "")
        });
        await _db.SaveChangesAsync();
        return Ok(ToDto(latest, form, user));
    }

    // POST api/cases/REF/form/ai-fill — AI reads what the CSE sent and suggests values (not saved)
    [HttpPost("form/ai-fill")]
    [HasPermission(Permissions.AiUse)]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> AiFill(string referenceId, CancellationToken ct)
    {
        var (user, latest, error) = await LoadAsync(referenceId);
        if (error != null) return error;
        if (!CanPrepare(user)) return StatusCode(403, new { message = "Only Operations can prepare the application form." });
        if (latest!.LatestStatus != WorkflowStatuses.FormUnderPreparation)
            return Conflict(new { message = "The form can only be filled while it is being prepared." });

        try
        {
            return Ok(await _ai.FillApplicationFormAsync(referenceId, user, ct));
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (AiUnavailableException ex) { return StatusCode(503, new { message = ex.Message, code = "ai_unavailable" }); }
        catch (AiOutputException ex) { return StatusCode(502, new { message = $"{ex.Message} Please fill the form manually.", code = "ai_invalid_output" }); }
    }

    // GET api/cases/REF/documents/7 — view/download a document uploaded on the case
    [HttpGet("documents/{documentId:int}")]
    public async Task<IActionResult> Document(string referenceId, int documentId)
    {
        var (_, _, error) = await LoadAsync(referenceId);
        if (error != null) return error;
        var doc = await _db.CaseDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.ReferenceId == referenceId);
        if (doc == null) return NotFound();
        return File(doc.Data, doc.ContentType, enableRangeProcessing: false);
    }
}
