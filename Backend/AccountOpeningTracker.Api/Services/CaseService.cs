using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using AccountOpeningTracker.Api.Workflow;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Services;

public sealed class ServiceResult<T>
{
    public T? Value { get; init; }
    public string? Error { get; init; }
    public int StatusCode { get; init; } = 200;
    public bool Success => Error == null;

    public static ServiceResult<T> Ok(T value) => new() { Value = value };
    public static ServiceResult<T> Fail(string error, int status = 400) => new() { Error = error, StatusCode = status };
}

/// <summary>Loads cases with access checks, runs workflow actions, and writes audit history.</summary>
public sealed class CaseService
{
    private readonly ApplicationDbContext _db;
    private readonly CaseAccessService _access;
    private readonly ISettingsService _settings;

    public CaseService(ApplicationDbContext db, CaseAccessService access, ISettingsService settings)
    {
        _db = db;
        _access = access;
        _settings = settings;
    }

    public static DateTime Today => DateTime.Today;

    /// <summary>Records of one case, or null when it does not exist / is not visible.</summary>
    public async Task<List<Account>?> LoadCaseRecordsAsync(string referenceId, UserAccessProfile user, bool tracking = false)
    {
        if (!await _access.CanViewCaseAsync(referenceId, user)) return null;

        var q = _db.Accounts.Where(a => a.ReferenceId == referenceId);
        if (!tracking) q = q.AsNoTracking();
        var records = await q.OrderBy(a => a.InwardDate).ThenBy(a => a.Id).ToListAsync();
        return records.Count == 0 ? null : records;
    }

    public async Task<List<CaseQuery>> LoadQueriesAsync(string referenceId, bool tracking = false)
    {
        var q = _db.CaseQueries.Include(x => x.Messages).Where(x => x.ReferenceId == referenceId);
        if (!tracking) q = q.AsNoTracking();
        return await q.OrderBy(x => x.Id).ToListAsync();
    }

    /// <summary>Actions this user can run now: structurally possible AND permitted AND on the right team.</summary>
    public static List<AvailableAction> ActionsForUser(WorkflowContext ctx, UserAccessProfile user) =>
        WorkflowEngine.GetAvailableActions(ctx)
            .Where(a => user.Has(a.Definition.Permission) && user.InWorkspace(a.Definition.Team))
            .ToList();

    /// <summary>Workflow precondition for "send form": null when the saved application form is complete.</summary>
    private async Task<string?> FormNotReadyAsync(string referenceId, Account latest)
    {
        if (latest.LatestStatus != WorkflowStatuses.FormUnderPreparation) return null;
        var form = await _db.ApplicationForms.AsNoTracking().FirstOrDefaultAsync(f => f.ReferenceId == referenceId);
        return ApplicationFormRules.NotReadyReason(form, latest.AccountType, Today);
    }

    public async Task<CaseDetailDto?> GetCaseDetailAsync(string referenceId, UserAccessProfile user)
    {
        var records = await LoadCaseRecordsAsync(referenceId, user);
        if (records == null) return null;

        var queries = await LoadQueriesAsync(referenceId);
        int sla = await _settings.GetSlaThresholdAsync();
        CaseCalculator.RefreshForRead(records, Today, sla);

        bool sensitive = user.Has(Permissions.CasesViewSensitive);
        var formNotReady = await FormNotReadyAsync(referenceId, CaseCalculator.Latest(records));
        var ctx = new WorkflowContext { Records = records, Queries = queries, Today = Today, FormNotReadyReason = formNotReady };
        var latest = ctx.Latest;

        var openByRef = new Dictionary<string, int>
        {
            [referenceId] = queries.Count(q => q.Status != QueryStatuses.Resolved)
        };
        var dtos = AccountDto.FromRecords(records, sensitive, openByRef);
        var latestDto = dtos.First(d => d.IsLatestRecord);

        var visibleQueries = queries
            .Where(q => CaseAccessService.CanSeeQuery(q, user))
            .Select(q => QueryDto.From(q, latest, sensitive))
            .ToList();

        var history = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.ReferenceId == referenceId)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .Take(200)
            .ToListAsync();

        var actions = ActionsForUser(ctx, user)
            .Select(a => new AvailableActionDto(
                a.Definition.Code, a.Definition.Label, a.Definition.Team, a.Definition.Description,
                a.Definition.Tone, a.QueryId, a.QueryLabel, a.Definition.Fields))
            .ToList();

