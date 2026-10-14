using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AccountOpeningTracker.Api.Data;
using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Security;
using AccountOpeningTracker.Api.Services;
using AccountOpeningTracker.Api.Workflow;
using Microsoft.EntityFrameworkCore;

namespace AccountOpeningTracker.Api.Ai;

public sealed class ExtractRequestInput
{
    public string Text { get; set; } = string.Empty;
}

public sealed record ExtractedField(string Name, string? Value);

public sealed record ExtractRequestResult(
    long InteractionId, Dictionary<string, string?> Fields, List<string> Warnings, string? Notes,
    double Confidence, string Provider, string Model);

public sealed class CaseSummaryRequest
{
    public string ReferenceId { get; set; } = string.Empty;
}

public sealed record CaseFacts(
    string ReferenceId, string CurrentStage, string LatestStatus, string ActionBy, string Channel, string AccountType,
    string Segment, string RftStatus, string? OriginalInwardDate, int GrossTatDays, int NetTatDays,
    int CurrentAgingDays, int SlaThresholdWorkingDays, bool IsSlaBreached, int ResubmissionCount,
    int OpenQueries, int TotalQueries, int QueryHoldDays);

public sealed record CaseSummaryResult(
    long InteractionId, string Summary, string NextStep, List<string> PendingItems, List<string> Risks,
    List<string> UnverifiedNumbers, CaseFacts Facts, string Provider, string Model);

public sealed class MisInsightsRequest
{
    public string? Cse { get; set; }
    public string? Location { get; set; }
    public string? Segment { get; set; }
}

public sealed record MisInsightsResult(
    long InteractionId, string Headline, List<string> Insights, List<string> Recommendations,
    List<string> UnverifiedNumbers, string Provider, string Model);

/// <summary>
/// AI features wired into the workflow. Responsibilities:
///  - enforce access (case scope, team + permission for the related action),
///  - build prompts from redacted data only,
///  - validate and normalise model output,
///  - log usage metadata (never prompts or outputs).
/// AI never changes data: every result is a suggestion the user reviews
/// and then submits through the normal, validated workflow endpoints.
/// Deterministic values (TAT, SLA, RFT, MIS arithmetic) are computed by
/// code and passed to the model as read-only facts.
/// </summary>
public sealed class AiService
{
    private readonly ApplicationDbContext _db;
    private readonly IAiProvider _provider;
    private readonly CaseService _cases;
    private readonly CaseAccessService _caseAccess;
    private readonly ISettingsService _settings;
    private readonly ILogger<AiService> _log;

    public AiService(
        ApplicationDbContext db, IAiProvider provider, CaseService cases,
        CaseAccessService caseAccess, ISettingsService settings, ILogger<AiService> log)
    {
        _db = db;
        _provider = provider;
        _cases = cases;
        _caseAccess = caseAccess;
        _settings = settings;
        _log = log;
    }

    public IAiProvider Provider => _provider;

    private const string BaseSystemPrompt =
        "You are the AI assistant inside AccountFlow, an internal account-opening workflow tool used by an " +
        "Operations team, customer service executives (CSE) and the MOFSL account-opening team in India. " +
        "Rules: (1) Reply with ONE JSON object only, exactly matching the requested schema, no markdown. " +
        "(2) Use only the information provided; never invent client details, documents, dates or numbers. " +
        "(3) Placeholders such as [CLIENT], [PAN], [NUMBER], [EMAIL] are redacted values — keep them as placeholders or say 'the client'. " +
        "(4) TAT, aging, SLA, RFT/NRFT and counts are calculated by the system: quote them exactly as given in FACTS and never recalculate them. " +
        "(5) Be concise, professional and specific. Use plain English suitable for a business message.";

    // ------------------------------------------------------------------
    // A. Smart intake: read the CSE's message and fill the new-case form
    // ------------------------------------------------------------------

    public static readonly string[] AccountTypes = { "Individual", "HUF", "Corporate", "NRI", "Minor" };
    public static readonly string[] Segments = { "Retail", "HNI", "Ultra HNI", "Institutional", "Corporate" };
    public const int MaxRequestTextLength = 6000;

