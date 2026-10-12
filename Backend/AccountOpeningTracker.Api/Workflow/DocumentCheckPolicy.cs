using System.Globalization;
using System.Text.RegularExpressions;
using AccountOpeningTracker.Api.Models;
using S = AccountOpeningTracker.Api.Models.WorkflowStatuses;

namespace AccountOpeningTracker.Api.Workflow;

/// <summary>What the vision model saw on one uploaded image (no judgements).</summary>
public sealed record DocumentObservation(
    string FileName,
    string DocumentType,
    bool Readable,
    string? QualityIssue,
    bool SignatureExpected,
    bool? SignaturePresent,
    Dictionary<string, string?> Fields,
    List<string> OtherIssues,
    double Confidence);

public sealed record DocumentCheckPlan(
    string Outcome,
    List<string> Findings,
    string Summary,
    string? DiscrepancyType,
    string? Message,
    List<PlannedStep> Steps);

/// <summary>
/// Stage 3 pre-check rules. The AI only reports what it sees on each image;
/// this code compares those values with the case and the application form,
/// decides what counts as an issue, and drafts the discrepancy. The draft is
/// only ever a suggestion: Operations raises the discrepancy (or not).
/// </summary>
public static class DocumentCheckPolicy
{
    public const string IssuesFound = InsightOutcomes.IssuesFound;
    public const string NoIssuesFound = InsightOutcomes.NoIssuesFound;

    private static readonly string[] DateFormats =
        { "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy", "d/M/yyyy", "d-M-yyyy", "dd.MM.yyyy", "dd MMM yyyy", "d MMM yyyy", "dd MMMM yyyy", "d MMMM yyyy" };

    private static string Norm(string s) => Regex.Replace(s, @"[^A-Za-z0-9]", "").ToUpperInvariant();

    private static HashSet<string> NameTokens(string s) =>
        Regex.Split(s.ToUpperInvariant(), @"[^A-Z]+").Where(t => t.Length > 1).ToHashSet();

    private static DateTime? ParseDate(string? s) =>
        s != null && DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : null;

    /// <summary>Field-by-field comparison of what is on the document with what is on record.</summary>
    public static List<string> Mismatches(DocumentObservation o, Account latest, IReadOnlyDictionary<string, string> form)
    {
        var result = new List<string>();
        string? Seen(string k) => o.Fields.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v!.Trim() : null;
        string? Form(string k) => form.TryGetValue(k, out var v) ? v : null;

        var pan = Seen("panNo");
        if (pan != null && Norm(pan) != Norm(latest.PanNo))
            result.Add($"PAN on the document ({pan}) does not match the PAN on record ({latest.PanNo}).");

        var name = Seen("clientName");
        if (name != null && !string.IsNullOrWhiteSpace(latest.ClientName))
        {
            var a = NameTokens(name);
            var b = NameTokens(latest.ClientName);
            // Same words in any order (initials and middle names are tolerated).
            if (a.Count > 0 && b.Count > 0 && !a.IsSubsetOf(b) && !b.IsSubsetOf(a))
                result.Add($"Name on the document ({name}) does not match the client name ({latest.ClientName}).");
        }

        var dobSeen = ParseDate(Seen("dateOfBirth"));
        var dobForm = ParseDate(Form("dateOfBirth"));
        if (dobSeen.HasValue && dobForm.HasValue && dobSeen != dobForm)
            result.Add($"Date of birth on the document ({dobSeen:dd-MMM-yyyy}) does not match the form ({dobForm:dd-MMM-yyyy}).");

        var acct = Seen("bankAccountNumber");
        var acctForm = Form("bankAccountNumber");
        if (acct != null && acctForm != null && Norm(acct) != Norm(acctForm))
            result.Add($"Bank account number on the document does not match the form (ending {acctForm[^Math.Min(4, acctForm.Length)..]}).");

        var ifsc = Seen("ifsc");
        var ifscForm = Form("ifsc");
        if (ifsc != null && ifscForm != null && Norm(ifsc) != Norm(ifscForm))
            result.Add($"IFSC on the document ({ifsc}) does not match the form ({ifscForm}).");

        return result;
    }

