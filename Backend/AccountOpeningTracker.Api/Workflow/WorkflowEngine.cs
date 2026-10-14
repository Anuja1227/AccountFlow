using System.Text.RegularExpressions;
using AccountOpeningTracker.Api.Models;
using S = AccountOpeningTracker.Api.Models.WorkflowStatuses;

namespace AccountOpeningTracker.Api.Workflow;

public static class ActionCodes
{
    // Operations — Stage 1
    public const string StartReview = "start-review";
    public const string RaiseStage1Query = "raise-stage1-query";
    public const string StartFormPreparation = "start-form-preparation";
    // Operations — Stage 2
    public const string SendForm = "send-form";
    // Operations — Stage 3
    public const string RecordSignedForm = "record-signed-form";
    public const string StartSignedFormReview = "start-signed-form-review";
    public const string RaiseDiscrepancy = "raise-discrepancy";
    public const string ReturnForm = "return-form";
    public const string MarkInOrder = "mark-in-order";
    // Operations — reviewing CSE responses (Stage 1 / Stage 4)
    public const string OpsAcceptResponse = "ops-accept-response";
    public const string OpsFollowUp = "ops-follow-up";
    // Operations — Stage 5 / 6
    public const string SubmitToMofsl = "submit-to-mofsl";
    public const string OpsRespondMofsl = "ops-respond-mofsl";
    public const string SendCommunication = "send-communication";
    // Operations — exceptions
    public const string PutOnHold = "put-on-hold";
    public const string Resume = "resume";
    public const string Reject = "reject";
    public const string Cancel = "cancel";
    // CSE
    public const string CseRespond = "cse-respond";
    public const string CseSendSignedForm = "cse-send-signed-form";
    // MOFSL
    public const string MofslSendAccountDetails = "mofsl-send-account-details";
    public const string MofslRaiseQuery = "mofsl-raise-query";
    public const string MofslAcceptResponse = "mofsl-accept-response";
    public const string MofslFollowUp = "mofsl-follow-up";
    public const string RecordAccountOpened = "record-account-opened";
}

public sealed record ActionField(
    string Name,
    string Label,
    string Type,
    bool Required,
    string[]? Options = null,
    string? Help = null);

public sealed record ActionDefinition(
    string Code,
    string Label,
    string Team,
    string Permission,
    string Description,
    bool UsesQuery,
    string? AiDraftQueryType,
    string Tone,
    ActionField[] Fields);

public sealed record AvailableAction(ActionDefinition Definition, int? QueryId, string? QueryLabel);

public sealed class WorkflowInput
{
    public int? QueryId { get; set; }
    public string? Details { get; set; }
    public string? DiscrepancyType { get; set; }
    public string? MofslQueryType { get; set; }
    public string? AccountNumber { get; set; }
    public DateTime? ActionDate { get; set; }
    public string? Reason { get; set; }

    /// <summary>True when the user started from an AI draft (recorded on the message).</summary>
    public bool AiAssisted { get; set; }

    /// <summary>Number of documents uploaded with the action. Set by the server from the actual files.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int FileCount { get; set; }
}

public sealed record WorkflowActor(int UserId, string Name, string Team);

public sealed class WorkflowContext
{
    public required List<Account> Records { get; init; }
    public required List<CaseQuery> Queries { get; init; }
    public required DateTime Today { get; init; }

    /// <summary>
    /// Why the Stage 2 application form cannot be sent for signature yet (null = ready).
    /// Filled by the caller from the saved form; the engine itself has no database access.
    /// </summary>
    public string? FormNotReadyReason { get; init; }

    public Account Latest => CaseCalculator.Latest(Records);
}

public sealed class WorkflowResult
{
    public bool Success => Error == null;
    public string? Error { get; init; }
    public string FromStatus { get; init; } = string.Empty;
    public string ToStatus { get; init; } = string.Empty;
    public Account? NewRecord { get; init; }
    public CaseQuery? NewQuery { get; init; }
    public CaseQuery? UpdatedQuery { get; init; }
    public string AuditAction { get; init; } = string.Empty;
    public string? AuditDetails { get; init; }

    public static WorkflowResult Fail(string error) => new() { Error = error };
}

/// <summary>
/// Deterministic six-stage workflow. Defines which actions exist, which
/// team and permission each needs, which statuses each is valid from, and
/// exactly what each action changes. No database access: it mutates the
/// passed-in entities and returns any new entities to persist.
/// </summary>
public static class WorkflowEngine
{
    public const int MaxTextLength = 2000;
    public const int MaxReasonLength = 500;

    public static readonly string[] DiscrepancyTypes =
        { "Document Missing", "Signature Mismatch", "Incomplete Details", "Other" };

    private static readonly Regex AccountNumberPattern = new("^[A-Za-z0-9/-]{4,30}$", RegexOptions.Compiled);

    private static readonly ActionField DateField =
        new("actionDate", "Action date", "date", false, Help: "Defaults to today. Cannot be in the future.");

    private static ActionField Text(string name, string label, string? help = null) =>
        new(name, label, "textarea", true, Help: help);

    private static ActionField Files(string label, bool required, string? help = null) =>
        new("files", label, "files", required, Help: help ?? "Up to 5 files, 4 MB each: JPG, PNG, WEBP or PDF.");

