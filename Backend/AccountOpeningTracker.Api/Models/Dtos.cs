using System.Reflection;
using AccountOpeningTracker.Api.Security;
using AccountOpeningTracker.Api.Workflow;

namespace AccountOpeningTracker.Api.Models;

/// <summary>
/// An account record as returned by the API: every Account field (masked
/// when the user lacks cases.view.sensitive) plus derived values.
/// </summary>
public sealed class AccountDto : Account
{
    public int ResubmissionCount { get; set; }
    public int RecordCount { get; set; }
    public bool IsLatestRecord { get; set; }
    public bool IsMasked { get; set; }
    public string StatusStage { get; set; } = string.Empty;
    public int OpenQueryCount { get; set; }

    private static readonly PropertyInfo[] AccountProperties =
        typeof(Account).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToArray();

    public static AccountDto From(Account source, bool canSeeSensitive)
    {
        var dto = new AccountDto();
        foreach (var p in AccountProperties) p.SetValue(dto, p.GetValue(source));

        dto.StatusStage = WorkflowStatuses.StageOf(source.LatestStatus);

        if (!canSeeSensitive)
        {
            dto.IsMasked = true;
            dto.ClientName = SensitiveData.MaskName(source.ClientName);
            dto.PanNo = SensitiveData.MaskPan(source.PanNo);
            dto.AccountNumber = SensitiveData.MaskAccountNumber(source.AccountNumber);
            dto.QueryDetails = Redact(source.QueryDetails, source);
            dto.Stage1QueryDetails = Redact(source.Stage1QueryDetails, source);
            dto.Remarks = Redact(source.Remarks, source);
        }
        return dto;
    }

    private static string? Redact(string? text, Account a) =>
        text == null ? null : SensitiveData.Redact(text, a.ClientName, a.PanNo, a.AccountNumber);

    public static List<AccountDto> FromRecords(IEnumerable<Account> records, bool canSeeSensitive,
        IReadOnlyDictionary<string, int>? openQueriesByReference = null)
    {
        var list = records.ToList();
        var groups = list.GroupBy(r => r.ReferenceId).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<AccountDto>(list.Count);
        foreach (var r in list)
        {
            var group = groups[r.ReferenceId];
            var latest = CaseCalculator.Latest(group);
            var dto = From(r, canSeeSensitive);
            dto.RecordCount = group.Count;
            dto.ResubmissionCount = CaseCalculator.ResubmissionCount(group.Count);
            dto.IsLatestRecord = ReferenceEquals(latest, r);
            dto.OpenQueryCount = openQueriesByReference != null &&
                                 openQueriesByReference.TryGetValue(r.ReferenceId, out var n) ? n : 0;
            result.Add(dto);
        }
        return result;
    }
}

public sealed record QueryMessageDto(
    int Id, string AuthorName, string AuthorTeam, string MessageType, string Body, bool AiAssisted, DateTime CreatedAt);

public sealed record QueryDto(
    int Id,
    int AccountId,
    string ReferenceId,
    string Stage,
    string QueryType,
    string QueryTypeLabel,
    string? Category,
    string FromTeam,
    string ToTeam,
    string Status,
    string RaisedByName,
    DateTime RaisedAt,
    DateTime? RespondedAt,
    DateTime? ResolvedAt,
    int HoldDays,
    int FollowUpCount,
    string? CaseStatus,
    string? ClientName,
    List<QueryMessageDto> Messages)
{
    public static QueryDto From(CaseQuery q, Account? caseRecord, bool canSeeSensitive)
    {
        string Body(string text) => canSeeSensitive || caseRecord == null
            ? text
            : SensitiveData.Redact(text, caseRecord.ClientName, caseRecord.PanNo, caseRecord.AccountNumber);

        return new QueryDto(
            q.Id, q.AccountId, q.ReferenceId, q.Stage, q.QueryType, QueryTypes.Label(q.QueryType), q.Category,
            q.FromTeam, q.ToTeam, q.Status, q.RaisedByName, q.RaisedAt, q.RespondedAt, q.ResolvedAt,
            q.HoldDays, q.FollowUpCount,
            caseRecord?.LatestStatus,
            caseRecord == null ? null : canSeeSensitive ? caseRecord.ClientName : SensitiveData.MaskName(caseRecord.ClientName),
            q.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
                .Select(m => new QueryMessageDto(m.Id, m.AuthorName, m.AuthorTeam, m.MessageType, Body(m.Body), m.AiAssisted, m.CreatedAt))
                .ToList());
    }
}