    /// <summary>
    /// Extracts case fields from the request details Operations enters. The text necessarily
    /// contains client details, so it is sent as-is (demo uses synthetic data);
    /// output is validated field by field and only fills the form.
    /// </summary>
    public async Task<ExtractRequestResult> ExtractRequestAsync(ExtractRequestInput input, UserAccessProfile user, CancellationToken ct)
    {
        if (!user.InWorkspace(WorkspaceTypes.Operations) || !user.Has(Permissions.CasesCreate))
            throw new UnauthorizedAccessException("Only Operations users who can log requests can use smart intake.");
        var text = input.Text?.Trim() ?? "";
        if (text.Length < 20) throw new ArgumentException("Paste the CSE's request message (at least a few lines).");
        if (text.Length > MaxRequestTextLength) throw new ArgumentException($"The message must be at most {MaxRequestTextLength} characters.");

        var knownCses = await _db.Accounts.AsNoTracking().Where(a => a.Cse != "").Select(a => a.Cse).Distinct().Take(50).ToListAsync(ct);
        var knownLocations = await _db.Accounts.AsNoTracking().Where(a => a.Location != "").Select(a => a.Location).Distinct().Take(50).ToListAsync(ct);

        var prompt = new StringBuilder();
        prompt.AppendLine("TASK: A customer service executive (CSE) sent Operations this account-opening request. Extract the fields for the AccountFlow inward register.");
        prompt.AppendLine("Only extract what the message states. Use null for anything not stated. Do not guess PAN or names.");
        prompt.AppendLine($"Allowed accountType values: {string.Join(", ", AccountTypes)}. Allowed channel values: Physical, Digital. Allowed segment values: {string.Join(", ", Segments)}.");
        if (knownCses.Count > 0) prompt.AppendLine($"Known CSE names (prefer an exact match): {string.Join(", ", knownCses)}");
        if (knownLocations.Count > 0) prompt.AppendLine($"Known locations (prefer an exact match): {string.Join(", ", knownLocations)}");
        prompt.AppendLine($"Today is {CaseService.Today:yyyy-MM-dd}.");
        prompt.AppendLine();
        prompt.AppendLine("MESSAGE:");
        prompt.AppendLine(text);
        prompt.AppendLine();
        prompt.AppendLine("SCHEMA: {\"referenceId\": string|null, \"requestId\": string|null, \"clientName\": string|null, \"panNo\": string|null, " +
                          "\"accountType\": string|null, \"channel\": string|null, \"location\": string|null, \"segment\": string|null, " +
                          "\"cse\": string|null, \"relationshipManager\": string|null, \"inwardDate\": \"YYYY-MM-DD\"|null, " +
                          "\"notes\": string|null (documents mentioned, anything pending or special instructions, max 3 sentences), \"confidence\": number 0-1}");

        var (json, id) = await RunAsync("smart-intake", null, user, prompt.ToString(), ct);

        var warnings = new List<string>();
        string? Str(string name, int max = 150) => AiJson.OptionalString(json, name, max);
        string? Pick(string name, string[] allowed)
        {
            var v = Str(name);
            if (v == null) return null;
            var match = allowed.FirstOrDefault(a => string.Equals(a, v, StringComparison.OrdinalIgnoreCase));
            if (match == null) warnings.Add($"Ignored unrecognised {name} '{v}'.");
            return match;
        }

        var fields = new Dictionary<string, string?>
        {
            ["referenceId"] = Str("referenceId", 100),
            ["requestId"] = Str("requestId", 100),
            ["clientName"] = Str("clientName", 200),
            ["accountType"] = Pick("accountType", AccountTypes),
            ["channel"] = Pick("channel", new[] { "Physical", "Digital" }),
            ["location"] = Str("location", 100),
            ["segment"] = Pick("segment", Segments),
            ["cse"] = Str("cse"),
            ["relationshipManager"] = Str("relationshipManager"),
        };

        var pan = Str("panNo", 20)?.Replace(" ", "").ToUpperInvariant();
        if (pan != null && !Regex.IsMatch(pan, "^[A-Z]{5}[0-9]{4}[A-Z]$"))
        {
            warnings.Add($"The PAN found ('{pan}') is not in the format AAAAA9999A — please check it.");
        }
        fields["panNo"] = pan;

        var inward = Str("inwardDate", 10);
        if (inward != null && DateTime.TryParse(inward, out var d) && d.Date <= CaseService.Today && d.Date > CaseService.Today.AddYears(-1))
            fields["inwardDate"] = d.ToString("yyyy-MM-dd");
        else
        {
            if (inward != null) warnings.Add($"Ignored inward date '{inward}'.");
            fields["inwardDate"] = null;
        }

        // Grounding check: names and PAN must literally appear in the message.
        foreach (var key in new[] { "clientName", "panNo", "referenceId", "requestId" })
        {
            var v = fields[key];
            if (v != null && !text.Replace(" ", "").Contains(v.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Removed {key} '{v}' because it does not appear in the message.");
                fields[key] = null;
            }
        }

        double confidence = Math.Clamp(json.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.5, 0, 1);
        return new ExtractRequestResult(id, fields, warnings, Str("notes", 600), confidence, _provider.Name, _provider.Model);
    }

    // ------------------------------------------------------------------
    // A2. Stage 2: pre-fill the application form from what the CSE sent
    // ------------------------------------------------------------------

    /// <summary>
    /// Fills application-form fields from the CSE's request and their query replies.
    /// Only fills; Operations reviews, edits and saves. Every value is checked:
    /// options must be valid, formats must pass, and free-text values must
    /// literally appear in the source text.
    /// </summary>
    public async Task<ExtractRequestResult> FillApplicationFormAsync(string referenceId, UserAccessProfile user, CancellationToken ct)
    {
        if (!user.InWorkspace(WorkspaceTypes.Operations) || !user.Has(Permissions.WorkflowOperations))
            throw new UnauthorizedAccessException("Only Operations can prepare the application form.");

        var (records, queries, ctx) = await LoadAsync(referenceId, user);
        var latest = ctx.Latest;

        var source = new StringBuilder();
        var requests = await _db.CaseRequests.AsNoTracking()
            .Where(r => r.ReferenceId == referenceId).OrderBy(r => r.Id).Select(r => r.Details).ToListAsync(ct);
        foreach (var r in requests) source.AppendLine($"[CSE request] {r}");
        foreach (var q in queries.OrderBy(q => q.Id))
            foreach (var m in q.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id))
                source.AppendLine($"[{m.AuthorTeam} {m.MessageType}] {m.Body}");
        if (!string.IsNullOrWhiteSpace(latest.Remarks)) source.AppendLine($"[Case remarks] {latest.Remarks}");

        var text = source.ToString().Trim();
        if (text.Length < 20)
            throw new ArgumentException("There is nothing from the CSE to fill the form from. Fill it manually.");
        if (text.Length > 8000) text = text[^8000..];

        var fields = ApplicationFormRules.FieldsFor(latest.AccountType).ToList();
        var prompt = new StringBuilder();
        prompt.AppendLine("TASK: Fill the account-opening application form for this case using ONLY the CSE's messages below.");
        prompt.AppendLine("Use null for anything the messages do not state. Never invent phone numbers, emails, addresses, bank details or names.");
        prompt.AppendLine($"Account type: {latest.AccountType}. Channel: {latest.Channel}. Today is {CaseService.Today:yyyy-MM-dd}.");
        prompt.AppendLine("Field rules:");
        foreach (var f in fields)
        {
            var rule = f.Type switch
            {
                "select" => $"one of: {string.Join(" | ", f.Options!)}",
                "multi" => $"comma-separated list from: {string.Join(", ", f.Options!)}",
                "date" => "YYYY-MM-DD",
                _ => "text"
            };
            prompt.AppendLine($"- {f.Key} ({f.Label}): {rule}");
        }
        prompt.AppendLine();
        prompt.AppendLine("MESSAGES:");
        prompt.AppendLine(text);
        prompt.AppendLine();
        prompt.AppendLine("SCHEMA: {\"fields\": {" + string.Join(", ", fields.Select(f => $"\"{f.Key}\": string|null")) +
                          "}, \"notes\": string|null (anything relevant that does not fit a field, max 2 sentences), \"confidence\": number 0-1}");

        var (json, id) = await RunAsync("form-fill", referenceId, user, prompt.ToString(), ct);

        var warnings = new List<string>();
        var raw = new Dictionary<string, string?>();
        if (json.TryGetProperty("fields", out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var f in fields)
            {
                var v = AiJson.OptionalString(obj, f.Key, 300);
                if (v == null) continue;
                if (f.Options != null && f.Type == "select")
                {
                    var match = f.Options.FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase));
                    if (match == null) { warnings.Add($"Ignored {f.Label} '{v}' (not a valid option)."); continue; }
                    v = match;
                }
                if (f.Type == "multi")
                {
                    var picked = v.Split(',').Select(x => x.Trim())
                        .Select(x => f.Options!.FirstOrDefault(o => string.Equals(o, x, StringComparison.OrdinalIgnoreCase)))
                        .Where(x => x != null).Distinct().ToList();
                    if (picked.Count == 0) continue;
                    v = string.Join(", ", picked);
                }
                if (f.Type == "date")
                {
                    if (!DateTime.TryParse(v, out var d)) { warnings.Add($"Ignored {f.Label} '{v}'."); continue; }
                    v = d.ToString("yyyy-MM-dd");
                }
                raw[f.Key] = v;
            }
        }

        var data = ApplicationFormRules.Normalize(raw);

        // Grounding: copied values must literally appear in what the CSE wrote.
        string Squash(string x) => Regex.Replace(x, @"[\s\-]", "").ToLowerInvariant();
        var haystack = Squash(text);
        foreach (var key in new[] { "fatherOrSpouseName", "mobile", "email", "addressLine1", "addressLine2", "city", "pinCode",
                                    "bankName", "bankAccountNumber", "ifsc", "guardianName", "guardianPan", "nomineeName" })
        {
            if (data.TryGetValue(key, out var v) && !haystack.Contains(Squash(v)))
            {
                warnings.Add($"Removed {ApplicationFormRules.Fields.First(f => f.Key == key).Label} '{v}' because it does not appear in the CSE's messages.");
                data.Remove(key);
            }
        }

        // Format check: drop anything invalid rather than fill a wrong value.
        foreach (var f in fields)
        {
            if (!data.TryGetValue(f.Key, out var v)) continue;
            var single = new Dictionary<string, string> { [f.Key] = v };
            var errs = ApplicationFormRules.FormatErrors(single, CaseService.Today);
            if (errs.Count > 0) { warnings.Add($"Removed {errs[0]}"); data.Remove(f.Key); }
        }

        double confidence = Math.Clamp(json.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.5, 0, 1);
        return new ExtractRequestResult(id, data.ToDictionary(k => k.Key, k => (string?)k.Value), warnings,
            AiJson.OptionalString(json, "notes", 400), confidence, _provider.Name, _provider.Model);
    }

    // ------------------------------------------------------------------
    // A3. Stage 3: pre-check of one signed-form / document image (vision)
    // ------------------------------------------------------------------

    public static readonly string[] DocumentTypes =
        { "Signed application form", "PAN card", "Address proof", "Bank proof", "Photograph", "Other" };

    /// <summary>
    /// Reads one uploaded image and reports what it can see. It does NOT decide
    /// anything: the comparison with the form is done in code (AutomationPolicy)
    /// and Operations makes the call.
    /// </summary>
    public async Task<DocumentObservation> ReadDocumentImageAsync(
        Account latest, IReadOnlyDictionary<string, string> form, CaseDocument doc, int userId, CancellationToken ct)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("TASK: This image was sent back by a customer service executive (CSE) as part of a signed account-opening application.");
        prompt.AppendLine("Describe ONLY what you can actually see. Copy values exactly as written on the document. Use null when something is not on this document or not readable.");
        prompt.AppendLine("Do not judge whether a signature is genuine; only say whether a signature is present where one is expected.");
        prompt.AppendLine($"Today's date is {CaseService.Today:dd-MMM-yyyy} (dates up to today are not in the future).");
        prompt.AppendLine("otherIssues is ONLY for problems not already covered by the other keys: do not mention the signature, readability, or differences in name/PAN/date of birth/bank details there.");
        prompt.AppendLine($"Allowed documentType values: {string.Join(" | ", DocumentTypes)}.");
        prompt.AppendLine();
        prompt.AppendLine("FORM SENT FOR SIGNATURE (for context):");
        prompt.AppendLine($"- Client name: {latest.ClientName}; PAN: {latest.PanNo}; account type: {latest.AccountType}");
        foreach (var key in new[] { "dateOfBirth", "fatherOrSpouseName", "addressLine1", "city", "pinCode", "bankAccountNumber", "ifsc" })
            if (form.TryGetValue(key, out var v)) prompt.AppendLine($"- {key}: {v}");
        prompt.AppendLine();
        prompt.AppendLine("SCHEMA: {\"documentType\": string, \"readable\": boolean, \"qualityIssue\": string|null (blurred, cut off, too dark, glare…), " +
                          "\"signatureExpected\": boolean (does this document have a place for the applicant's signature?), " +
                          "\"signaturePresent\": boolean|null (is a signature visible in that place?), " +
                          "\"fields\": {\"clientName\": string|null, \"panNo\": string|null, \"dateOfBirth\": string|null, \"address\": string|null, " +
                          "\"fatherOrSpouseName\": string|null, \"bankAccountNumber\": string|null, \"ifsc\": string|null}, " +
                          "\"otherIssues\": [string] (max 4, e.g. 'overwriting in date of birth', 'page number 2 of 3 — other pages not seen'), \"confidence\": number 0-1}");

        var (json, _) = await RunAsync("document-check", latest.ReferenceId, userId, prompt.ToString(), ct,
            new AiImage(doc.Data, doc.ContentType));

        string? Field(string name)
        {
            if (!json.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object) return null;
            return AiJson.OptionalString(f, name, 300);
        }
        bool? Bool(string name) => json.TryGetProperty(name, out var b) && (b.ValueKind == JsonValueKind.True || b.ValueKind == JsonValueKind.False)
            ? b.GetBoolean() : null;

        var type = AiJson.OptionalString(json, "documentType", 60);
        type = DocumentTypes.FirstOrDefault(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase)) ?? "Other";

        return new DocumentObservation(
            doc.FileName,
            type,
            Bool("readable") ?? true,
            AiJson.OptionalString(json, "qualityIssue", 200),
            Bool("signatureExpected") ?? false,
            Bool("signaturePresent"),
            new Dictionary<string, string?>
            {
                ["clientName"] = Field("clientName"),
                ["panNo"] = Field("panNo"),
                ["dateOfBirth"] = Field("dateOfBirth"),
                ["address"] = Field("address"),
                ["fatherOrSpouseName"] = Field("fatherOrSpouseName"),
                ["bankAccountNumber"] = Field("bankAccountNumber"),
                ["ifsc"] = Field("ifsc"),
            },
            AiJson.StringList(json, "otherIssues", 4, 200),
            Math.Clamp(json.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.5, 0, 1));
    }

    // ------------------------------------------------------------------
    // B. Intake completeness check (drives the automatic Stage 1 query)
    // ------------------------------------------------------------------

    public async Task<(IntakeAssessment Assessment, long InteractionId)> AssessIntakeAsync(
        Account latest, IReadOnlyList<string> requiredMissing, int userId, CancellationToken ct)
    {
        string Present(string? v) => string.IsNullOrWhiteSpace(v) ? "MISSING" : "provided";

        var prompt = new StringBuilder();
        prompt.AppendLine("TASK: Operations has just logged an account-opening request from a CSE (Indian stock-broking account, MOFSL). " +
                          "Decide whether the information is complete enough to prepare the application form, or whether a query must go back to the CSE.");
        // Only the Minor rule is applied: the application form has a guardian section for it.
        // Other account-type documents (HUF Karta, NRI, Corporate) are not in the requirement.
        prompt.AppendLine("Flag an item ONLY if (a) the notes say it is missing, pending, unclear or to-follow, or (b) the account type is Minor " +
                          "and the notes do not confirm the guardian's details. Do not ask for any other account-type documents. " +
                          "For a request with no problems mentioned, it is complete.");
        prompt.AppendLine();
        prompt.AppendLine("REQUEST:");
        prompt.AppendLine($"- Account type: {latest.AccountType}; channel: {latest.Channel}; segment: {Fallback(latest.Segment)}; location: {Fallback(latest.Location)}; entry type: {latest.EntryType}");
        prompt.AppendLine($"- Client name: {Present(latest.ClientName)}; PAN: {Present(latest.PanNo)}; RM: {Present(latest.RelationshipManager)}; CSE: {Present(latest.Cse)}");
        prompt.AppendLine($"- Missing required fields found by the system: {(requiredMissing.Count == 0 ? "none" : string.Join(", ", requiredMissing))}");
        prompt.AppendLine($"- Notes from the request: {(string.IsNullOrWhiteSpace(latest.Remarks) ? "(none)" : Redact(latest.Remarks, latest, 2000))}");
        prompt.AppendLine();
        prompt.AppendLine("SCHEMA: {\"complete\": boolean, \"confidence\": number 0-1, \"missingItems\": array of short strings (what the CSE must provide; empty if complete), " +
                          "\"queryMessage\": string|null (2-3 polite sentences to the CSE asking for exactly those items; null if complete), \"summary\": string (one sentence)}");

        var (json, id) = await RunAsync("intake-check", latest.ReferenceId, userId, prompt.ToString(), ct, null);

        var assessment = new IntakeAssessment(
            AiJson.OptionalBool(json, "complete") ?? false,
            Confidence(json),
            AiJson.StringList(json, "missingItems", 8, 160).Select(Restore).ToList(),
            AiJson.OptionalString(json, "queryMessage", 1200) is { } m ? Restore(m) : null,
            Restore(AiJson.OptionalString(json, "summary", 400) ?? "Intake check completed."));
        return (assessment, id);
    }

    // ------------------------------------------------------------------
    // C. Review of a CSE reply (drives automatic accept / follow-up)
    // ------------------------------------------------------------------

    public async Task<(ResponseAssessment Assessment, long InteractionId)> AssessResponseAsync(
        Account latest, CaseQuery query, int userId, CancellationToken ct)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("TASK: Operations asked the CSE for information or a correction on an account-opening case, and the CSE has replied. " +
                          "Decide whether the LATEST reply fully resolves EVERY point Operations raised (including earlier follow-ups).");
        prompt.AppendLine("A point is resolved only if the reply provides it or clearly confirms it has been done/attached. Vague replies " +
                          "(\"will send\", \"checking with client\", \"ok\") do not resolve anything.");
        prompt.AppendLine();
        prompt.AppendLine("CASE CONTEXT:");
        prompt.AppendLine(CaseContext(latest, 1));
        prompt.AppendLine();
        prompt.AppendLine("QUERY THREAD (oldest first):");
        prompt.AppendLine(Thread(query, latest, maxMessages: 12));
        prompt.AppendLine();
        prompt.AppendLine("SCHEMA: {\"resolved\": boolean, \"confidence\": number 0-1, \"outstandingItems\": array of short strings (what is still missing; empty if resolved), " +
                          "\"followUpMessage\": string|null (1-2 polite sentences to the CSE introducing the outstanding items; null if resolved), \"summary\": string (one sentence)}");

        var (json, id) = await RunAsync("response-review", latest.ReferenceId, userId, prompt.ToString(), ct, null);

        var outstanding = AiJson.StringList(json, "outstandingItems", 8, 160).Select(Restore).ToList();
        bool resolved = (AiJson.OptionalBool(json, "resolved") ?? false) && outstanding.Count == 0;
        var assessment = new ResponseAssessment(
            resolved,
            Confidence(json),
            outstanding,
            AiJson.OptionalString(json, "followUpMessage", 800) is { } m ? Restore(m) : null,
            Restore(AiJson.OptionalString(json, "summary", 400) ?? "Response reviewed."));
        return (assessment, id);
    }

    private static double Confidence(JsonElement json) =>
        Math.Clamp(json.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0.5, 0, 1);

    // ------------------------------------------------------------------
    // D. Case briefing for whoever opens the case
    // ------------------------------------------------------------------

    public async Task<CaseSummaryResult> SummarizeCaseAsync(CaseSummaryRequest request, UserAccessProfile user, CancellationToken ct)
    {
        var (records, queries, ctx) = await LoadAsync(request.ReferenceId, user);
        var latest = ctx.Latest;
        int sla = await _settings.GetSlaThresholdAsync();
        CaseCalculator.RefreshForRead(records, CaseService.Today, sla);

        var visible = queries.Where(q => CaseAccessService.CanSeeQuery(q, user)).ToList();
        var facts = new CaseFacts(
            latest.ReferenceId, latest.CurrentStage, latest.LatestStatus, latest.ActionBy, latest.Channel,
            latest.AccountType, latest.Segment, RftHelper.DetermineRftStatus(records),
            latest.OriginalInwardDate?.ToString("dd-MMM-yyyy"), latest.GrossTatDays, latest.NetTatDays,
            latest.CurrentAgingDays, sla, latest.IsSlaBreached, CaseCalculator.ResubmissionCount(records.Count),
            visible.Count(q => q.Status != QueryStatuses.Resolved), visible.Count, latest.QueryHoldDays);

        var history = await _db.AuditEvents.AsNoTracking()
            .Where(e => e.ReferenceId == request.ReferenceId)
            .OrderByDescending(e => e.CreatedAt).Take(25)
            .ToListAsync();

        var available = CaseService.ActionsForUser(ctx, user).Select(a => a.Definition.Label).Distinct().ToList();

        var prompt = new StringBuilder();
        prompt.AppendLine("TASK: Brief the user who just opened this case: what has happened, where it stands, what is pending and who must act next.");
        prompt.AppendLine($"THE USER WORKS IN: {string.Join(", ", user.Workspaces)}");
        prompt.AppendLine();
        prompt.AppendLine("FACTS (system-calculated; quote exactly, do not recalculate):");
        prompt.AppendLine(JsonSerializer.Serialize(facts));
        prompt.AppendLine();
        prompt.AppendLine("RECORDS (oldest first):");
        foreach (var r in records.OrderBy(r => r.InwardDate).ThenBy(r => r.Id))
            prompt.AppendLine($"- {r.EntryType} record, inward {r.InwardDate:dd-MMM-yyyy}, status '{r.LatestStatus}'");
        prompt.AppendLine();
        prompt.AppendLine("QUERIES:");
        if (visible.Count == 0) prompt.AppendLine("(none)");
        foreach (var q in visible) prompt.AppendLine(Thread(q, latest, maxMessages: 4));
        prompt.AppendLine();
        prompt.AppendLine("RECENT HISTORY (newest first):");
        foreach (var e in history)
            prompt.AppendLine($"- {e.CreatedAt:dd-MMM-yyyy} [{e.Team ?? "System"}] {e.Action}{(e.ToStatus != null ? $" → {e.ToStatus}" : "")}");
        prompt.AppendLine();
        prompt.AppendLine($"ACTIONS THIS USER CAN TAKE NOW: {(available.Count == 0 ? "none" : string.Join("; ", available))}");
        prompt.AppendLine();
        prompt.AppendLine("SCHEMA: {\"summary\": string (3-5 sentences), \"nextStep\": string (one sentence: who should do what next), " +
                          "\"pendingItems\": array of strings (max 6), \"risks\": array of strings (e.g. SLA breach, repeated rework; max 4; empty if none)}");

        var (json, id) = await RunAsync("case-summary", request.ReferenceId, user, prompt.ToString(), ct);

        var summary = Restore(AiJson.RequiredString(json, "summary", 20, 1500));
        var nextStep = Restore(AiJson.RequiredString(json, "nextStep", 5, 400));
        var pending = AiJson.StringList(json, "pendingItems", 6, 200).Select(Restore).ToList();
        var risks = AiJson.StringList(json, "risks", 4, 200).Select(Restore).ToList();

        var allowedNumbers = NumbersIn(prompt.ToString());
        var unverified = NumbersIn(string.Join(" ", new[] { summary, nextStep }.Concat(pending).Concat(risks)))
            .Where(n => !allowedNumbers.Contains(n)).Distinct().ToList();

        return new CaseSummaryResult(id, summary, nextStep, pending, risks, unverified, facts, _provider.Name, _provider.Model);
    }

    // ------------------------------------------------------------------
    // E. MIS commentary from aggregated, pseudonymised metrics
    // ------------------------------------------------------------------

    public async Task<MisInsightsResult> MisInsightsAsync(MisInsightsRequest request, UserAccessProfile user, CancellationToken ct)
    {
        var records = await _caseAccess.ScopeRecords(_db.Accounts.AsNoTracking(), user).ToListAsync(ct);
        int sla = await _settings.GetSlaThresholdAsync();
        CaseCalculator.RefreshForRead(records, CaseService.Today, sla);
        var report = MisCalculator.Build(records, new MisFilters(request.Cse, request.Location, request.Segment), sla);

        if (report.Summary.TotalCases == 0)
            throw new ArgumentException("There are no cases for the selected filters.");

        // CSE names are staff identities: replace with stable pseudonyms and map back afterwards.
        var pseudonyms = report.ByCse.Select((b, i) => (b.Name, Alias: $"CSE-{i + 1:00}"))
            .ToDictionary(x => x.Name, x => x.Alias);

        object Compact(MisMetrics m) => new
        {
            cases = m.TotalCases, records = m.TotalRecords, open = m.OpenCases, completed = m.CompletedCases,
            rft = m.RftCases, nrft = m.NrftCases, rftPct = m.RftPercentage,
            avgGrossTat = m.AverageGrossTatDays, avgNetTat = m.AverageNetTatDays, slaBreaches = m.SlaBreachCases, stageSlaBreaches = m.StageSlaBreachCases,
            stage1Queries = m.Stage1QueryCases, stage3Discrepancies = m.Stage3DiscrepancyCases, mofslQueries = m.MofslQueryCases
        };

        var payload = new
        {
            slaThresholdWorkingDays = sla,
            filters = new
            {
                cse = request.Cse == null ? null : pseudonyms.GetValueOrDefault(request.Cse, "selected CSE"),
                request.Location, request.Segment
            },
            overall = Compact(report.Summary),
            byCse = report.ByCse.Take(15).Select(b => new { name = pseudonyms[b.Name], metrics = Compact(b.Metrics) }),
            byLocation = report.ByLocation.Take(15).Select(b => new { b.Name, metrics = Compact(b.Metrics) }),
            bySegment = report.BySegment.Take(15).Select(b => new { b.Name, metrics = Compact(b.Metrics) }),
            pipeline = report.StagePipeline.Select(s => new
            {
                s.Stage, casesNow = s.CaseCount, avgAgingDays = s.AverageAgingDays, slaBreaches = s.SlaBreachCases,
                casesReachedStage = s.CasesMeasured, avgDaysInStage = s.AverageStageTatDays, stageSlaBreaches = s.StageSlaBreachCases
            })
        };

        var prompt = new StringBuilder();
        prompt.AppendLine("TASK: Write management commentary on these account-opening MIS figures. Identify bottlenecks (stages with high aging, counts or average days in stage), " +
                          "sources of rework (CSE-side Stage 1/3 vs MOFSL-side Stage 5), and outliers by CSE, location or segment. " +
                          "Quote figures exactly as given; do not compute new percentages or averages.");
        prompt.AppendLine();
        prompt.AppendLine("MIS DATA (system-calculated):");
        prompt.AppendLine(JsonSerializer.Serialize(payload));
        prompt.AppendLine();
        prompt.AppendLine("SCHEMA: {\"headline\": string (one sentence), \"insights\": array of strings (3-6, each citing specific figures), " +
                          "\"recommendations\": array of strings (2-4 concrete operational actions)}");

        var (json, id) = await RunAsync("mis-insights", null, user, prompt.ToString(), ct);

        string Unmask(string s)
        {
            foreach (var (name, alias) in pseudonyms) s = s.Replace(alias, name);
            return s;
        }

        var headline = AiJson.RequiredString(json, "headline", 10, 300);
        var insights = AiJson.StringList(json, "insights", 6, 300);
        var recommendations = AiJson.StringList(json, "recommendations", 4, 300);
        if (insights.Count == 0) throw new AiOutputException("The AI response contained no insights.");

        var allowedNumbers = NumbersIn(prompt.ToString());
        var unverified = NumbersIn(string.Join(" ", new[] { headline }.Concat(insights).Concat(recommendations)))
            .Where(n => !allowedNumbers.Contains(n) && !Regex.IsMatch(n, @"^0?[1-6]$")) // stage numbers 1-6
            .Distinct().ToList();

        return new MisInsightsResult(id, Unmask(headline), insights.Select(Unmask).ToList(),
            recommendations.Select(Unmask).ToList(), unverified, _provider.Name, _provider.Model);
    }

    // ------------------------------------------------------------------
    // Feedback
    // ------------------------------------------------------------------

    public async Task<bool> RecordFeedbackAsync(long interactionId, bool accepted, UserAccessProfile user)
    {
        var log = await _db.AiInteractionLogs.FirstOrDefaultAsync(l => l.Id == interactionId && l.UserId == user.UserId);
        if (log == null) return false;
        log.Accepted = accepted;
        await _db.SaveChangesAsync();
        return true;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<(List<Account> Records, List<CaseQuery> Queries, WorkflowContext Ctx)> LoadAsync(
        string referenceId, UserAccessProfile user)
    {
        var records = await _cases.LoadCaseRecordsAsync(referenceId, user)
                      ?? throw new KeyNotFoundException("Case not found.");
        var queries = await _cases.LoadQueriesAsync(referenceId);
        var ctx = new WorkflowContext { Records = records, Queries = queries, Today = CaseService.Today };
        return (records, queries, ctx);
    }

    /// <summary>Non-identifying case context. No names, PAN, account numbers or staff names.</summary>
    private static string CaseContext(Account a, int recordCount) =>
        $"- Account type: {a.AccountType}; channel: {a.Channel}; segment: {Fallback(a.Segment)}; location: {Fallback(a.Location)}\n" +
        $"- Current stage: {a.CurrentStage}; latest status: {a.LatestStatus}; action by: {a.ActionBy}\n" +
        $"- Entry type of latest record: {a.EntryType}; records for this case: {recordCount}\n" +
        $"- Discrepancy type on record: {Fallback(a.DiscrepancyType)}; MOFSL query type: {Fallback(a.MofslQueryType)}";

    private static string Fallback(string? s) => string.IsNullOrWhiteSpace(s) ? "n/a" : s;

    private static string Thread(CaseQuery q, Account a, int maxMessages = 8)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Query #{q.Id}: {QueryTypes.Label(q.QueryType)}{(q.Category != null ? $" ({q.Category})" : "")}, " +
                      $"{q.FromTeam} → {q.ToTeam}, status {q.Status}, raised {q.RaisedAt:dd-MMM-yyyy}, follow-ups {q.FollowUpCount}");
        foreach (var m in q.Messages.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).TakeLast(maxMessages))
            sb.AppendLine($"  [{m.AuthorTeam} {m.MessageType} {m.CreatedAt:dd-MMM-yyyy}] {Redact(m.Body, a, 700)}");
        return sb.ToString().TrimEnd();
    }

    private static string Redact(string? text, Account a, int max)
    {
        var r = SensitiveData.Redact(text, a.ClientName, a.PanNo, a.AccountNumber);
        foreach (var staff in new[] { a.Cse, a.RelationshipManager, a.Processor })
        {
            if (!string.IsNullOrWhiteSpace(staff) && staff.Trim().Length >= 3)
                r = Regex.Replace(r, Regex.Escape(staff.Trim()), "[STAFF]", RegexOptions.IgnoreCase);
        }
        return r.Length > max ? r[..max] + "…" : r;
    }

    /// <summary>Makes model output read naturally while never re-inserting identifiers.</summary>
    private static string Restore(string s) =>
        SensitiveData.Redact(s.Replace("[CLIENT]", "the client"));

    private static HashSet<string> NumbersIn(string text) =>
        Regex.Matches(text, @"\d+(?:\.\d+)?").Select(m => m.Value.TrimStart('0') is { Length: > 0 } v ? v : "0").ToHashSet();

    private Task<(JsonElement Json, long InteractionId)> RunAsync(
        string feature, string? referenceId, UserAccessProfile user, string prompt, CancellationToken ct) =>
        RunAsync(feature, referenceId, user.UserId, prompt, ct, null);

    private async Task<(JsonElement Json, long InteractionId)> RunAsync(
        string feature, string? referenceId, int userId, string prompt, CancellationToken ct, AiImage? image)
    {
        var log = new AiInteractionLog
        {
            UserId = userId,
            Feature = feature,
            ReferenceId = referenceId,
            Provider = _provider.Name,
            Model = image != null ? _provider.VisionModel : _provider.Model
        };
        var sw = Stopwatch.StartNew();
        try
        {
            var raw = await _provider.CompleteAsync(BaseSystemPrompt, prompt, ct, image);
            var json = AiJson.ParseObject(raw);
            log.Success = true;
            return (json, await SaveLogAsync(log, sw));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.Success = false;
            log.Error = ex switch
            {
                AiUnavailableException or AiOutputException => ex.Message,
                TaskCanceledException => "The AI provider timed out.",
                JsonException => "The AI provider returned malformed data.",
                _ => "Unexpected AI error."
            };
            await SaveLogAsync(log, sw);
            _log.LogWarning(ex, "AI feature {Feature} failed: {Error}", feature, log.Error);

            if (ex is AiUnavailableException or AiOutputException) throw;
            if (ex is TaskCanceledException) throw new AiUnavailableException("The AI provider timed out. Try again, or use a smaller/faster model.");
            throw new AiOutputException(log.Error);
        }
    }

    private async Task<long> SaveLogAsync(AiInteractionLog log, Stopwatch sw)
    {
        log.DurationMs = (int)sw.ElapsedMilliseconds;
        log.Error = log.Error?.Length > 500 ? log.Error[..500] : log.Error;
        _db.AiInteractionLogs.Add(log);
        await _db.SaveChangesAsync(CancellationToken.None);
        return log.Id;
    }
}