    public static readonly ActionDefinition[] Definitions =
    {
        // ---------- Operations ----------
        new(ActionCodes.StartReview, "Start review", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Begin the completeness review of the inward request.", false, null, "primary",
            Array.Empty<ActionField>()),
        new(ActionCodes.RaiseStage1Query, "Raise query to CSE", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Information is missing: send a Stage 1 query to the CSE.", false, QueryTypes.MissingInformation, "warning",
            new[] { Text("details", "What is missing?"), DateField }),
        new(ActionCodes.StartFormPreparation, "Information complete — prepare form", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "All required information is present. Move to application-form preparation.", false, null, "primary",
            Array.Empty<ActionField>()),
        new(ActionCodes.SendForm, "Send form for signature", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "The application form has been checked. Physical channel: submit it to the CSE, who gets it signed by the client. Digital channel: send it to the client.", false, null, "primary",
            new[] { DateField }),
        new(ActionCodes.RecordSignedForm, "Record signed form received", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Use when the signed form reached Operations outside the app (normally the CSE sends it here).", false, null, "neutral",
            new[] { Files("Signed form and documents (optional)", false), DateField }),
        new(ActionCodes.StartSignedFormReview, "Start signed-form review", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Begin reviewing the signed form and documents.", false, null, "primary",
            Array.Empty<ActionField>()),
        new(ActionCodes.RaiseDiscrepancy, "Raise discrepancy to CSE", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Flag a discrepancy on the signed form or documents for rectification.", false, QueryTypes.Discrepancy, "warning",
            new[]
            {
                new ActionField("discrepancyType", "Discrepancy type", "select", true, DiscrepancyTypes),
                Text("details", "Discrepancy details"), DateField
            }),
        new(ActionCodes.ReturnForm, "Return form to CSE", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Physically return the form to the CSE for correction and resubmission.", false, QueryTypes.FormReturned, "warning",
            new[]
            {
                new ActionField("discrepancyType", "Reason type", "select", true, DiscrepancyTypes),
                Text("details", "What must be corrected?"), DateField
            }),
        new(ActionCodes.MarkInOrder, "Form found in order", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "The signed form and documents are complete. Ready for MOFSL submission.", false, null, "success",
            Array.Empty<ActionField>()),
        new(ActionCodes.OpsAcceptResponse, "Accept CSE response", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "The CSE's response resolves the query. Stage 1: return to review. Stage 4: form found in order.", true, null, "success",
            new[] { new ActionField("details", "Review note (optional)", "textarea", false) }),
        new(ActionCodes.OpsFollowUp, "Still pending — follow up with CSE", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "The response does not resolve the query. Reopen it with a follow-up.", true, null, "warning",
            new[] { Text("details", "Follow-up message"), DateField }),
        new(ActionCodes.SubmitToMofsl, "Submit to MOFSL", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Submit the completed case to MOFSL for account opening.", false, null, "primary",
            new[] { DateField }),
        new(ActionCodes.OpsRespondMofsl, "Respond to MOFSL query", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Resolve the MOFSL query and resubmit to MOFSL.", true, null, "primary",
            new[]
            {
                Text("details", "Resolution details"),
                new ActionField("actionDate", "MOFSL Query Resolved Date", "date", false, Help: "Defaults to today.")
            }),
        new(ActionCodes.SendCommunication, "Send communication & close case", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Record that the account-opening communication was sent. Closes the case.", false, null, "success",
            new[] { DateField }),
        new(ActionCodes.PutOnHold, "Put on hold", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Pause the case. It resumes at its current status.", false, null, "neutral",
            new[] { new ActionField("reason", "Reason", "textarea", true) }),
        new(ActionCodes.Resume, "Resume case", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Resume the case at the status it had before it was put on hold.", false, null, "primary",
            Array.Empty<ActionField>()),
        new(ActionCodes.Reject, "Reject case", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Reject the request. This closes the case.", false, null, "danger",
            new[] { new ActionField("reason", "Reason", "textarea", true) }),
        new(ActionCodes.Cancel, "Cancelled by client", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Record that the client cancelled. This closes the case.", false, null, "danger",
            new[] { new ActionField("reason", "Reason", "textarea", true) }),

        // ---------- CSE ----------
        new(ActionCodes.CseRespond, "Respond to query", WorkspaceTypes.Cse, Permissions.WorkflowCse,
            "Provide the missing information or the discrepancy resolution. Stage 3 responses create a resubmission record.", true, null, "primary",
            new[] { Text("details", "Your response"), Files("Corrected form / documents (optional)", false), DateField }),
        new(ActionCodes.CseSendSignedForm, "Send signed form to Operations", WorkspaceTypes.Cse, Permissions.WorkflowCse,
            "Upload the application form signed by the client, with the supporting documents, for Operations to verify.", false, null, "primary",
            new[]
            {
                Files("Signed form and supporting documents", true),
                new ActionField("details", "Note to Operations (optional)", "textarea", false),
                new ActionField("actionDate", "Date received from client", "date", false, Help: "Defaults to today.")
            }),

        // ---------- MOFSL ----------
        new(ActionCodes.MofslSendAccountDetails, "✓ Account opened — send details to Operations", WorkspaceTypes.Mofsl, Permissions.WorkflowMofsl,
            "The account is opened. Send the account number and opening date; Operations logs them in the tracker.", false, null, "success",
            new[]
            {
                new ActionField("accountNumber", "Account Number", "text", true),
                new ActionField("actionDate", "Account Opening Date", "date", false, Help: "Defaults to today."),
                new ActionField("details", "Note to Operations (optional)", "textarea", false)
            }),
        new(ActionCodes.MofslRaiseQuery, "Raise MOFSL query", WorkspaceTypes.Mofsl, Permissions.WorkflowMofsl,
            "Raise a query to Operations about this submission.", false, QueryTypes.MofslQuery, "warning",
            new[]
            {
                new ActionField("mofslQueryType", "MOFSL Query Type", "text", true, Help: "Short label, e.g. KYC mismatch"),
                Text("details", "Query details"),
                new ActionField("actionDate", "MOFSL Query Raised Date", "date", false, Help: "Defaults to today.")
            }),
        new(ActionCodes.MofslAcceptResponse, "Accept Operations response", WorkspaceTypes.Mofsl, Permissions.WorkflowMofsl,
            "The Operations response resolves the MOFSL query. Send the account details once the account is opened.", true, null, "success",
            new[] { new ActionField("details", "Note (optional)", "textarea", false) }),
        new(ActionCodes.MofslFollowUp, "Still pending — follow up", WorkspaceTypes.Mofsl, Permissions.WorkflowMofsl,
            "The response does not resolve the MOFSL query. Reopen it.", true, null, "warning",
            new[] { Text("details", "Follow-up message"), DateField }),
        new(ActionCodes.RecordAccountOpened, "Log account details in tracker", WorkspaceTypes.Operations, Permissions.WorkflowOperations,
            "Log the account number and opening date MOFSL sent. Marks the case as Account Opened.", false, null, "success",
            new[]
            {
                new ActionField("accountNumber", "Account Number", "text", true),
                new ActionField("actionDate", "Account Opening Date", "date", false, Help: "Defaults to today.")
            }),
    };

