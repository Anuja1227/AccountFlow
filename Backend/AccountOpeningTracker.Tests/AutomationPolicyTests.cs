using AccountOpeningTracker.Api.Models;
using AccountOpeningTracker.Api.Workflow;
using S = AccountOpeningTracker.Api.Models.WorkflowStatuses;

namespace AccountOpeningTracker.Tests;

public class AutomationPolicyTests
{
    private static Account NewCase(string status = S.RequestReceived)
    {
        var a = Make.Record(1);
        a.AccountType = "Individual";
        a.RelationshipManager = "RM One";
        a.LatestStatus = status;
        return a;
    }

    private static IntakeAssessment Ai(bool complete, double confidence, params string[] missing) =>
        new(complete, confidence, missing.ToList(), complete ? null : "Please share the items below.", "test");

    [Fact]
    public void Required_field_rules_flag_blank_fields_and_bad_pan_without_ai()
    {
        var a = NewCase();
        a.Location = "";
        a.PanNo = "BAD";
        var missing = AutomationPolicy.MissingRequiredFields(a);
        Assert.Contains("Location", missing);
        Assert.Contains(missing, m => m.Contains("valid PAN"));
    }

    [Fact]
    public void Complete_and_confident_moves_to_form_preparation()
    {
        var plan = AutomationPolicy.PlanIntake(Ai(true, 0.9), NewCase(), new List<string>());
        Assert.Equal(InsightOutcomes.Complete, plan.Outcome);
        Assert.Equal(new[] { ActionCodes.StartReview, ActionCodes.StartFormPreparation }, plan.Steps.Select(s => s.Action));
    }

    [Fact]
    public void Missing_items_raise_a_stage1_query_listing_them()
    {
        var plan = AutomationPolicy.PlanIntake(Ai(false, 0.8, "Guardian details", "Minor's birth certificate"), NewCase(), new List<string>());
        Assert.Equal(InsightOutcomes.Incomplete, plan.Outcome);
        var query = plan.Steps.Single(s => s.Action == ActionCodes.RaiseStage1Query);
        Assert.Contains("1. Guardian details", query.Input.Details);
        Assert.Contains("2. Minor's birth certificate", query.Input.Details);
    }

    [Fact]
    public void Required_field_gaps_raise_a_query_even_if_ai_says_complete()
    {
        var plan = AutomationPolicy.PlanIntake(Ai(true, 0.99), NewCase(), new List<string> { "RM name" });
        Assert.Equal(InsightOutcomes.Incomplete, plan.Outcome);
        Assert.Contains(plan.Steps, s => s.Action == ActionCodes.RaiseStage1Query && s.Input.Details!.Contains("RM name"));
    }

    [Theory]
    [InlineData(true, 0.5)]   // complete but not confident enough to move on
    [InlineData(false, 0.3)]  // incomplete but not confident enough to query
    public void Low_confidence_leaves_the_case_for_a_person(bool complete, double confidence)
    {
        var plan = AutomationPolicy.PlanIntake(Ai(complete, confidence, complete ? Array.Empty<string>() : new[] { "x" }), NewCase(), new List<string>());
        Assert.Equal(InsightOutcomes.Uncertain, plan.Outcome);
        Assert.DoesNotContain(plan.Steps, s => s.Action is ActionCodes.RaiseStage1Query or ActionCodes.StartFormPreparation);
    }

    [Fact]
    public void Intake_does_nothing_once_the_case_has_moved_on()
    {
        var plan = AutomationPolicy.PlanIntake(Ai(false, 0.9, "x"), NewCase(S.SubmittedToMofsl), new List<string>());
        Assert.Empty(plan.Steps);
    }

    private static CaseQuery Responded(string type = QueryTypes.MissingInformation, int id = 7) => new()
    {
        Id = id, QueryType = type, ToTeam = WorkspaceTypes.Cse, FromTeam = WorkspaceTypes.Operations, Status = QueryStatuses.Responded
    };

    private static ResponseAssessment Review(bool resolved, double confidence, params string[] outstanding) =>
        new(resolved, confidence, outstanding.ToList(), resolved ? null : "Thanks, a few items remain.", "test");

    [Fact]
    public void Resolved_stage1_reply_is_accepted_and_case_moves_to_form_preparation()
    {
        var q = Responded();
        var plan = AutomationPolicy.PlanResponseReview(Review(true, 0.9), q, NewCase(S.ResubmittedByCse), new[] { q });
        Assert.Equal(InsightOutcomes.Resolved, plan.Outcome);
        Assert.Equal(new[] { ActionCodes.OpsAcceptResponse, ActionCodes.StartFormPreparation }, plan.Steps.Select(s => s.Action));
        Assert.Equal(q.Id, plan.Steps[0].Input.QueryId);
    }

    [Fact]
    public void Incomplete_reply_gets_an_automatic_follow_up()
    {
        var q = Responded();
        var plan = AutomationPolicy.PlanResponseReview(Review(false, 0.8, "Nominee date of birth"), q, NewCase(S.ResubmittedByCse), new[] { q });
        Assert.Equal(InsightOutcomes.NotResolved, plan.Outcome);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(ActionCodes.OpsFollowUp, step.Action);
        Assert.Contains("Nominee date of birth", step.Input.Details);
    }

    [Fact]
    public void Stage3_reply_is_never_auto_accepted_because_the_form_must_be_checked_by_a_person()
    {
        var q = Responded(QueryTypes.Discrepancy);
        var plan = AutomationPolicy.PlanResponseReview(Review(true, 0.99), q, NewCase(S.DiscrepancyResolutionReceived), new[] { q });
        Assert.Equal(InsightOutcomes.Resolved, plan.Outcome);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Stage3_reply_that_ignores_the_issue_gets_a_follow_up()
    {
        var q = Responded(QueryTypes.FormReturned);
        var plan = AutomationPolicy.PlanResponseReview(Review(false, 0.9, "Corrected nominee section"), q, NewCase(S.ResubmittedFormUnderReview), new[] { q });
        Assert.Equal(ActionCodes.OpsFollowUp, Assert.Single(plan.Steps).Action);
    }

    [Fact]
    public void Uncertain_review_takes_no_action()
    {
        var q = Responded();
        var plan = AutomationPolicy.PlanResponseReview(Review(true, 0.5), q, NewCase(S.ResubmittedByCse), new[] { q });
        Assert.Equal(InsightOutcomes.Uncertain, plan.Outcome);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void Planned_steps_are_valid_for_the_workflow_engine()
    {
        // Drive the intake plan through the real engine: start review then raise the query.
        var h = new Harness();
        var plan = AutomationPolicy.PlanIntake(Ai(false, 0.9, "Address proof"), h.Latest, new List<string>());
        foreach (var step in plan.Steps) h.Ok(step.Action, Harness.Ops, step.Input);
        Assert.Equal(S.QueryRaisedMissingInfo, h.Latest.LatestStatus);

        // CSE answers; the review plan accepts and moves on.
        h.Ok(ActionCodes.CseRespond, Harness.Cse, new WorkflowInput { QueryId = h.LastQuery.Id, Details = "Utility bill attached" });
        var review = AutomationPolicy.PlanResponseReview(Review(true, 0.9), h.LastQuery, h.Latest, h.Queries);
        foreach (var step in review.Steps) h.Ok(step.Action, Harness.Ops, step.Input);
        Assert.Equal(S.FormUnderPreparation, h.Latest.LatestStatus);
    }
}
