using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Workflow;
using S = AccountOpeningTracker.Api.Models.WorkflowStatuses;

namespace AccountOpeningTracker.Tests;

/// <summary>
/// Drives the pure workflow engine the same way CaseService does, without a
/// database: persisted entities get ids, new ones are added to the context.
/// </summary>
internal sealed class Harness
{
    private int _nextAccountId = 100;
    private int _nextQueryId = 1;

    public List<Account> Records { get; } = new();
    public List<CaseQuery> Queries { get; } = new();
    public DateTime Today { get; set; } = Make.Monday.AddDays(4);

    public static readonly WorkflowActor Ops = new(1, "Olivia Ops", WorkspaceTypes.Operations);
    public static readonly WorkflowActor Cse = new(2, "Chetan CSE", WorkspaceTypes.Cse);
    public static readonly WorkflowActor Mofsl = new(3, "Meera MOFSL", WorkspaceTypes.Mofsl);

    public Harness(string channel = "Physical")
    {
        var a = Make.Record(_nextAccountId++);
        a.Channel = channel;
        Records.Add(a);
    }

    /// <summary>Simulates the saved application form's state (null = complete and ready to send).</summary>
    public string? FormNotReadyReason { get; set; }

    public WorkflowContext Ctx => new() { Records = Records, Queries = Queries, Today = Today, FormNotReadyReason = FormNotReadyReason };
    public Account Latest => Ctx.Latest;

    public WorkflowResult Run(string action, WorkflowActor actor, WorkflowInput? input = null)
    {
        var result = WorkflowEngine.Apply(Ctx, action, input ?? new WorkflowInput(), actor);
        if (result.NewRecord != null)
        {
            result.NewRecord.Id = _nextAccountId++;
            Records.Add(result.NewRecord);
        }
        if (result.NewQuery != null)
        {
            result.NewQuery.Id = _nextQueryId++;
            Queries.Add(result.NewQuery);
        }
        CaseCalculator.Recalculate(Records, Today, 7);
        return result;
    }

    public WorkflowResult Ok(string action, WorkflowActor actor, WorkflowInput? input = null)
    {
        var r = Run(action, actor, input);
        Assert.True(r.Success, $"{action} failed: {r.Error}");
        return r;
    }

    public CaseQuery LastQuery => Queries.Last();
}

public class WorkflowEngineTests
{
    private static WorkflowInput Text(string details, int? queryId = null) => new() { Details = details, QueryId = queryId };