    public static ActionDefinition? Find(string code) =>
        Definitions.FirstOrDefault(d => d.Code == code);

    // ------------------------------------------------------------------
    // Availability
    // ------------------------------------------------------------------

    /// <summary>
    /// All actions structurally possible right now, for any team. Callers
    /// filter by the user's permissions and team membership.
    /// </summary>
    public static List<AvailableAction> GetAvailableActions(WorkflowContext ctx)
    {
        var result = new List<AvailableAction>();
        foreach (var def in Definitions)
        {
            if (def.UsesQuery)
            {
                foreach (var q in ctx.Queries.OrderBy(q => q.Id))
                {
                    if (Precondition(ctx, def.Code, q.Id) == null)
                        result.Add(new AvailableAction(def, q.Id, DescribeQuery(q)));
                }
            }
            else if (Precondition(ctx, def.Code, null) == null)
            {
                result.Add(new AvailableAction(def, null, null));
            }
        }
        return result;
    }

    private static string DescribeQuery(CaseQuery q) =>
        $"#{q.Id} {QueryTypes.Label(q.QueryType)}{(q.Category != null ? $" ({q.Category})" : "")} — {q.Status}";

    private static bool Unresolved(CaseQuery q) => q.Status != QueryStatuses.Resolved;

    private static bool IsCseQuery(CaseQuery q) => q.ToTeam == WorkspaceTypes.Cse;