public sealed record AuditEventDto(
    long Id, int? AccountId, string? ReferenceId, string UserName, string? Team, string Action,
    string? FromStatus, string? ToStatus, string? Details, DateTime CreatedAt)
{
    public static AuditEventDto From(AuditEvent e) => new(
        e.Id, e.AccountId, e.ReferenceId, e.UserName, e.Team, e.Action, e.FromStatus, e.ToStatus, e.Details, e.CreatedAt);
}

public sealed record AvailableActionDto(
    string Code,
    string Label,
    string Team,
    string Description,
    string Tone,
    int? QueryId,
    string? QueryLabel,
    IReadOnlyList<ActionField> Fields);

public sealed record CaseAttachmentDto(int RequestId, int Id, string FileName, string ContentType, long SizeBytes, DateTime UploadedAt);

public sealed record CaseDocumentDto(
    int Id, int? QueryId, string Purpose, string FileName, string ContentType, long SizeBytes,
    string UploadedByName, string Team, DateTime UploadedAt);

/// <summary>The drafted input of the action the AI recommends, so a person can open it pre-filled and edit it.</summary>
public sealed record SuggestedInputDto(
    string Action, int? QueryId, string? DiscrepancyType, string? Details,
    /// <summary>First step of the plan, used to tell whether the suggestion still fits the case's current status.</summary>
    string FirstAction, int? FirstQueryId, int StepCount);

public sealed record AiInsightDto(
    long Id, string Kind, int? QueryId, string Outcome, double Confidence, string Summary, List<string> Findings,
    string? RecommendedAction, string Mode, string ActionStatus, string? ActionResult, string? AppliedByName,
    DateTime? AppliedAt, string Provider, string Model, DateTime CreatedAt, SuggestedInputDto? Suggested)
{
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static AiInsightDto From(AiCaseInsight i)
    {
        List<string> findings;
        try { findings = System.Text.Json.JsonSerializer.Deserialize<List<string>>(i.FindingsJson) ?? new(); }
        catch (System.Text.Json.JsonException) { findings = new(); }

        SuggestedInputDto? suggested = null;
        if (!string.IsNullOrWhiteSpace(i.RecommendedInputJson))
        {
            try
            {
                // The step a person should review: the one carrying a drafted message, else the last step.
                var steps = System.Text.Json.JsonSerializer.Deserialize<List<AccountOpeningTracker.Api.Workflow.PlannedStep>>(i.RecommendedInputJson, Web) ?? new();
                var step = steps.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.Input.Details)) ?? steps.LastOrDefault();
                if (step != null)
                    suggested = new SuggestedInputDto(step.Action, step.Input.QueryId, step.Input.DiscrepancyType, step.Input.Details,
                        steps[0].Action, steps[0].Input.QueryId, steps.Count);
            }
            catch (System.Text.Json.JsonException) { }
        }

        return new AiInsightDto(i.Id, i.Kind, i.QueryId, i.Outcome, i.Confidence, i.Summary, findings, i.RecommendedAction,
            i.Mode, i.ActionStatus, i.ActionResult, i.AppliedByName, i.AppliedAt, i.Provider, i.Model, i.CreatedAt, suggested);
    }
}

public sealed record CaseDetailDto(
    string ReferenceId,
    AccountDto Latest,
    List<AccountDto> Records,
    List<QueryDto> Queries,
    List<AuditEventDto> History,
    List<AvailableActionDto> AvailableActions,
    List<AiInsightDto> AiInsights,
    List<CaseAttachmentDto> Attachments,
    List<CaseDocumentDto> Documents,
    string? ApplicationFormStatus,
    string? FormNotReadyReason,
    string AutomationMode,
    DateTime? OriginalInwardDate,
    int ResubmissionCount,
    int SlaThresholdWorkingDays,
    bool CanEditDetails,
    bool CanOverride);