    [Fact]
    public void Happy_path_physical_case_reaches_closed_and_stays_rft()
    {
        var h = new Harness();
        h.Ok(ActionCodes.StartReview, Harness.Ops);
        h.Ok(ActionCodes.StartFormPreparation, Harness.Ops);
        Assert.Equal("Stage 2", h.Latest.CurrentStage);

        h.Ok(ActionCodes.SendForm, Harness.Ops);
        Assert.Equal(S.PhysicalFormSubmitted, h.Latest.LatestStatus);
        Assert.NotNull(h.Latest.PhysicalFormSubmittedToCseDate);
        Assert.Null(h.Latest.DigitalFormSentToClientDate);

        h.Ok(ActionCodes.RecordSignedForm, Harness.Ops);
        h.Ok(ActionCodes.StartSignedFormReview, Harness.Ops);
        h.Ok(ActionCodes.MarkInOrder, Harness.Ops);
        Assert.Equal(S.ReadyForMofsl, h.Latest.LatestStatus);

        h.Ok(ActionCodes.SubmitToMofsl, Harness.Ops);
        Assert.Equal(WorkspaceTypes.Mofsl, h.Latest.ActionBy);

        // Operations cannot log the opening until MOFSL sends the account details.
        Assert.False(h.Run(ActionCodes.RecordAccountOpened, Harness.Ops, new WorkflowInput { AccountNumber = "MO123456" }).Success);
        // Step 1: MOFSL sees "Raise MOFSL query" and "No query — all clear", not "Account opened" yet.
        Assert.False(h.Run(ActionCodes.MofslSendAccountDetails, Harness.Mofsl, new WorkflowInput { AccountNumber = "MO123456" }).Success);
        Assert.False(h.Run(ActionCodes.MofslQueriesResolved, Harness.Mofsl).Success);
        h.Ok(ActionCodes.MofslNoQuery, Harness.Mofsl);
        Assert.Equal(S.SubmittedToMofsl, h.Latest.LatestStatus);
        Assert.False(h.Run(ActionCodes.MofslRaiseQuery, Harness.Mofsl, new WorkflowInput { MofslQueryType = "KYC", Details = "x" }).Success);
        Assert.False(h.Run(ActionCodes.MofslSendAccountDetails, Harness.Mofsl).Success); // account number required
        h.Ok(ActionCodes.MofslSendAccountDetails, Harness.Mofsl, new WorkflowInput { AccountNumber = "MO123456", Details = "Opened today" });
        Assert.Equal("MO123456", h.Latest.MofslAccountNumber);
        Assert.NotNull(h.Latest.MofslAccountOpeningDate);
        Assert.Null(h.Latest.AccountNumber); // not in the tracker until Operations logs it
        Assert.Equal(S.SubmittedToMofsl, h.Latest.LatestStatus);
        Assert.Equal(WorkspaceTypes.Operations, h.Latest.ActionBy);
        Assert.False(h.Run(ActionCodes.MofslRaiseQuery, Harness.Mofsl, new WorkflowInput { MofslQueryType = "KYC", Details = "x" }).Success);

        h.Ok(ActionCodes.RecordAccountOpened, Harness.Ops, new WorkflowInput { AccountNumber = "MO123456" });
        Assert.Equal("Completed", h.Latest.CurrentStage);
        Assert.Equal("MO123456", h.Latest.AccountNumber);

        h.Ok(ActionCodes.SendCommunication, Harness.Ops);
        Assert.Equal(S.CaseClosed, h.Latest.LatestStatus);
        Assert.Equal("RFT", h.Latest.RftStatus);
        Assert.Single(h.Records);

        // Closed cases accept no further actions.
        Assert.Empty(WorkflowEngine.GetAvailableActions(h.Ctx));
    }

    [Fact]
    public void Digital_channel_sends_the_form_to_the_client()
    {
        var h = new Harness("Digital");
        h.Ok(ActionCodes.StartReview, Harness.Ops);
        h.Ok(ActionCodes.StartFormPreparation, Harness.Ops);
        h.Ok(ActionCodes.SendForm, Harness.Ops);
        Assert.Equal(S.DigitalFormSent, h.Latest.LatestStatus);
        Assert.NotNull(h.Latest.DigitalFormSentToClientDate);
        Assert.Null(h.Latest.PhysicalFormSubmittedToCseDate);
    }

    [Fact]
    public void Stage1_query_cycle_records_dates_counts_hold_days_and_makes_case_nrft()
    {
        var h = new Harness { Today = Make.Monday.AddDays(4) };
        h.Ok(ActionCodes.StartReview, Harness.Ops);
        h.Ok(ActionCodes.RaiseStage1Query, Harness.Ops, new WorkflowInput { Details = "Address proof missing", ActionDate = Make.Monday });

        Assert.Equal(S.QueryRaisedMissingInfo, h.Latest.LatestStatus);
        Assert.Equal(WorkspaceTypes.Cse, h.Latest.ActionBy);
        Assert.Equal(1, h.Latest.QueryCount);
        Assert.Equal("NRFT", h.Latest.RftStatus);
        var q = h.LastQuery;
        Assert.Equal(WorkspaceTypes.Operations, q.FromTeam);
        Assert.Equal(WorkspaceTypes.Cse, q.ToTeam);

        // Ops cannot progress while the query is open.
        Assert.False(h.Run(ActionCodes.StartFormPreparation, Harness.Ops).Success);

        h.Ok(ActionCodes.CseRespond, Harness.Cse, new WorkflowInput { Details = "Uploaded utility bill", QueryId = q.Id, ActionDate = Make.Monday.AddDays(2) });
        Assert.Equal(S.ResubmittedByCse, h.Latest.LatestStatus);
        Assert.Equal(Make.Monday.AddDays(2), h.Latest.Stage1ResubmissionDate);
        Assert.Equal(2, q.HoldDays);
        Assert.Equal(2, h.Latest.QueryHoldDays);
        Assert.Equal(h.Latest.GrossTatDays - 2, h.Latest.NetTatDays);

        h.Ok(ActionCodes.OpsAcceptResponse, Harness.Ops, Text("OK", q.Id));
        Assert.Equal(QueryStatuses.Resolved, q.Status);
        Assert.Equal(S.UnderReviewByOperations, h.Latest.LatestStatus);
        h.Ok(ActionCodes.StartFormPreparation, Harness.Ops);
        Assert.Equal(3, q.Messages.Count);
    }

