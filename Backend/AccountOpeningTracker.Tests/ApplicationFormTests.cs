using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Workflow;
using S = AccountOpeningTracker.Api.Models.WorkflowStatuses;

namespace AccountOpeningTracker.Tests;

public class ApplicationFormTests
{
    private static readonly DateTime Today = new(2026, 10, 9);

    private static Dictionary<string, string> CompleteAdult() => new()
    {
        ["dateOfBirth"] = "1990-05-14",
        ["gender"] = "Female",
        ["fatherOrSpouseName"] = "Suresh Rao",
        ["occupation"] = "Salaried",
        ["annualIncome"] = "5-10 lakh",
        ["mobile"] = "9876543210",
        ["email"] = "kavya@example.com",
        ["addressLine1"] = "12 Lake Road",
        ["city"] = "Pune",
        ["state"] = "Maharashtra",
        ["pinCode"] = "411001",
        ["bankName"] = "HDFC Bank",
        ["bankAccountNumber"] = "123456789012",
        ["ifsc"] = "HDFC0001234",
        ["bankAccountType"] = "Savings",
        ["segments"] = "Equity, F&O",
    };

    private static ApplicationForm Form(Dictionary<string, string> data) =>
        new() { DataJson = System.Text.Json.JsonSerializer.Serialize(data) };

    [Fact]
    public void Complete_adult_form_is_ready_to_send()
    {
        Assert.Null(ApplicationFormRules.NotReadyReason(Form(CompleteAdult()), "Individual", Today));
    }

    [Fact]
    public void Form_must_be_saved_but_no_field_is_required()
    {
        Assert.Equal("Prepare and save the application form first.", ApplicationFormRules.NotReadyReason(null, "Individual", Today));
        Assert.Null(ApplicationFormRules.NotReadyReason(Form(new Dictionary<string, string>()), "Individual", Today));
        Assert.Null(ApplicationFormRules.NotReadyReason(Form(new Dictionary<string, string>()), "Minor", Today));
        Assert.All(ApplicationFormRules.Fields, f => Assert.False(f.Required));
    }

    [Fact]
    public void Values_that_are_entered_must_be_valid_before_sending()
    {
        var data = new Dictionary<string, string> { ["ifsc"] = "HDFC1234" };
        Assert.Contains("IFSC", ApplicationFormRules.NotReadyReason(Form(data), "Individual", Today));
    }

    [Fact]
    public void Formats_are_validated()
    {
        var data = CompleteAdult();
        data["ifsc"] = "HDFC1234";
        data["mobile"] = "12345";
        data["pinCode"] = "41100";
        data["gender"] = "Unknown";
        var errors = ApplicationFormRules.FormatErrors(data, Today);
        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public void Normalize_drops_unknown_keys_and_tidies_values()
    {
        var data = ApplicationFormRules.Normalize(new Dictionary<string, string?>
        {
            ["ifsc"] = " hdfc0001234 ",
            ["email"] = "Kavya@Example.COM",
            ["mobile"] = "98765 43210",
            ["clientName"] = "should be ignored",
            ["city"] = "  ",
        });
        Assert.Equal("HDFC0001234", data["ifsc"]);
        Assert.Equal("kavya@example.com", data["email"]);
        Assert.Equal("9876543210", data["mobile"]);
        Assert.False(data.ContainsKey("clientName"));
        Assert.False(data.ContainsKey("city"));
    }

    [Fact]
    public void Send_form_waits_for_a_complete_application_form()
    {
        var h = new Harness { FormNotReadyReason = "Prepare and save the application form first." };
        h.Ok(ActionCodes.StartReview, Harness.Ops);
        h.Ok(ActionCodes.StartFormPreparation, Harness.Ops);

        var blocked = h.Run(ActionCodes.SendForm, Harness.Ops);
        Assert.False(blocked.Success);
        Assert.Contains("save the application form", blocked.Error);
        Assert.DoesNotContain(WorkflowEngine.GetAvailableActions(h.Ctx), a => a.Definition.Code == ActionCodes.SendForm);

        h.FormNotReadyReason = null;
        h.Ok(ActionCodes.SendForm, Harness.Ops);
        Assert.Equal(S.PhysicalFormSubmitted, h.Latest.LatestStatus);
    }

    [Fact]
    public void Cse_sends_the_signed_form_back_with_documents()
    {
        var h = new Harness();
        h.Ok(ActionCodes.StartReview, Harness.Ops);
        h.Ok(ActionCodes.StartFormPreparation, Harness.Ops);
        h.Ok(ActionCodes.SendForm, Harness.Ops);

        // Documents are required.
        var noFiles = h.Run(ActionCodes.CseSendSignedForm, Harness.Cse);
        Assert.False(noFiles.Success);

        var r = h.Ok(ActionCodes.CseSendSignedForm, Harness.Cse, new WorkflowInput { FileCount = 2, Details = "Signed in branch." });
        Assert.Equal(S.SignedFormReceived, h.Latest.LatestStatus);
        Assert.NotNull(h.Latest.SignedFormReceivedDate);
        Assert.Equal(WorkspaceTypes.Operations, h.Latest.ActionBy);
        Assert.Contains("2 document(s)", r.AuditDetails);

        // Operations then verifies it.
        h.Ok(ActionCodes.StartSignedFormReview, Harness.Ops);
        h.Ok(ActionCodes.MarkInOrder, Harness.Ops);
        Assert.Equal(S.ReadyForMofsl, h.Latest.LatestStatus);
    }

    [Fact]
    public void Only_cse_has_the_send_signed_form_action()
    {
        var def = WorkflowEngine.Find(ActionCodes.CseSendSignedForm)!;
        Assert.Equal(WorkspaceTypes.Cse, def.Team);
        Assert.Contains(def.Fields, f => f.Type == "files" && f.Required);
    }
}
