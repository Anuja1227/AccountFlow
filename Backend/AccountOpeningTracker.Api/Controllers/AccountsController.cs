using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using AccountOpeningTracker.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Controllers;

/// <summary>
/// Account records and MIS. Every endpoint requires a signed-in user and
/// every query is limited to the cases that user may see.
/// </summary>
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class AccountsController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IAccessService _access;
    private readonly CaseAccessService _caseAccess;
    private readonly CaseService _cases;
    private readonly ISettingsService _settings;
    private readonly AiAutomationService _automation;
    private readonly ILogger<AccountsController> _log;

    public AccountsController(
        ApplicationDbContext context,
        IAccessService access,
        CaseAccessService caseAccess,
        CaseService cases,
        ISettingsService settings,
        AiAutomationService automation,
        ILogger<AccountsController> log)
    {
        _context = context;
        _access = access;
        _caseAccess = caseAccess;
        _cases = cases;
        _settings = settings;
        _automation = automation;
        _log = log;
    }

    // GET: api/accounts/mis?cse=...&location=...&segment=...
    [HttpGet("mis")]
    [HasPermission(Permissions.ReportsView)]
    public async Task<ActionResult<MisReport>> GetMis(
        [FromQuery] string? cse,
        [FromQuery] string? location,
        [FromQuery] string? segment)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var records = await _caseAccess.ScopeRecords(_context.Accounts.AsNoTracking(), user).ToListAsync();

        int sla = await _settings.GetSlaThresholdAsync();
        CaseCalculator.RefreshForRead(records, CaseService.Today, sla);

        return Ok(MisCalculator.Build(records, new MisFilters(cse, location, segment), sla));
    }

    // GET: api/accounts?view=cases|records&actionBy=...&stage=...&openOnly=true
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AccountDto>>> GetAccounts(
        [FromQuery] string? view,
        [FromQuery] string? actionBy,
        [FromQuery] string? stage,
        [FromQuery] bool openOnly = false)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var records = await _caseAccess.ScopeRecords(_context.Accounts.AsNoTracking(), user)
            .OrderByDescending(a => a.InwardDate).ThenByDescending(a => a.Id)
            .ToListAsync();

        CaseCalculator.RefreshForRead(records, CaseService.Today, await _settings.GetSlaThresholdAsync());

        var openQueries = await _caseAccess.ScopeQueries(_context.CaseQueries.AsNoTracking(), user)
            .Where(q => q.Status != QueryStatuses.Resolved)
            .GroupBy(q => q.ReferenceId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

        var dtos = AccountDto.FromRecords(records, user.Has(Permissions.CasesViewSensitive), openQueries);

        IEnumerable<AccountDto> result = dtos;
        if (string.Equals(view, "cases", StringComparison.OrdinalIgnoreCase))
            result = result.Where(d => d.IsLatestRecord);
        if (!string.IsNullOrWhiteSpace(actionBy))
            result = result.Where(d => d.IsLatestRecord && d.ActionBy == actionBy);
        if (!string.IsNullOrWhiteSpace(stage))
            result = result.Where(d => d.CurrentStage == stage);
        if (openOnly)
            result = result.Where(d => WorkflowStatuses.IsOpen(d));

        return Ok(result.ToList());
    }

    // GET: api/accounts/1
    [HttpGet("{id:int}")]
    public async Task<ActionResult<AccountDto>> GetAccount(int id)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var account = await _caseAccess.ScopeRecords(_context.Accounts.AsNoTracking(), user)
            .FirstOrDefaultAsync(a => a.Id == id);

        // 404 (not 403) so users cannot probe for cases outside their scope.
        if (account == null)
            return NotFound(new { message = $"Account with ID {id} was not found." });

        var related = await _context.Accounts.AsNoTracking()
            .Where(a => a.ReferenceId == account.ReferenceId).ToListAsync();
        CaseCalculator.RefreshForRead(related, CaseService.Today, await _settings.GetSlaThresholdAsync());

        var dto = AccountDto.FromRecords(related, user.Has(Permissions.CasesViewSensitive))
            .First(d => d.Id == id);
        return Ok(dto);
    }

    // POST: api/accounts — log an inward request (or a resubmission/modification record).
    [HttpPost]
    [HasPermission(Permissions.CasesCreate)]
    public async Task<ActionResult<Account>> CreateAccount(CreateAccountRequest request, CancellationToken ct)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var result = await _cases.CreateAsync(request, user);
        if (!result.Success)
            return StatusCode(result.StatusCode, new { message = result.Error });

        // AI automation #2: completeness check → raises the Stage 1 query itself
        // (or moves a complete request on). Best-effort: the case is already saved.
        if (result.Value!.EntryType == "New" && result.Value.LatestStatus == WorkflowStatuses.RequestReceived)
        {
            try
            {
                await _automation.RunIntakeCheckAsync(result.Value.ReferenceId, user.UserId, manual: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "AI intake check failed for {Ref}", result.Value.ReferenceId);
            }
        }

        var dto = AccountDto.From(result.Value, user.Has(Permissions.CasesViewSensitive));
        return CreatedAtAction(nameof(GetAccount), new { id = dto.Id }, dto);
    }

    // PUT: api/accounts/4 — direct correction of every field, bypassing the
    // state machine. Restricted to workflow.override and fully audited.
    [HttpPut("{id:int}")]
    [HasPermission(Permissions.WorkflowOverride)]
    public async Task<ActionResult<Account>> UpdateAccount(int id, UpdateAccountRequest request)
    {
        var user = await _access.GetRequiredCurrentAsync();
        var account = await _caseAccess.ScopeRecords(_context.Accounts, user)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (account == null)
            return NotFound(new { message = $"Account with ID {id} was not found." });

        if (string.IsNullOrWhiteSpace(request.LatestStatus) ||
            !WorkflowStatuses.All.Contains(request.LatestStatus))
        {
            return BadRequest(new { message = "Invalid Latest Status.", allowedStatuses = WorkflowStatuses.All });
        }

        var validationError = WorkflowValidationHelper.Validate(
            request.AccountType, request.Channel, request.ActionBy, request.DiscrepancyType,
            request.Stage4ReviewOutcome, account.InwardDate, request.AccountOpeningDate,
            request.DiscrepancyRaisedDate, request.DiscrepancyResolutionReceivedDate,
            request.SubmittedToMofslDate, request.MofslQueryRaisedDate, request.MofslQueryResolvedDate);

        if (validationError != null)
            return BadRequest(new { message = validationError });

        var fromStatus = account.LatestStatus;

        // ReferenceId, RequestId and InwardDate cannot be changed (case identity).
        account.EntryType = request.EntryType;
        account.ClientName = request.ClientName;
        account.PanNo = request.PanNo;
        account.AccountType = request.AccountType;
        account.Channel = request.Channel;
        account.ActionBy = request.ActionBy;
        account.Location = request.Location;
        account.Segment = request.Segment;
        account.Cse = request.Cse;
        account.RelationshipManager = request.RelationshipManager;
        account.Processor = request.Processor;
        account.OutwardDate = request.OutwardDate;
        account.ResubmissionDate = request.ResubmissionDate;

        account.Stage1QueryRaisedDate = request.Stage1QueryRaisedDate;
        account.Stage1QueryDetails = request.Stage1QueryDetails;
        account.Stage1ResubmissionDate = request.Stage1ResubmissionDate;
        account.Stage1Status = request.Stage1Status;

        account.FormPreparedDate = request.FormPreparedDate;
        account.PhysicalFormSubmittedToCseDate = request.PhysicalFormSubmittedToCseDate;
        account.DigitalFormSentToClientDate = request.DigitalFormSentToClientDate;
        account.Stage2Status = request.Stage2Status;

        account.SignedFormReceivedDate = request.SignedFormReceivedDate;
        account.DiscrepancyRaisedDate = request.DiscrepancyRaisedDate;
        account.DiscrepancyType = request.DiscrepancyType;
        account.FormReturnedToCseDate = request.FormReturnedToCseDate;
        account.Stage3Status = request.Stage3Status;

        account.DiscrepancyResolutionReceivedDate = request.DiscrepancyResolutionReceivedDate;
        account.ResubmittedFormReceivedDate = request.ResubmittedFormReceivedDate;
        account.Stage4ReviewOutcome = request.Stage4ReviewOutcome;
        account.Stage4Status = request.Stage4Status;

        account.SubmittedToMofslDate = request.SubmittedToMofslDate;
        account.MofslQueryRaisedDate = request.MofslQueryRaisedDate;
        account.MofslQueryType = request.MofslQueryType;
        account.MofslQueryResolvedDate = request.MofslQueryResolvedDate;
        account.Stage5Status = request.Stage5Status;

        account.AccountNumber = request.AccountNumber;
        account.AccountOpeningDate = request.AccountOpeningDate;
        account.Stage6Status = request.Stage6Status;

        account.LatestStatus = request.LatestStatus;
        account.QueryDetails = request.QueryDetails;
        if (request.QueryCount.HasValue)
            account.QueryCount = request.QueryCount.Value;
        account.QueryHoldDays = request.QueryHoldDays;
        account.TouchCount = request.TouchCount;
        account.Remarks = request.Remarks;
        account.UpdatedAt = DateTime.UtcNow;

        var relatedAccounts = await _context.Accounts
            .Where(a => a.ReferenceId == account.ReferenceId)
            .ToListAsync();

        CaseCalculator.Recalculate(relatedAccounts, CaseService.Today, await _settings.GetSlaThresholdAsync());
        await _context.SaveChangesAsync();

        await _cases.AuditAsync(account, user, "Workflow override (direct record correction)",
            fromStatus, account.LatestStatus, $"Record {account.Id} corrected via override.");

        return Ok(AccountDto.From(account, user.Has(Permissions.CasesViewSensitive)));
    }
}