    [Fact]
    public void Follow_up_reopens_the_query_and_counts_another_query()
    {
        var h = new Harness();
        h.Ok(ActionCodes.StartReview, Harness.Ops);
        h.Ok(ActionCodes.RaiseStage1Query, Harness.Ops, Text("Need nominee details"));
        var q = h.LastQuery;
        h.Ok(ActionCodes.CseRespond, Harness.Cse, Text("Nominee is spouse", q.Id));
        h.Ok(ActionCodes.OpsFollowUp, Harness.Ops, Text("Also need nominee DOB", q.Id));

        Assert.Equal(QueryStatuses.Open, q.Status);
        Assert.Equal(1, q.FollowUpCount);
        Assert.Equal(2, h.Latest.QueryCount);
        Assert.Equal(S.QueryRaisedMissingInfo, h.Latest.LatestStatus);
    }

    [Fact]
    public void Stage3_discrepancy_response_creates_a_resubmission_record_with_same_reference()
    {
        var h = new Harness();
        foreach (var a in new[] { ActionCodes.StartReview, ActionCodes.StartFormPreparation, ActionCodes.SendForm, ActionCodes.RecordSignedForm })
            h.Ok(a, Harness.Ops);

        h.Ok(ActionCodes.RaiseDiscrepancy, Harness.Ops, new WorkflowInput { Details = "Signature mismatch on page 2", DiscrepancyType = "Signature Mismatch" });
        Assert.Equal(S.DiscrepancyRaised, h.Latest.LatestStatus);
        var q = h.LastQuery;

        h.Ok(ActionCodes.CseRespond, Harness.Cse, Text("Client re-signed", q.Id));
        Assert.Equal(2, h.Records.Count);
        var latest = h.Latest;
        Assert.Equal("Discrepancy Resolution", latest.EntryType);
        Assert.Equal("REF-1", latest.ReferenceId);
        Assert.Equal(S.DiscrepancyResolutionReceived, latest.LatestStatus);
        Assert.NotNull(latest.DiscrepancyResolutionReceivedDate);
        Assert.Equal(h.Records[0].InwardDate, latest.OriginalInwardDate); // TAT still from first receipt
        Assert.Equal("Stage 4", latest.CurrentStage);
        Assert.Equal("NRFT", latest.RftStatus);

        h.Ok(ActionCodes.OpsAcceptResponse, Harness.Ops, Text("", q.Id));
        Assert.Equal(S.ReadyForMofsl, h.Latest.LatestStatus);
        Assert.Equal("Found in Order", h.Latest.Stage4ReviewOutcome);
    }

    [Fact]
    public void Returned_form_resubmission_is_entry_type_resubmission()
    {
        var h = new Harness();
        foreach (var a in new[] { ActionCodes.StartReview, ActionCodes.StartFormPreparation, ActionCodes.SendForm, ActionCodes.RecordSignedForm })
            h.Ok(a, Harness.Ops);
        h.Ok(ActionCodes.ReturnForm, Harness.Ops, new WorkflowInput { Details = "Wrong form version", DiscrepancyType = "Other" });
        Assert.NotNull(h.Latest.FormReturnedToCseDate);
        h.Ok(ActionCodes.CseRespond, Harness.Cse, Text("Resubmitted on the new form", h.LastQuery.Id));
        Assert.Equal("Resubmission", h.Latest.EntryType);
        Assert.Equal(S.ResubmittedFormUnderReview, h.Latest.LatestStatus);
    }