    /// <summary>Returns null if the action can run, otherwise the reason it cannot.</summary>
    public static string? Precondition(WorkflowContext ctx, string code, int? queryId)
    {
        var def = Find(code);
        if (def == null) return $"Unknown action '{code}'.";
        if (ctx.Records.Count == 0) return "Case has no records.";

        var latest = ctx.Latest;
        var status = latest.LatestStatus;

        if (S.IsTerminal(status)) return $"The case is closed ({status}).";

        if (status == S.OnHold && code != ActionCodes.Resume &&
            code != ActionCodes.Reject && code != ActionCodes.Cancel)
            return "The case is on hold. Resume it first.";

        CaseQuery? query = null;
        if (def.UsesQuery)
        {
            if (queryId == null) return "Select the query this action applies to.";
            query = ctx.Queries.FirstOrDefault(q => q.Id == queryId);
            if (query == null) return "Query not found on this case.";
        }

        bool StatusIn(params string[] statuses) => statuses.Contains(status);

        switch (code)
        {
            case ActionCodes.StartReview:
                return StatusIn(S.RequestReceived) ? null : NotFrom(status);

            case ActionCodes.RaiseStage1Query:
                return StatusIn(S.RequestReceived, S.UnderReviewByOperations) ? null : NotFrom(status);

            case ActionCodes.StartFormPreparation:
                if (!StatusIn(S.UnderReviewByOperations)) return NotFrom(status);
                return ctx.Queries.Any(q => q.QueryType == QueryTypes.MissingInformation && Unresolved(q))
                    ? "Resolve the open Stage 1 query first." : null;

            case ActionCodes.SendForm:
                if (!StatusIn(S.FormUnderPreparation)) return NotFrom(status);
                return ctx.FormNotReadyReason;

            case ActionCodes.RecordSignedForm:
            case ActionCodes.CseSendSignedForm:
                return StatusIn(S.PhysicalFormSubmitted, S.DigitalFormSent) ? null : NotFrom(status);

            case ActionCodes.StartSignedFormReview:
                return StatusIn(S.SignedFormReceived) ? null : NotFrom(status);

            case ActionCodes.RaiseDiscrepancy:
            case ActionCodes.ReturnForm:
                return StatusIn(S.SignedFormReceived, S.UnderReviewSignedForm) ? null : NotFrom(status);

            case ActionCodes.MarkInOrder:
                if (!StatusIn(S.SignedFormReceived, S.UnderReviewSignedForm)) return NotFrom(status);
                return ctx.Queries.Any(q => IsCseQuery(q) && Unresolved(q))
                    ? "Resolve the open CSE queries first." : null;

            case ActionCodes.OpsAcceptResponse:
            case ActionCodes.OpsFollowUp:
                if (!IsCseQuery(query!) || query!.Status != QueryStatuses.Responded)
                    return "Only a CSE query that has been responded to can be reviewed.";
                return query.QueryType == QueryTypes.MissingInformation
                    ? (StatusIn(S.ResubmittedByCse) ? null : NotFrom(status))
                    : (StatusIn(S.DiscrepancyResolutionReceived, S.ResubmittedFormUnderReview) ? null : NotFrom(status));

            case ActionCodes.SubmitToMofsl:
                if (!StatusIn(S.ReadyForMofsl)) return NotFrom(status);
                return ctx.Queries.Any(q => IsCseQuery(q) && Unresolved(q))
                    ? "Resolve the open CSE queries first." : null;

            case ActionCodes.OpsRespondMofsl:
                if (query!.QueryType != QueryTypes.MofslQuery || query.Status != QueryStatuses.Open)
                    return "Only an open MOFSL query can be responded to.";
                return StatusIn(S.QueryRaisedByMofsl) ? null : NotFrom(status);

            case ActionCodes.SendCommunication:
                return StatusIn(S.AccountOpened) ? null : NotFrom(status);

            case ActionCodes.PutOnHold:
                return StatusIn(S.OnHold, S.AccountOpened) ? NotFrom(status) : null;

            case ActionCodes.Resume:
                return StatusIn(S.OnHold) ? null : NotFrom(status);

            case ActionCodes.Reject:
            case ActionCodes.Cancel:
                return StatusIn(S.AccountOpened) ? NotFrom(status) : null;

            case ActionCodes.CseRespond:
                if (!IsCseQuery(query!) || query!.Status != QueryStatuses.Open)
                    return "Only an open query addressed to CSE can be responded to.";
                return query.QueryType switch
                {
                    QueryTypes.MissingInformation => StatusIn(S.QueryRaisedMissingInfo) ? null : NotFrom(status),
                    QueryTypes.Discrepancy => StatusIn(S.DiscrepancyRaised) ? null : NotFrom(status),
                    QueryTypes.FormReturned => StatusIn(S.FormReturned) ? null : NotFrom(status),
                    _ => "Unsupported query type."
                };

            case ActionCodes.MofslSendAccountDetails:
            case ActionCodes.MofslRaiseQuery:
                if (!StatusIn(S.SubmittedToMofsl, S.MofslQueryResolved)) return NotFrom(status);
                // Once MOFSL has accepted the resolution of its query there are no further
                // queries: only "Account opened" remains. (Still-open points use "Still pending".)
                if (code == ActionCodes.MofslRaiseQuery && status == S.MofslQueryResolved)
                    return "MOFSL accepted the query resolution; use \"Still pending — follow up\" before accepting if something is still open.";
                if (latest.ActionBy != WorkspaceTypes.Mofsl) return "MOFSL has already sent the account details.";
                // Only once every MOFSL query has been tracked through to resolution.
                return ctx.Queries.Any(q => q.QueryType == QueryTypes.MofslQuery && Unresolved(q))
                    ? "Review the Operations response to the open MOFSL query first." : null;

            case ActionCodes.MofslAcceptResponse:
            case ActionCodes.MofslFollowUp:
                if (query!.QueryType != QueryTypes.MofslQuery || query.Status != QueryStatuses.Responded)
                    return "Only a MOFSL query that Operations has responded to can be reviewed.";
                return StatusIn(S.MofslQueryResolved) ? null : NotFrom(status);

            case ActionCodes.RecordAccountOpened:
                if (!StatusIn(S.SubmittedToMofsl, S.MofslQueryResolved)) return NotFrom(status);
                if (ctx.Queries.Any(q => q.QueryType == QueryTypes.MofslQuery && Unresolved(q)))
                    return "The MOFSL query must be resolved first.";
                return string.IsNullOrWhiteSpace(latest.MofslAccountNumber)
                    ? "Waiting for MOFSL to send the account details." : null;
        }

        return $"Action '{code}' is not supported.";
    }

    private static string NotFrom(string status) => $"This action is not available while the case is '{status}'.";

    // ------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------