        var attachments = await _db.RequestAttachments.AsNoTracking()
            .Where(a => a.CaseRequest!.ReferenceId == referenceId)
            .OrderBy(a => a.Id)
            .Select(a => new CaseAttachmentDto(a.CaseRequestId, a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedAt))
            .ToListAsync();

        var documents = await _db.CaseDocuments.AsNoTracking()
            .Where(d => d.ReferenceId == referenceId)
            .OrderBy(d => d.Id)
            .Select(d => new CaseDocumentDto(d.Id, d.QueryId, d.Purpose, d.FileName, d.ContentType, d.SizeBytes,
                d.UploadedByName, d.Team, d.UploadedAt))
            .ToListAsync();

        var formStatus = await _db.ApplicationForms.AsNoTracking()
            .Where(f => f.ReferenceId == referenceId).Select(f => f.Status).FirstOrDefaultAsync();

        var insights = await _db.AiCaseInsights.AsNoTracking()
            .Where(i => i.ReferenceId == referenceId)
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Take(30)
            .ToListAsync();

        return new CaseDetailDto(
            referenceId,
            latestDto,
            dtos,
            visibleQueries,
            history.Select(AuditEventDto.From).ToList(),
            actions,
            insights.Select(AiInsightDto.From).ToList(),
            attachments,
            documents,
            formStatus,
            formNotReady,
            await _settings.GetAutomationModeAsync(),
            latest.OriginalInwardDate,
            CaseCalculator.ResubmissionCount(records.Count),
            sla,
            CanEditDetails: user.Has(Permissions.CasesEdit) && WorkflowStatuses.IsOpen(latest),
            CanOverride: user.Has(Permissions.WorkflowOverride));
    }

    public async Task<ServiceResult<string>> ExecuteActionAsync(
        string referenceId, string actionCode, WorkflowInput input, UserAccessProfile user,
        IReadOnlyList<UploadedFile>? files = null)
    {
        var def = WorkflowEngine.Find(actionCode);
        if (def == null) return ServiceResult<string>.Fail($"Unknown action '{actionCode}'.", 404);

        files ??= Array.Empty<UploadedFile>();
        input.FileCount = files.Count;
        if (files.Count > 0 && def.Fields.All(f => f.Type != "files"))
            return ServiceResult<string>.Fail("This action does not take documents.");

        if (!user.Has(def.Permission))
            return ServiceResult<string>.Fail($"You do not have the '{def.Permission}' permission.", 403);
        if (!user.InWorkspace(def.Team))
            return ServiceResult<string>.Fail($"Only members of a {def.Team} team can perform this action.", 403);

        if (!await _access.CanViewCaseAsync(referenceId, user))
            return ServiceResult<string>.Fail("Case not found.", 404);

        return await ExecuteCoreAsync(referenceId, def, input,
            new WorkflowActor(user.UserId, user.DisplayName, def.Team),
            input.AiAssisted ? "Text drafted with AI assistance and confirmed by the user." : null,
            files);
    }

    /// <summary>Name shown for actions the AI automation performs.</summary>
    public const string AiActorName = "AccountFlow AI";

    /// <summary>
    /// Runs a workflow action on behalf of the AI automation. No user permission
    /// check (the automation itself is enabled by Admin), but every workflow rule,
    /// precondition and validation applies exactly as for a person.
    /// </summary>
    public async Task<ServiceResult<string>> ExecuteSystemActionAsync(
        string referenceId, string actionCode, WorkflowInput input, string note)
    {
        var def = WorkflowEngine.Find(actionCode);
        if (def == null) return ServiceResult<string>.Fail($"Unknown action '{actionCode}'.", 404);
        input.AiAssisted = true;
        input.FileCount = 0;
        return await ExecuteCoreAsync(referenceId, def, input, new WorkflowActor(0, AiActorName, def.Team), note, null);
    }

    private async Task<ServiceResult<string>> ExecuteCoreAsync(
        string referenceId, ActionDefinition def, WorkflowInput input, WorkflowActor actor, string? note,
        IReadOnlyList<UploadedFile>? files)
    {
        var records = await _db.Accounts.Where(a => a.ReferenceId == referenceId)
            .OrderBy(a => a.InwardDate).ThenBy(a => a.Id).ToListAsync();
        if (records.Count == 0) return ServiceResult<string>.Fail("Case not found.", 404);

        var queries = await LoadQueriesAsync(referenceId, tracking: true);
        var formNotReady = await FormNotReadyAsync(referenceId, CaseCalculator.Latest(records));
        var ctx = new WorkflowContext { Records = records, Queries = queries, Today = Today, FormNotReadyReason = formNotReady };
        var latest = ctx.Latest;

        var result = WorkflowEngine.Apply(ctx, def.Code, input, actor);
        if (!result.Success) return ServiceResult<string>.Fail(result.Error!, 409);

        if (result.NewRecord != null)
        {
            _db.Accounts.Add(result.NewRecord);
            records.Add(result.NewRecord);
        }
        if (result.NewQuery != null) _db.CaseQueries.Add(result.NewQuery);

        // Documents are saved in the same transaction as the workflow change.
        foreach (var f in files ?? Array.Empty<UploadedFile>())
        {
            _db.CaseDocuments.Add(new CaseDocument
            {
                ReferenceId = referenceId,
                QueryId = def.UsesQuery ? input.QueryId : null,
                Purpose = def.Label,
                FileName = f.FileName,
                ContentType = f.ContentType,
                SizeBytes = f.Data.LongLength,
                Data = f.Data,
                UploadedByUserId = actor.UserId == 0 ? null : actor.UserId,
                UploadedByName = actor.Name,
                Team = def.Team
            });
        }

        // Once sent for signature the application form is locked.
        if (def.Code == ActionCodes.SendForm)
        {
            var form = await _db.ApplicationForms.FirstOrDefaultAsync(f => f.ReferenceId == referenceId);
            if (form != null)
            {
                form.Status = ApplicationFormStatuses.Sent;
                form.SentAt = DateTime.UtcNow;
            }
        }

        int sla = await _settings.GetSlaThresholdAsync();
        CaseCalculator.Recalculate(records, Today, sla);

        await _db.SaveChangesAsync();

        var target = result.NewRecord ?? latest;
        _db.AuditEvents.Add(new AuditEvent
        {
            AccountId = target.Id,
            ReferenceId = referenceId,
            UserId = actor.UserId == 0 ? null : actor.UserId,
            UserName = actor.Name,
            Team = def.Team,
            Action = result.AuditAction,
            FromStatus = result.FromStatus,
            ToStatus = result.ToStatus,
            Details = Truncate(Combine(
                result.NewQuery != null ? $"Query #{result.NewQuery.Id} raised to {result.NewQuery.ToTeam}." : null,
                result.UpdatedQuery != null ? $"Query #{result.UpdatedQuery.Id} is now {result.UpdatedQuery.Status}." : null,
                result.AuditDetails,
                note), 1000)
        });
        await _db.SaveChangesAsync();

        return ServiceResult<string>.Ok(result.ToStatus);
    }

    // ------------------------------------------------------------------
    // Creation and edits
    // ------------------------------------------------------------------

    private static readonly string[] EntryTypes = { "New", "Resubmission", "Discrepancy Resolution", "Modification" };

    public async Task<ServiceResult<Account>> CreateAsync(CreateAccountRequest request, UserAccessProfile user)
    {
        bool overrideAllowed = user.Has(Permissions.WorkflowOverride);
        if (!user.InWorkspace(WorkspaceTypes.Operations) && !overrideAllowed)
            return ServiceResult<Account>.Fail("Only Operations team members can log inward requests.", 403);

        if (string.IsNullOrWhiteSpace(request.ReferenceId) ||
            string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.ClientName) ||
            string.IsNullOrWhiteSpace(request.PanNo) ||
            !request.InwardDate.HasValue || request.InwardDate.Value == default)
        {
            return ServiceResult<Account>.Fail(
                "Reference ID, Request ID, Client Name, PAN Number, and Inward Date are required.");
        }

        if (!EntryTypes.Contains(request.EntryType))
            return ServiceResult<Account>.Fail("Invalid Entry Type.");

        var pan = request.PanNo.Trim().ToUpperInvariant();
        if (!SensitiveData.PanPattern.IsMatch(pan) || pan.Length != 10)
            return ServiceResult<Account>.Fail("PAN must be 10 characters in the format AAAAA9999A.");

        if (request.InwardDate.Value.Date > Today)
            return ServiceResult<Account>.Fail("Inward Date cannot be in the future.");

        if (!WorkflowStatuses.All.Contains(request.LatestStatus))
            return ServiceResult<Account>.Fail("Invalid Latest Status.");

        var referenceId = request.ReferenceId.Trim();
        var existing = await _db.Accounts.Where(a => a.ReferenceId == referenceId).ToListAsync();

        if (request.EntryType == "New" && existing.Count > 0)
            return ServiceResult<Account>.Fail($"Reference ID {referenceId} already exists. Log a Resubmission, Discrepancy Resolution or Modification entry instead.", 409);
        if (request.EntryType != "New" && existing.Count == 0)
            return ServiceResult<Account>.Fail($"Reference ID {referenceId} does not exist. A {request.EntryType} entry must share the Reference ID of an existing case.");

        if (existing.Count > 0 && !await _access.CanViewCaseAsync(referenceId, user))
            return ServiceResult<Account>.Fail("Case not found.", 404);

        if (!overrideAllowed && HasWorkflowData(request))
            return ServiceResult<Account>.Fail(
                "New entries start at 'Request Received from CSE'. Use the workflow actions to progress the case " +
                "(stage dates and statuses can only be set directly with the workflow.override permission).", 403);

        var validationError = WorkflowValidationHelper.Validate(
            request.AccountType, request.Channel, request.ActionBy, request.DiscrepancyType,
            request.Stage4ReviewOutcome, request.InwardDate, request.AccountOpeningDate,
            request.DiscrepancyRaisedDate, request.DiscrepancyResolutionReceivedDate,
            request.SubmittedToMofslDate, request.MofslQueryRaisedDate, request.MofslQueryResolvedDate);
        if (validationError != null) return ServiceResult<Account>.Fail(validationError);

        CaseRequest? sourceRequest = null;
        if (request.SourceRequestId.HasValue)
        {
            sourceRequest = await _db.CaseRequests.FirstOrDefaultAsync(r => r.Id == request.SourceRequestId.Value);
            if (sourceRequest == null)
                return ServiceResult<Account>.Fail("The CSE request being logged was not found.", 404);
            if (sourceRequest.Status != CaseRequestStatuses.Submitted)
                return ServiceResult<Account>.Fail($"This CSE request was already logged as {sourceRequest.ReferenceId}.", 409);
        }

        var now = DateTime.UtcNow;
        var account = new Account
        {
            ReferenceId = referenceId,
            RequestId = request.RequestId.Trim(),
            EntryType = request.EntryType,
            ClientName = (request.ClientName ?? "").Trim(),
            PanNo = pan,
            AccountType = request.AccountType,
            Channel = request.Channel,
            ActionBy = overrideAllowed ? request.ActionBy : WorkspaceTypes.Operations,
            Location = (request.Location ?? "").Trim(),
            Segment = (request.Segment ?? "").Trim(),
            Cse = (request.Cse ?? "").Trim(),
            RelationshipManager = (request.RelationshipManager ?? "").Trim(),
            Processor = string.IsNullOrWhiteSpace(request.Processor) ? user.DisplayName : request.Processor.Trim(),
            InwardDate = request.InwardDate.Value.Date,
            OutwardDate = request.OutwardDate,
            ResubmissionDate = request.ResubmissionDate,
            Stage1QueryRaisedDate = request.Stage1QueryRaisedDate,
            Stage1QueryDetails = request.Stage1QueryDetails,
            Stage1ResubmissionDate = request.Stage1ResubmissionDate,
            Stage1Status = request.Stage1Status,
            FormPreparedDate = request.FormPreparedDate,
            PhysicalFormSubmittedToCseDate = request.PhysicalFormSubmittedToCseDate,
            DigitalFormSentToClientDate = request.DigitalFormSentToClientDate,
            Stage2Status = request.Stage2Status,
            SignedFormReceivedDate = request.SignedFormReceivedDate,
            DiscrepancyRaisedDate = request.DiscrepancyRaisedDate,
            DiscrepancyType = request.DiscrepancyType,
            FormReturnedToCseDate = request.FormReturnedToCseDate,
            Stage3Status = request.Stage3Status,
            DiscrepancyResolutionReceivedDate = request.DiscrepancyResolutionReceivedDate,
            ResubmittedFormReceivedDate = request.ResubmittedFormReceivedDate,
            Stage4ReviewOutcome = request.Stage4ReviewOutcome,
            Stage4Status = request.Stage4Status,
            SubmittedToMofslDate = request.SubmittedToMofslDate,
            MofslQueryRaisedDate = request.MofslQueryRaisedDate,
            MofslQueryType = request.MofslQueryType,
            MofslQueryResolvedDate = request.MofslQueryResolvedDate,
            Stage5Status = request.Stage5Status,
            AccountNumber = request.AccountNumber,
            AccountOpeningDate = request.AccountOpeningDate,
            Stage6Status = request.Stage6Status,
            LatestStatus = request.LatestStatus,
            QueryDetails = request.QueryDetails,
            QueryCount = request.QueryCount,
            QueryHoldDays = request.QueryHoldDays,
            TouchCount = request.TouchCount,
            Remarks = request.Remarks,
            CreatedAt = now,
            UpdatedAt = now
        };

        var related = existing.Append(account).ToList();
        CaseCalculator.Recalculate(related, Today, await _settings.GetSlaThresholdAsync());

        _db.Accounts.Add(account);
        if (sourceRequest != null)
        {
            sourceRequest.Status = CaseRequestStatuses.Logged;
            sourceRequest.ReferenceId = account.ReferenceId;
            sourceRequest.LoggedByUserId = user.UserId;
            sourceRequest.LoggedByName = user.DisplayName;
            sourceRequest.LoggedAt = now;
        }
        await _db.SaveChangesAsync();

        _db.AuditEvents.Add(new AuditEvent
        {
            AccountId = account.Id,
            ReferenceId = account.ReferenceId,
            UserId = user.UserId,
            UserName = user.DisplayName,
            Team = WorkspaceTypes.Operations,
            Action = request.EntryType == "New" ? "Logged inward request" : $"Logged {request.EntryType} record",
            ToStatus = account.LatestStatus,
            Details = string.Join(" ", new[]
            {
                sourceRequest != null ? $"Logged from CSE request #{sourceRequest.Id}; its details and attachments are linked to this case." : null,
                overrideAllowed && HasWorkflowData(request) ? "Created with workflow data (override)." : null
            }.Where(x => x != null))
        });
        await _db.SaveChangesAsync();

        return ServiceResult<Account>.Ok(account);
    }

    private static bool HasWorkflowData(CreateAccountRequest r) =>
        r.LatestStatus != WorkflowStatuses.RequestReceived ||
        r.Stage1QueryRaisedDate.HasValue || r.Stage1ResubmissionDate.HasValue ||
        r.FormPreparedDate.HasValue || r.PhysicalFormSubmittedToCseDate.HasValue ||
        r.DigitalFormSentToClientDate.HasValue || r.SignedFormReceivedDate.HasValue ||
        r.DiscrepancyRaisedDate.HasValue || r.FormReturnedToCseDate.HasValue ||
        r.DiscrepancyResolutionReceivedDate.HasValue || r.ResubmittedFormReceivedDate.HasValue ||
        r.SubmittedToMofslDate.HasValue || r.MofslQueryRaisedDate.HasValue ||
        r.MofslQueryResolvedDate.HasValue || r.AccountOpeningDate.HasValue ||
        !string.IsNullOrWhiteSpace(r.AccountNumber) ||
        r.Stage2Status != "Pending" || r.Stage3Status != "Pending" || r.Stage4Status != "Pending" ||
        r.Stage5Status != "Pending" || r.Stage6Status != "Pending" ||
        r.Stage1Status != WorkflowStatuses.RequestReceived ||
        r.QueryCount != 0;

    public async Task<ServiceResult<Account>> UpdateDetailsAsync(
        string referenceId, UpdateCaseDetailsRequest request, UserAccessProfile user)
    {
        var records = await LoadCaseRecordsAsync(referenceId, user, tracking: true);
        if (records == null) return ServiceResult<Account>.Fail("Case not found.", 404);

        var latest = CaseCalculator.Latest(records);
        if (!WorkflowStatuses.IsOpen(latest) && !user.Has(Permissions.WorkflowOverride))
            return ServiceResult<Account>.Fail("Closed cases can only be corrected with the workflow.override permission.", 409);

        var pan = request.PanNo.Trim().ToUpperInvariant();
        if (!SensitiveData.PanPattern.IsMatch(pan) || pan.Length != 10)
            return ServiceResult<Account>.Fail("PAN must be 10 characters in the format AAAAA9999A.");

        bool channelLocked = latest.FormPreparedDate.HasValue ||
                             latest.PhysicalFormSubmittedToCseDate.HasValue ||
                             latest.DigitalFormSentToClientDate.HasValue;
        if (channelLocked && !string.Equals(request.Channel, latest.Channel, StringComparison.OrdinalIgnoreCase))
            return ServiceResult<Account>.Fail("The channel cannot change after the form has been sent.", 409);

        var validation = WorkflowValidationHelper.Validate(
            request.AccountType, request.Channel, latest.ActionBy, latest.DiscrepancyType,
            latest.Stage4ReviewOutcome, latest.InwardDate, latest.AccountOpeningDate,
            latest.DiscrepancyRaisedDate, latest.DiscrepancyResolutionReceivedDate,
            latest.SubmittedToMofslDate, latest.MofslQueryRaisedDate, latest.MofslQueryResolvedDate);
        if (validation != null) return ServiceResult<Account>.Fail(validation);

        string? stageOverride = string.IsNullOrWhiteSpace(request.CurrentStageOverride) ? null : request.CurrentStageOverride.Trim();
        if (stageOverride != null && !MisCalculator.StageNames.Contains(stageOverride))
            return ServiceResult<Account>.Fail($"Current stage must be one of: {string.Join(", ", MisCalculator.StageNames)}.");

        // Work out what changed (field names only — values are not written to the audit log).
        var changed = new List<string>();
        void Track(string field, string? oldValue, string? newValue)
        {
            if (!string.Equals((oldValue ?? "").Trim(), (newValue ?? "").Trim(), StringComparison.Ordinal))
                changed.Add(field);
        }
        Track("Client name", latest.ClientName, request.ClientName);
        Track("PAN", latest.PanNo, pan);
        Track("Account type", latest.AccountType, request.AccountType);
        Track("Channel", latest.Channel, request.Channel);
        Track("Location", latest.Location, request.Location);
        Track("Segment", latest.Segment, request.Segment);
        Track("CSE", latest.Cse, request.Cse);
        Track("RM", latest.RelationshipManager, request.RelationshipManager);
        Track("Processor", latest.Processor, request.Processor);
        Track("Remarks", latest.Remarks, request.Remarks);
        Track("Query hold days", latest.QueryHoldDays.ToString(), request.QueryHoldDays.ToString());
        Track("Outward date", latest.OutwardDate?.ToString("yyyy-MM-dd"), request.OutwardDate?.ToString("yyyy-MM-dd"));
        Track("Current stage (manual override)",
            latest.IsCurrentStageManuallyOverridden ? latest.CurrentStage : "Automatic",
            stageOverride ?? "Automatic");

        // Identity fields apply to every record of the case so the case stays consistent.
        foreach (var r in records)
        {
            r.ClientName = (request.ClientName ?? "").Trim();
            r.PanNo = pan;
            r.AccountType = request.AccountType;
            r.Location = (request.Location ?? "").Trim();
            r.Segment = (request.Segment ?? "").Trim();
            r.Cse = (request.Cse ?? "").Trim();
            r.RelationshipManager = (request.RelationshipManager ?? "").Trim();
        }

        // Record-level fields apply to the latest record.
        latest.Channel = request.Channel;
        latest.Processor = (request.Processor ?? "").Trim();
        latest.Remarks = request.Remarks;
        latest.QueryHoldDays = request.QueryHoldDays;
        latest.OutwardDate = request.OutwardDate?.Date;
        latest.IsCurrentStageManuallyOverridden = stageOverride != null;
        if (stageOverride != null) latest.CurrentStage = stageOverride;
        latest.UpdatedAt = DateTime.UtcNow;

        CaseCalculator.Recalculate(records, Today, await _settings.GetSlaThresholdAsync());
        await _db.SaveChangesAsync();

        _db.AuditEvents.Add(new AuditEvent
        {
            AccountId = latest.Id,
            ReferenceId = referenceId,
            UserId = user.UserId,
            UserName = user.DisplayName,
            Team = WorkspaceTypes.Operations,
            Action = "Edited case details",
            Details = "General case information updated" + (changed.Count > 0 ? $" ({string.Join(", ", changed.Distinct())})." : ".")
        });
        await _db.SaveChangesAsync();

        return ServiceResult<Account>.Ok(latest);
    }

    public async Task AuditAsync(Account account, UserAccessProfile user, string action, string? from, string? to, string? details)
    {
        _db.AuditEvents.Add(new AuditEvent
        {
            AccountId = account.Id,
            ReferenceId = account.ReferenceId,
            UserId = user.UserId,
            UserName = user.DisplayName,
            Action = action,
            FromStatus = from,
            ToStatus = to,
            Details = Truncate(details, 1000)
        });
        await _db.SaveChangesAsync();
    }

    private static string? Combine(params string?[] parts)
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return list.Count == 0 ? null : string.Join(" ", list);
    }

    private static string? Truncate(string? value, int max) =>
        value == null || value.Length <= max ? value : value[..max];
}