    [Fact]
    public void Mofsl_query_flows_from_mofsl_to_operations_and_back()
    {
        var h = new Harness();
        foreach (var a in new[] { ActionCodes.StartReview, ActionCodes.StartFormPreparation, ActionCodes.SendForm, ActionCodes.RecordSignedForm, ActionCodes.MarkInOrder, ActionCodes.SubmitToMofsl })
            h.Ok(a, Harness.Ops);

        // MOFSL raises and reviews its queries; Operations records the account opening.
        Assert.Equal(WorkspaceTypes.Mofsl, WorkflowEngine.Find(ActionCodes.MofslRaiseQuery)!.Team);
        Assert.Equal(WorkspaceTypes.Operations, WorkflowEngine.Find(ActionCodes.RecordAccountOpened)!.Team);

        h.Ok(ActionCodes.MofslRaiseQuery, Harness.Mofsl, new WorkflowInput { MofslQueryType = "KYC mismatch", Details = "Name differs from PAN" });
        var q = h.LastQuery;
        Assert.Equal(WorkspaceTypes.Mofsl, q.FromTeam);
        Assert.Equal(WorkspaceTypes.Operations, q.ToTeam);
        Assert.Equal(S.QueryRaisedByMofsl, h.Latest.LatestStatus);

        // Cannot open the account while the MOFSL query is open.
        Assert.False(h.Run(ActionCodes.RecordAccountOpened, Harness.Ops, new WorkflowInput { AccountNumber = "MO1234" }).Success);

        h.Ok(ActionCodes.OpsRespondMofsl, Harness.Ops, Text("Corrected name per PAN", q.Id));
        Assert.Equal(S.MofslQueryResolved, h.Latest.LatestStatus);
        Assert.NotNull(h.Latest.MofslQueryResolvedDate);
        Assert.Equal(WorkspaceTypes.Mofsl, h.Latest.ActionBy);

        // Step 3: accept the response, then MOFSL finds something else and raises another query.
        Assert.False(h.Run(ActionCodes.MofslSendAccountDetails, Harness.Mofsl, new WorkflowInput { AccountNumber = "MO1234" }).Success);
        h.Ok(ActionCodes.MofslAcceptResponse, Harness.Mofsl, Text("OK", q.Id));
        Assert.Equal(QueryStatuses.Resolved, q.Status);
        Assert.False(h.Run(ActionCodes.MofslSendAccountDetails, Harness.Mofsl, new WorkflowInput { AccountNumber = "MO1234" }).Success);
        h.Ok(ActionCodes.MofslRaiseQuery, Harness.Mofsl, new WorkflowInput { MofslQueryType = "Address proof", Details = "Address proof is not legible" });
        var q2 = h.LastQuery;
        Assert.Equal(S.QueryRaisedByMofsl, h.Latest.LatestStatus);
        Assert.False(h.Run(ActionCodes.MofslQueriesResolved, Harness.Mofsl).Success); // waiting for Operations
        h.Ok(ActionCodes.OpsRespondMofsl, Harness.Ops, Text("Clear copy attached", q2.Id));

        // "Query resolved" (accepts the pending response) → only "Account opened" remains.
        Assert.False(h.Run(ActionCodes.MofslNoQuery, Harness.Mofsl).Success);
        h.Ok(ActionCodes.MofslQueriesResolved, Harness.Mofsl);
        Assert.Equal(QueryStatuses.Resolved, q2.Status);
        Assert.False(h.Run(ActionCodes.MofslRaiseQuery, Harness.Mofsl, new WorkflowInput { MofslQueryType = "KYC", Details = "x" }).Success);
        Assert.Equal(WorkspaceTypes.Mofsl, h.Latest.ActionBy); // MOFSL still owes the account details
        Assert.False(h.Run(ActionCodes.RecordAccountOpened, Harness.Ops, new WorkflowInput { AccountNumber = "MO1234" }).Success);

        h.Ok(ActionCodes.MofslSendAccountDetails, Harness.Mofsl, new WorkflowInput { AccountNumber = "MO1234" });
        Assert.Equal(WorkspaceTypes.Operations, h.Latest.ActionBy);

        h.Ok(ActionCodes.RecordAccountOpened, Harness.Ops, new WorkflowInput { AccountNumber = "MO1234" });
        Assert.Equal("NRFT", h.Latest.RftStatus);
    }