    public static WorkflowResult Apply(
        WorkflowContext ctx,
        string code,
        WorkflowInput input,
        WorkflowActor actor)
    {
        var def = Find(code);
        if (def == null) return WorkflowResult.Fail($"Unknown action '{code}'.");

        string? pre = Precondition(ctx, code, def.UsesQuery ? input.QueryId : null);
        if (pre != null) return WorkflowResult.Fail(pre);

        string? inputError = ValidateInput(ctx, def, input);
        if (inputError != null) return WorkflowResult.Fail(inputError);

        var latest = ctx.Latest;
        var from = latest.LatestStatus;
        DateTime date = (input.ActionDate ?? ctx.Today).Date;
        DateTime now = DateTime.UtcNow;
        string details = input.Details?.Trim() ?? string.Empty;
        CaseQuery? query = def.UsesQuery ? ctx.Queries.First(q => q.Id == input.QueryId) : null;

        Account? newRecord = null;
        CaseQuery? newQuery = null;
        string audit = def.Label;
        string? auditDetails = null;

        void SetStatus(Account a, string status, string actionBy)
        {
            a.LatestStatus = status;
            a.ActionBy = actionBy;
            a.UpdatedAt = now;
        }

        CaseQuery Raise(string type, string? category, string toTeam)
        {
            var q = new CaseQuery
            {
                AccountId = latest.Id,
                Account = latest,
                ReferenceId = latest.ReferenceId,
                Stage = QueryTypes.StageOf(type),
                QueryType = type,
                Category = category,
                FromTeam = actor.Team,
                ToTeam = toTeam,
                Status = QueryStatuses.Open,
                RaisedByUserId = actor.UserId,
                RaisedByName = actor.Name,
                RaisedAt = date,
                LastOpenedAt = date,
            };
            q.Messages.Add(Message(MessageTypes.Query, details));
            latest.QueryCount += 1;
            latest.QueryDetails = AppendNote(latest.QueryDetails, $"[{QueryTypes.StageOf(type)} {date:dd-MMM-yyyy}] {details}");
            return q;
        }

        CaseQueryMessage Message(string type, string body) => new()
        {
            AuthorUserId = actor.UserId,
            AuthorName = actor.Name,
            AuthorTeam = actor.Team,
            MessageType = type,
            Body = body,
            AiAssisted = input.AiAssisted,
            CreatedAt = now
        };

        int CloseHoldCycle(CaseQuery q)
        {
            int hold = Math.Max(0, WorkingDaysHelper.CountWorkingDays(q.LastOpenedAt, date));
            q.HoldDays += hold;
            q.Status = QueryStatuses.Responded;
            q.RespondedAt = date;
            return hold;
        }

        void Reopen(CaseQuery q)
        {
            q.Status = QueryStatuses.Open;
            q.LastOpenedAt = date;
            q.RespondedAt = null;
            q.FollowUpCount += 1;
            q.Messages.Add(Message(MessageTypes.FollowUp, details));
            latest.QueryCount += 1;
            latest.QueryDetails = AppendNote(latest.QueryDetails, $"[Follow-up {date:dd-MMM-yyyy}] {details}");
        }

        void Resolve(CaseQuery q)
        {
            q.Status = QueryStatuses.Resolved;
            q.ResolvedAt = date;
            q.ResolvedByUserId = actor.UserId;
            q.Messages.Add(Message(MessageTypes.Resolution,
                string.IsNullOrWhiteSpace(details) ? "Response accepted." : details));
        }

        switch (code)
        {
            case ActionCodes.StartReview:
                SetStatus(latest, S.UnderReviewByOperations, WorkspaceTypes.Operations);
                latest.Stage1Status = S.UnderReviewByOperations;
                break;

            case ActionCodes.RaiseStage1Query:
                newQuery = Raise(QueryTypes.MissingInformation, null, WorkspaceTypes.Cse);
                latest.Stage1QueryRaisedDate ??= date;
                latest.Stage1QueryDetails = AppendNote(latest.Stage1QueryDetails, details);
                SetStatus(latest, S.QueryRaisedMissingInfo, WorkspaceTypes.Cse);
                latest.Stage1Status = S.QueryRaisedMissingInfo;
                audit = "Raised Stage 1 query to CSE";
                break;

            case ActionCodes.StartFormPreparation:
                SetStatus(latest, S.FormUnderPreparation, WorkspaceTypes.Operations);
                latest.Stage1Status = "Completed";
                latest.Stage2Status = S.FormUnderPreparation;
                break;

            case ActionCodes.SendForm:
                latest.FormPreparedDate ??= date;
                if (IsDigital(latest))
                {
                    latest.DigitalFormSentToClientDate = date;
                    SetStatus(latest, S.DigitalFormSent, WorkspaceTypes.Cse);
                    latest.Stage2Status = S.DigitalFormSent;
                    auditDetails = "Digital channel: form sent to client for signature.";
                }
                else
                {
                    latest.PhysicalFormSubmittedToCseDate = date;
                    SetStatus(latest, S.PhysicalFormSubmitted, WorkspaceTypes.Cse);
                    latest.Stage2Status = S.PhysicalFormSubmitted;
                    auditDetails = "Physical channel: form submitted to CSE.";
                }
                break;

            case ActionCodes.RecordSignedForm:
            case ActionCodes.CseSendSignedForm:
                latest.SignedFormReceivedDate = date;
                SetStatus(latest, S.SignedFormReceived, WorkspaceTypes.Operations);
                latest.Stage2Status = "Completed";
                latest.Stage3Status = S.SignedFormReceived;
                auditDetails = Join(
                    input.FileCount > 0 ? $"{input.FileCount} document(s) attached." : null,
                    details.Length > 0 ? $"Note: {details}" : null);
                break;

            case ActionCodes.StartSignedFormReview:
                SetStatus(latest, S.UnderReviewSignedForm, WorkspaceTypes.Operations);
                latest.Stage3Status = S.UnderReviewSignedForm;
                break;

            case ActionCodes.RaiseDiscrepancy:
                newQuery = Raise(QueryTypes.Discrepancy, input.DiscrepancyType, WorkspaceTypes.Cse);
                latest.DiscrepancyRaisedDate = date;
                latest.DiscrepancyType = input.DiscrepancyType;
                SetStatus(latest, S.DiscrepancyRaised, WorkspaceTypes.Cse);
                latest.Stage3Status = S.DiscrepancyRaised;
                audit = "Raised Stage 3 discrepancy to CSE";
                auditDetails = $"Type: {input.DiscrepancyType}";
                break;

            case ActionCodes.ReturnForm:
                newQuery = Raise(QueryTypes.FormReturned, input.DiscrepancyType, WorkspaceTypes.Cse);
                latest.FormReturnedToCseDate = date;
                latest.DiscrepancyType = input.DiscrepancyType;
                SetStatus(latest, S.FormReturned, WorkspaceTypes.Cse);
                latest.Stage3Status = S.FormReturned;
                audit = "Returned form to CSE";
                auditDetails = $"Type: {input.DiscrepancyType}";
                break;

            case ActionCodes.MarkInOrder:
                SetStatus(latest, S.ReadyForMofsl, WorkspaceTypes.Operations);
                latest.Stage3Status = "Completed";
                latest.Stage4Status = "Not Required";
                latest.Stage4ReviewOutcome = "Found in Order";
                break;

            case ActionCodes.OpsAcceptResponse:
                Resolve(query!);
                if (query!.QueryType == QueryTypes.MissingInformation)
                {
                    SetStatus(latest, S.UnderReviewByOperations, WorkspaceTypes.Operations);
                    latest.Stage1Status = S.UnderReviewByOperations;
                    audit = "Accepted CSE response to Stage 1 query";
                }
                else
                {
                    SetStatus(latest, S.ReadyForMofsl, WorkspaceTypes.Operations);
                    latest.Stage3Status = "Completed";
                    latest.Stage4ReviewOutcome = "Found in Order";
                    latest.Stage4Status = "Found in Order";
                    audit = "Reviewed resubmission: found in order";
                }
                break;

            case ActionCodes.OpsFollowUp:
                Reopen(query!);
                switch (query!.QueryType)
                {
                    case QueryTypes.MissingInformation:
                        SetStatus(latest, S.QueryRaisedMissingInfo, WorkspaceTypes.Cse);
                        latest.Stage1Status = S.QueryRaisedMissingInfo;
                        break;
                    case QueryTypes.Discrepancy:
                        latest.DiscrepancyRaisedDate = date;
                        latest.Stage4ReviewOutcome = "Still Pending";
                        latest.Stage4Status = "Still Pending";
                        SetStatus(latest, S.DiscrepancyRaised, WorkspaceTypes.Cse);
                        break;
                    case QueryTypes.FormReturned:
                        latest.FormReturnedToCseDate = date;
                        latest.Stage4ReviewOutcome = "Still Pending";
                        latest.Stage4Status = "Still Pending";
                        SetStatus(latest, S.FormReturned, WorkspaceTypes.Cse);
                        break;
                }
                audit = "Followed up on CSE response (still pending)";
                break;

            case ActionCodes.SubmitToMofsl:
                latest.SubmittedToMofslDate = date;
                SetStatus(latest, S.SubmittedToMofsl, WorkspaceTypes.Mofsl);
                latest.Stage5Status = S.SubmittedToMofsl;
                break;

            case ActionCodes.OpsRespondMofsl:
            {
                int hold = CloseHoldCycle(query!);
                query!.Messages.Add(Message(MessageTypes.Response, details));
                latest.QueryHoldDays += hold;
                latest.MofslQueryResolvedDate = date;
                SetStatus(latest, S.MofslQueryResolved, WorkspaceTypes.Mofsl);
                latest.Stage5Status = S.MofslQueryResolved;
                audit = "Responded to MOFSL query";
                auditDetails = $"Hold days added: {hold}";
                break;
            }

            case ActionCodes.SendCommunication:
                latest.CommunicationSentDate = date;
                SetStatus(latest, S.CaseClosed, WorkspaceTypes.Operations);
                latest.Stage6Status = "Case Closed";
                break;

            case ActionCodes.PutOnHold:
                latest.StatusBeforeHold = latest.LatestStatus;
                latest.Remarks = AppendNote(latest.Remarks, $"On hold {date:dd-MMM-yyyy}: {input.Reason!.Trim()}");
                SetStatus(latest, S.OnHold, latest.ActionBy);
                break;

            case ActionCodes.Resume:
            {
                var target = latest.StatusBeforeHold;
                if (string.IsNullOrWhiteSpace(target) || !S.All.Contains(target) || target == S.OnHold)
                    return WorkflowResult.Fail("The status before hold is unknown. Use a workflow override to correct this case.");
                SetStatus(latest, target, latest.ActionBy);
                latest.StatusBeforeHold = null;
                break;
            }

            case ActionCodes.Reject:
                latest.Remarks = AppendNote(latest.Remarks, $"Rejected {date:dd-MMM-yyyy}: {input.Reason!.Trim()}");
                latest.StatusBeforeHold = null;
                SetStatus(latest, S.Rejected, WorkspaceTypes.Operations);
                break;

            case ActionCodes.Cancel:
                latest.Remarks = AppendNote(latest.Remarks, $"Cancelled {date:dd-MMM-yyyy}: {input.Reason!.Trim()}");
                latest.StatusBeforeHold = null;
                SetStatus(latest, S.CancelledByClient, WorkspaceTypes.Operations);
                break;

            case ActionCodes.CseRespond:
            {
                int hold = CloseHoldCycle(query!);
                query!.Messages.Add(Message(MessageTypes.Response, details));

                if (query.QueryType == QueryTypes.MissingInformation)
                {
                    latest.QueryHoldDays += hold;
                    latest.Stage1ResubmissionDate = date;
                    latest.ResubmissionDate = date;
                    SetStatus(latest, S.ResubmittedByCse, WorkspaceTypes.Operations);
                    latest.Stage1Status = S.ResubmittedByCse;
                    audit = "CSE responded to Stage 1 query";
                }
                else
                {
                    // Stage 3 → Stage 4: the corrected form / resolution is a new
                    // inward record sharing the Reference ID.
                    bool isReturn = query.QueryType == QueryTypes.FormReturned;
                    newRecord = CreateResubmissionRecord(
                        latest,
                        isReturn ? "Resubmission" : "Discrepancy Resolution",
                        date, now);
                    newRecord.QueryHoldDays = latest.QueryHoldDays + hold;
                    if (isReturn)
                    {
                        newRecord.ResubmittedFormReceivedDate = date;
                        newRecord.LatestStatus = S.ResubmittedFormUnderReview;
                    }
                    else
                    {
                        newRecord.DiscrepancyResolutionReceivedDate = date;
                        newRecord.LatestStatus = S.DiscrepancyResolutionReceived;
                    }
                    newRecord.Stage4Status = newRecord.LatestStatus;
                    newRecord.ActionBy = WorkspaceTypes.Operations;
                    audit = isReturn ? "CSE resubmitted corrected form" : "CSE submitted discrepancy resolution";
                    auditDetails = $"New {newRecord.EntryType} record created for the same Reference ID.";
                }
                auditDetails = AppendNote(auditDetails, $"Hold days added: {hold}");
                if (input.FileCount > 0) auditDetails = AppendNote(auditDetails, $"{input.FileCount} document(s) attached");
                break;
            }

            case ActionCodes.MofslRaiseQuery:
                newQuery = Raise(QueryTypes.MofslQuery, input.MofslQueryType!.Trim(), WorkspaceTypes.Operations);
                latest.MofslQueryRaisedDate ??= date;
                latest.MofslQueryType = input.MofslQueryType.Trim();
                latest.MofslQueryResolvedDate = null;
                SetStatus(latest, S.QueryRaisedByMofsl, WorkspaceTypes.Operations);
                latest.Stage5Status = S.QueryRaisedByMofsl;
                audit = "MOFSL raised a query";
                auditDetails = $"Type: {latest.MofslQueryType}";
                break;

            case ActionCodes.MofslSendAccountDetails:
                // Status stays within the requirement's list until Operations logs the
                // account as opened; the next step moves to Operations.
                latest.MofslAccountNumber = input.AccountNumber!.Trim();
                latest.MofslAccountOpeningDate = date;
                latest.MofslAccountDetails = details.Length > 0 ? details : null;
                SetStatus(latest, latest.LatestStatus, WorkspaceTypes.Operations);
                latest.Stage5Status = "Account details sent by MOFSL";
                audit = "MOFSL sent the account details";
                auditDetails = $"Account opened {date:dd-MMM-yyyy}";
                break;

            case ActionCodes.MofslAcceptResponse:
                Resolve(query!);
                audit = "MOFSL accepted the query resolution";
                break;

            case ActionCodes.MofslFollowUp:
                Reopen(query!);
                latest.MofslQueryResolvedDate = null;
                SetStatus(latest, S.QueryRaisedByMofsl, WorkspaceTypes.Operations);
                latest.Stage5Status = S.QueryRaisedByMofsl;
                audit = "MOFSL followed up on its query (still pending)";
                break;

            case ActionCodes.RecordAccountOpened:
                foreach (var q in ctx.Queries.Where(q =>
                             q.QueryType == QueryTypes.MofslQuery && q.Status == QueryStatuses.Responded))
                {
                    Resolve(q);
                }
                latest.AccountNumber = input.AccountNumber!.Trim();
                latest.AccountOpeningDate = date;
                latest.Stage5Status = "Completed";
                latest.Stage6Status = S.AccountOpened;
                SetStatus(latest, S.AccountOpened, WorkspaceTypes.Operations);
                audit = "Recorded account opened (details from MOFSL)";
                auditDetails = $"Opening date {date:dd-MMM-yyyy}";
                break;

            default:
                return WorkflowResult.Fail($"Action '{code}' is not supported.");
        }

        var touched = newRecord ?? latest;
        touched.TouchCount += 1;
        // A workflow step moves the case on, so a manual Current Stage override no longer applies.
        latest.IsCurrentStageManuallyOverridden = false;

        // Re-run the field-level business validation on the changed record.
        var validation = WorkflowValidationHelper.Validate(
            touched.AccountType, touched.Channel, touched.ActionBy, touched.DiscrepancyType,
            touched.Stage4ReviewOutcome, touched.InwardDate, touched.AccountOpeningDate,
            touched.DiscrepancyRaisedDate, touched.DiscrepancyResolutionReceivedDate,
            touched.SubmittedToMofslDate, touched.MofslQueryRaisedDate, touched.MofslQueryResolvedDate);
        if (validation != null) return WorkflowResult.Fail(validation);

        return new WorkflowResult
        {
            FromStatus = from,
            ToStatus = touched.LatestStatus,
            NewRecord = newRecord,
            NewQuery = newQuery,
            UpdatedQuery = query,
            AuditAction = audit,
            AuditDetails = auditDetails
        };
    }