    public static DocumentCheckPlan Plan(
        IReadOnlyList<DocumentObservation> observations,
        IReadOnlyList<string> notChecked,
        Account latest,
        IReadOnlyDictionary<string, string> form,
        IReadOnlyList<CaseQuery> queries)
    {
        var findings = new List<string>();
        bool signatureIssue = false, dataIssue = false, missingIssue = false;

        foreach (var o in observations)
        {
            var prefix = $"{o.FileName} ({o.DocumentType}): ";
            if (!o.Readable || o.QualityIssue != null)
            {
                findings.Add(prefix + $"not clearly readable{(o.QualityIssue != null ? $" — {o.QualityIssue}" : "")}.");
                missingIssue = true;
            }
            if (o.SignatureExpected && o.SignaturePresent == false)
            {
                findings.Add(prefix + "no signature in the applicant's signature space.");
                signatureIssue = true;
            }
            foreach (var m in Mismatches(o, latest, form))
            {
                findings.Add(prefix + m);
                dataIssue = true;
            }
            // Signature, readability and field values are checked through the structured
            // answers above; free-text comments about them would only duplicate findings.
            foreach (var other in o.OtherIssues.Where(x => !Regex.IsMatch(x, @"signature|signed|blur|legib|readab|\bPAN\b|date of birth|\bDOB\b|IFSC|account number", RegexOptions.IgnoreCase)))
            {
                findings.Add(prefix + other);
                missingIssue = true;
            }
        }

        if (observations.Count > 0 && !observations.Any(o => o.DocumentType == "Signed application form"))
        {
            findings.Add("No signed application form was recognised among the images sent.");
            missingIssue = true;
        }

        var note = notChecked.Count > 0 ? $" Not checked by AI: {string.Join(", ", notChecked)}." : "";

        if (observations.Count == 0)
            return new DocumentCheckPlan(InsightOutcomes.Uncertain, findings,
                "AI could not check any image on this case." + note + " Operations must verify the documents manually.",
                null, null, new());

        if (findings.Count == 0)
            return new DocumentCheckPlan(NoIssuesFound, findings,
                $"AI found no problems in {observations.Count} image(s): details match the form and a signature is present where expected." + note +
                " Operations must still compare the signature with the PAN / ID proof before marking the form in order.",
                null, null, new());

        string type = signatureIssue ? "Signature Mismatch" : dataIssue ? "Incomplete Details" : missingIssue ? "Document Missing" : "Other";
        var lines = new List<string> { "The signed form and documents received have the following issues:", "" };
        lines.AddRange(findings.Select((f, i) => $"{i + 1}. {f}"));
        lines.Add("");
        lines.Add("Please get these corrected and send the documents again.");
        var message = string.Join("\n", lines);
        if (message.Length > WorkflowEngine.MaxTextLength) message = message[..WorkflowEngine.MaxTextLength];

        var steps = new List<PlannedStep>();
        var status = latest.LatestStatus;
        if (status == S.SignedFormReceived)
        {
            steps.Add(new PlannedStep(ActionCodes.StartSignedFormReview, new WorkflowInput()));
            steps.Add(new PlannedStep(ActionCodes.RaiseDiscrepancy, new WorkflowInput { DiscrepancyType = type, Details = message }));
        }
        else if (status == S.UnderReviewSignedForm)
        {
            steps.Add(new PlannedStep(ActionCodes.RaiseDiscrepancy, new WorkflowInput { DiscrepancyType = type, Details = message }));
        }
        else if (status is S.DiscrepancyResolutionReceived or S.ResubmittedFormUnderReview)
        {
            var q = queries.Where(q => q.ToTeam == WorkspaceTypes.Cse && q.Status == QueryStatuses.Responded).OrderByDescending(q => q.Id).FirstOrDefault();
            if (q != null) steps.Add(new PlannedStep(ActionCodes.OpsFollowUp, new WorkflowInput { QueryId = q.Id, Details = message }));
        }

        return new DocumentCheckPlan(IssuesFound, findings,
            $"AI found {findings.Count} possible issue(s) in the documents sent back. Check them and raise the discrepancy if you agree." + note,
            type, message, steps);
    }
}
