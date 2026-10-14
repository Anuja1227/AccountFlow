using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Workflow;
using S = AccountOpeningTracker.Api.Models.WorkflowStatuses;

namespace AccountOpeningTracker.Tests;

public class DocumentCheckTests
{
    private static readonly Dictionary<string, string> Form = new()
    {
        ["dateOfBirth"] = "1992-05-14",
        ["bankAccountNumber"] = "501002345678",
        ["ifsc"] = "HDFC0001234",
    };

    private static Account Case(string status = S.SignedFormReceived)
    {
        var a = Make.Record(1);
        a.ClientName = "Riya Desai";
        a.PanNo = "ABCPD7788Q";
        a.LatestStatus = status;
        return a;
    }

    private static DocumentObservation Obs(
        string type = "Signed application form", bool readable = true, string? quality = null,
        bool sigExpected = true, bool? signed = true, Dictionary<string, string?>? fields = null, List<string>? other = null) =>
        new("page1.png", type, readable, quality, sigExpected, signed, fields ?? new(), other ?? new(), 0.9);

    [Fact]
    public void Clean_signed_form_has_no_issues_and_no_suggested_action()
    {
        var obs = Obs(fields: new()
        {
            ["clientName"] = "DESAI RIYA",
            ["panNo"] = "ABCPD 7788 Q",
            ["dateOfBirth"] = "14/05/1992",
            ["bankAccountNumber"] = "5010 0234 5678",
            ["ifsc"] = "hdfc0001234",
        });
        var plan = DocumentCheckPolicy.Plan(new[] { obs }, Array.Empty<string>(), Case(), Form, new List<CaseQuery>());
        Assert.Equal(InsightOutcomes.NoIssuesFound, plan.Outcome);
        Assert.Empty(plan.Findings);
        Assert.Empty(plan.Steps);
        Assert.Contains("compare the signature", plan.Summary);
    }

    [Fact]
    public void Missing_signature_suggests_a_signature_discrepancy_but_does_not_act()
    {
        var plan = DocumentCheckPolicy.Plan(new[] { Obs(signed: false) }, Array.Empty<string>(), Case(), Form, new List<CaseQuery>());
        Assert.Equal(InsightOutcomes.IssuesFound, plan.Outcome);
        Assert.Equal("Signature Mismatch", plan.DiscrepancyType);
        Assert.Equal(new[] { ActionCodes.StartSignedFormReview, ActionCodes.RaiseDiscrepancy }, plan.Steps.Select(s => s.Action));
        Assert.Contains("no signature", plan.Message);
    }

    [Fact]
    public void Data_mismatches_are_found_by_code_not_by_the_ai()
    {
        var obs = Obs(fields: new()
        {
            ["clientName"] = "Priya Desai",
            ["panNo"] = "ABCPD7788R",
            ["dateOfBirth"] = "15-05-1992",
            ["ifsc"] = "HDFC0009999",
        });
        var mismatches = DocumentCheckPolicy.Mismatches(obs, Case(), Form);
        Assert.Equal(4, mismatches.Count);

        var plan = DocumentCheckPolicy.Plan(new[] { obs }, Array.Empty<string>(), Case(S.UnderReviewSignedForm), Form, new List<CaseQuery>());
        Assert.Equal("Incomplete Details", plan.DiscrepancyType);
        Assert.Equal(ActionCodes.RaiseDiscrepancy, Assert.Single(plan.Steps).Action);
    }

    [Fact]
    public void Unreadable_scan_and_no_form_page_are_flagged()
    {
        var obs = Obs(type: "PAN card", readable: false, quality: "blurred", sigExpected: false, signed: null);
        var plan = DocumentCheckPolicy.Plan(new[] { obs }, new[] { "proof.pdf (PDF)" }, Case(), Form, new List<CaseQuery>());
        Assert.Equal("Document Missing", plan.DiscrepancyType);
        Assert.Contains(plan.Findings, f => f.Contains("blurred"));
        Assert.Contains(plan.Findings, f => f.Contains("No signed application form"));
        Assert.Contains("proof.pdf", plan.Summary);
    }