    [Fact]
    public void Invalid_transitions_are_rejected()
    {
        var h = new Harness();
        Assert.False(h.Run(ActionCodes.SubmitToMofsl, Harness.Ops).Success);
        Assert.False(h.Run(ActionCodes.RecordAccountOpened, Harness.Ops, new WorkflowInput { AccountNumber = "MO1234" }).Success);
        Assert.False(h.Run(ActionCodes.SendForm, Harness.Ops).Success);
        Assert.Equal(S.RequestReceived, h.Latest.LatestStatus);
    }

    [Fact]
    public void Required_fields_and_dates_are_validated()
    {
        var h = new Harness { Today = Make.Monday.AddDays(4) };
        h.Ok(ActionCodes.StartReview, Harness.Ops);
        Assert.Contains("required", h.Run(ActionCodes.RaiseStage1Query, Harness.Ops).Error);
        Assert.Contains("future", h.Run(ActionCodes.RaiseStage1Query, Harness.Ops,
            new WorkflowInput { Details = "x", ActionDate = Make.Monday.AddDays(10) }).Error);
        Assert.Contains("before", h.Run(ActionCodes.RaiseStage1Query, Harness.Ops,
            new WorkflowInput { Details = "x", ActionDate = Make.Monday.AddDays(-3) }).Error);
    }

    [Fact]
    public void On_hold_blocks_actions_and_resume_restores_previous_status()
    {
        var h = new Harness();
        h.Ok(ActionCodes.StartReview, Harness.Ops);
        h.Ok(ActionCodes.PutOnHold, Harness.Ops, new WorkflowInput { Reason = "Client travelling" });
        Assert.Equal(S.OnHold, h.Latest.LatestStatus);
        Assert.False(h.Run(ActionCodes.StartFormPreparation, Harness.Ops).Success);
        h.Ok(ActionCodes.Resume, Harness.Ops);
        Assert.Equal(S.UnderReviewByOperations, h.Latest.LatestStatus);
    }

    [Fact]
    public void Account_number_format_is_validated()
    {
        var h = new Harness();
        foreach (var a in new[] { ActionCodes.StartReview, ActionCodes.StartFormPreparation, ActionCodes.SendForm, ActionCodes.RecordSignedForm, ActionCodes.MarkInOrder, ActionCodes.SubmitToMofsl })
            h.Ok(a, Harness.Ops);
        h.Ok(ActionCodes.MofslNoQuery, Harness.Mofsl);
        Assert.False(h.Run(ActionCodes.MofslSendAccountDetails, Harness.Mofsl, new WorkflowInput { AccountNumber = "no spaces allowed" }).Success);
        h.Ok(ActionCodes.MofslSendAccountDetails, Harness.Mofsl, new WorkflowInput { AccountNumber = "MO1234" });
        Assert.False(h.Run(ActionCodes.RecordAccountOpened, Harness.Ops, new WorkflowInput { AccountNumber = "no spaces allowed" }).Success);
    }

    [Fact]
    public void Available_actions_carry_the_owning_team_so_permissions_can_filter_them()
    {
        var h = new Harness();
        h.Ok(ActionCodes.StartReview, Harness.Ops);
        h.Ok(ActionCodes.RaiseStage1Query, Harness.Ops, Text("Missing PAN copy"));
        var actions = WorkflowEngine.GetAvailableActions(h.Ctx);
        var respond = Assert.Single(actions, a => a.Definition.Code == ActionCodes.CseRespond);
        Assert.Equal(WorkspaceTypes.Cse, respond.Definition.Team);
        Assert.Equal(Permissions.WorkflowCse, respond.Definition.Permission);
        Assert.DoesNotContain(actions, a => a.Definition.Code == ActionCodes.StartFormPreparation);
    }
}
