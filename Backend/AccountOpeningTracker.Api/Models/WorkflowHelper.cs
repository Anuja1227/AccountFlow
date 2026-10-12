
namespace AccountOpeningTracker.Api.Models;

public static class WorkflowHelper
{
    public static string DetermineCurrentStage(Account account)
    {
        // Respect a valid manual override.
        if (account.IsCurrentStageManuallyOverridden &&
            (account.CurrentStage == "Stage 1" ||
             account.CurrentStage == "Stage 2" ||
             account.CurrentStage == "Stage 3" ||
             account.CurrentStage == "Stage 4" ||
             account.CurrentStage == "Stage 5" ||
             account.CurrentStage == "Stage 6" ||
             account.CurrentStage == "Completed"))
        {
            return account.CurrentStage;
        }

        // Completed account
        if (account.LatestStatus == "Account Opened" ||
            account.Stage6Status == "Account Opened" ||
            account.AccountOpeningDate.HasValue ||
            !string.IsNullOrWhiteSpace(account.AccountNumber))
        {
            return "Completed";
        }

        // Stage 6: Account opening
        if (account.Stage6Status != "Pending")
        {
            return "Stage 6";
        }

        // Stage 5: Submission to MOFSL
        if (account.SubmittedToMofslDate.HasValue ||
            account.MofslQueryRaisedDate.HasValue ||
            account.MofslQueryResolvedDate.HasValue ||
            account.Stage5Status != "Pending")
        {
            return "Stage 5";
        }

        // Stage 4: Discrepancy resolution
        if (account.DiscrepancyResolutionReceivedDate.HasValue ||
            account.ResubmittedFormReceivedDate.HasValue ||
            account.Stage4Status != "Pending")
        {
            return "Stage 4";
        }

        // Stage 3: Signed form and document review
        if (account.SignedFormReceivedDate.HasValue ||
            account.DiscrepancyRaisedDate.HasValue ||
            account.FormReturnedToCseDate.HasValue ||
            account.Stage3Status != "Pending")
        {
            return "Stage 3";
        }

        // Stage 2: Form preparation
        if (account.FormPreparedDate.HasValue ||
            account.PhysicalFormSubmittedToCseDate.HasValue ||
            account.DigitalFormSentToClientDate.HasValue ||
            account.Stage2Status != "Pending")
        {
            return "Stage 2";
        }

        return "Stage 1";
    }
}