    private static bool IsDigital(Account a) =>
        string.Equals(a.Channel?.Trim(), "Digital", StringComparison.OrdinalIgnoreCase);

    private static string? ValidateInput(WorkflowContext ctx, ActionDefinition def, WorkflowInput input)
    {
        if (input.ActionDate.HasValue)
        {
            var d = input.ActionDate.Value.Date;
            if (d > ctx.Today.Date) return "The action date cannot be in the future.";
            if (d < ctx.Latest.InwardDate.Date) return "The action date cannot be before the latest inward date of this case.";
        }

        foreach (var field in def.Fields.Where(f => f.Required))
        {
            string? value = field.Name switch
            {
                "details" => input.Details,
                "discrepancyType" => input.DiscrepancyType,
                "mofslQueryType" => input.MofslQueryType,
                "accountNumber" => input.AccountNumber,
                "reason" => input.Reason,
                "files" => input.FileCount > 0 ? "ok" : null,
                _ => "ok"
            };
            if (string.IsNullOrWhiteSpace(value)) return $"{field.Label} is required.";
        }

        if (input.Details?.Length > MaxTextLength) return $"Text must be at most {MaxTextLength} characters.";
        if (input.Reason?.Length > MaxReasonLength) return $"Reason must be at most {MaxReasonLength} characters.";

        if (def.Fields.Any(f => f.Name == "discrepancyType") &&
            !DiscrepancyTypes.Contains(input.DiscrepancyType?.Trim()))
            return "Invalid discrepancy type.";

        if (def.Code == ActionCodes.MofslRaiseQuery && input.MofslQueryType!.Trim().Length > 100)
            return "MOFSL query type must be at most 100 characters.";

        if (def.Code is ActionCodes.RecordAccountOpened or ActionCodes.MofslSendAccountDetails)
        {
            if (!AccountNumberPattern.IsMatch(input.AccountNumber!.Trim()))
                return "Account number must be 4–30 letters, digits, '-' or '/'.";
            var openDate = (input.ActionDate ?? ctx.Today).Date;
            var submitted = ctx.Records.Max(r => r.SubmittedToMofslDate);
            if (submitted.HasValue && openDate < submitted.Value.Date)
                return "The account opening date cannot be before the MOFSL submission date.";
        }

        if (def.Code == ActionCodes.SendCommunication)
        {
            var sentDate = (input.ActionDate ?? ctx.Today).Date;
            var opened = ctx.Records.Max(r => r.AccountOpeningDate);
            if (opened.HasValue && sentDate < opened.Value.Date)
                return "The communication date cannot be before the account opening date.";
        }

        return null;
    }