    [Fact]
    public void Corrected_documents_with_issues_suggest_a_follow_up_on_the_open_query()
    {
        var q = new CaseQuery { Id = 7, ToTeam = WorkspaceTypes.Cse, Status = QueryStatuses.Responded };
        var plan = DocumentCheckPolicy.Plan(new[] { Obs(signed: false) }, Array.Empty<string>(), Case(S.DiscrepancyResolutionReceived), Form, new List<CaseQuery> { q });
        var step = Assert.Single(plan.Steps);
        Assert.Equal(ActionCodes.OpsFollowUp, step.Action);
        Assert.Equal(7, step.Input.QueryId);
    }

    [Fact]
    public void Free_text_comments_that_repeat_structured_checks_are_dropped()
    {
        var obs = Obs(signed: false, other: new() { "Signature box is empty", "PAN ABCPD7788R differs", "Page 2 of 3 — other pages not seen" });
        var plan = DocumentCheckPolicy.Plan(new[] { obs }, Array.Empty<string>(), Case(), Form, new List<CaseQuery>());
        Assert.Equal(2, plan.Findings.Count);
        Assert.Contains(plan.Findings, f => f.Contains("Page 2 of 3"));
    }

    [Fact]
    public void Nothing_checkable_means_a_person_must_check()
    {
        var plan = DocumentCheckPolicy.Plan(Array.Empty<DocumentObservation>(), new[] { "form.pdf (PDF)" }, Case(), Form, new List<CaseQuery>());
        Assert.Equal(InsightOutcomes.Uncertain, plan.Outcome);
        Assert.Empty(plan.Steps);
    }
}

public class SignaturePerDocumentTests
{
    private static Account Case()
    {
        var a = Make.Record(1);
        a.ClientName = "Riya Desai";
        a.PanNo = "ABCPD7788Q";
        a.LatestStatus = S.SignedFormReceived;
        return a;
    }

    private static DocumentObservation Page(string name, bool? signed, List<string>? other = null) =>
        new(name, "Signed application form", true, null, true, signed, new(), other ?? new(), 0.9);

    [Fact]
    public void Signature_on_the_last_page_is_enough_and_page_count_remarks_are_ignored()
    {
        var pages = new[] { Page("form.pdf page 1", false, new() { "Page 1 of 2 — page 2 not seen" }), Page("form.pdf page 2", true) };
        var plan = DocumentCheckPolicy.Plan(pages, Array.Empty<string>(), Case(), new Dictionary<string, string>(), new List<CaseQuery>());
        Assert.Equal(InsightOutcomes.NoIssuesFound, plan.Outcome);
    }

    [Fact]
    public void Missing_signature_is_not_flagged_when_a_page_was_not_read()
    {
        var pages = new[] { Page("form.pdf page 1", false) };
        var plan = DocumentCheckPolicy.Plan(pages, new[] { "form.pdf page 2 (AI busy)" }, Case(), new Dictionary<string, string>(), new List<CaseQuery>());
        Assert.DoesNotContain(plan.Findings, f => f.Contains("signature"));
        Assert.Contains("Signature not confirmed", plan.Summary);
    }

    [Fact]
    public void Unsigned_document_is_flagged_once()
    {
        var pages = new[] { Page("form.pdf page 1", false), Page("form.pdf page 2", false) };
        var plan = DocumentCheckPolicy.Plan(pages, Array.Empty<string>(), Case(), new Dictionary<string, string>(), new List<CaseQuery>());
        Assert.Single(plan.Findings, f => f.Contains("no signature"));
    }
}

public class LatestUploadTests
{
    [Fact]
    public void Only_the_corrected_upload_is_checked_not_the_first_signed_form()
    {
        var t = new DateTime(2026, 10, 14, 8, 40, 0, DateTimeKind.Utc);
        var docs = new List<AccountOpeningTracker.Api.Models.CaseDocument>
        {
            new() { Id = 1, FileName = "first_signed.pdf", Purpose = "Send signed form to Operations", UploadedAt = t },
            new() { Id = 2, FileName = "corrected_signed.pdf", Purpose = "Respond to query", QueryId = 7, UploadedAt = t.AddSeconds(50) },
            new() { Id = 3, FileName = "pan.jpg", Purpose = "Respond to query", QueryId = 7, UploadedAt = t.AddSeconds(50.2) },
        };
        var batch = AccountOpeningTracker.Api.Services.AiAutomationService.LatestUpload(docs);
        Assert.Equal(new[] { 2, 3 }, batch.Select(d => d.Id));
    }
}