    internal static Account CreateResubmissionRecord(Account source, string entryType, DateTime date, DateTime now) => new()
    {
        ReferenceId = source.ReferenceId,
        RequestId = source.RequestId,
        EntryType = entryType,
        ClientName = source.ClientName,
        PanNo = source.PanNo,
        AccountType = source.AccountType,
        Channel = source.Channel,
        ActionBy = WorkspaceTypes.Operations,
        Location = source.Location,
        Segment = source.Segment,
        Cse = source.Cse,
        RelationshipManager = source.RelationshipManager,
        Processor = source.Processor,
        InwardDate = date,
        OriginalInwardDate = source.OriginalInwardDate,
        ResubmissionDate = date,

        Stage1QueryRaisedDate = source.Stage1QueryRaisedDate,
        Stage1QueryDetails = source.Stage1QueryDetails,
        Stage1ResubmissionDate = source.Stage1ResubmissionDate,
        Stage1Status = source.Stage1Status,

        FormPreparedDate = source.FormPreparedDate,
        PhysicalFormSubmittedToCseDate = source.PhysicalFormSubmittedToCseDate,
        DigitalFormSentToClientDate = source.DigitalFormSentToClientDate,
        Stage2Status = source.Stage2Status,

        SignedFormReceivedDate = source.SignedFormReceivedDate,
        DiscrepancyType = source.DiscrepancyType,
        Stage3Status = "Resolution Received",

        QueryCount = source.QueryCount,
        QueryDetails = source.QueryDetails,
        QueryHoldDays = source.QueryHoldDays,
        TouchCount = source.TouchCount,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static string? Join(params string?[] parts)
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return list.Count == 0 ? null : string.Join(" ", list);
    }

    private static string AppendNote(string? existing, string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return existing ?? string.Empty;
        return string.IsNullOrWhiteSpace(existing) ? note : $"{existing} | {note}";
    }
}
