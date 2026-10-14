
namespace AccountOpeningTracker.Api.Models;

public class Account
{
    public int Id { get; set; }

    // General account and request information
    public string ReferenceId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string EntryType { get; set; } = "New";
    public string ClientName { get; set; } = string.Empty;
    public string PanNo { get; set; } = string.Empty;

    public string AccountType { get; set; } = "Individual";
    public string Channel { get; set; } = "Physical";
    public string ActionBy { get; set; } = "Operations";

    public string Location { get; set; } = string.Empty;
    public string Segment { get; set; } = string.Empty;
    public string Cse { get; set; } = string.Empty;
    public string RelationshipManager { get; set; } = string.Empty;
    public string Processor { get; set; } = string.Empty;


    public DateTime InwardDate { get; set; }

    // Original date across all records with the same Reference ID.
    // This must not reset when a case is resubmitted.
    public DateTime? OriginalInwardDate { get; set; }

    public DateTime? OutwardDate { get; set; }
    public DateTime? ResubmissionDate { get; set; }


    // Stage 1: Initial review by Operations
    public DateTime? Stage1QueryRaisedDate { get; set; }
    public string? Stage1QueryDetails { get; set; }
    public DateTime? Stage1ResubmissionDate { get; set; }
    public string Stage1Status { get; set; } = "Request Received from CSE";

    // Stage 2: Form preparation
    public DateTime? FormPreparedDate { get; set; }
    public DateTime? PhysicalFormSubmittedToCseDate { get; set; }
    public DateTime? DigitalFormSentToClientDate { get; set; }
    public string Stage2Status { get; set; } = "Pending";

    // Stage 3: Signed form and document review
    public DateTime? SignedFormReceivedDate { get; set; }
    public DateTime? DiscrepancyRaisedDate { get; set; }
    public string? DiscrepancyType { get; set; }
    public DateTime? FormReturnedToCseDate { get; set; }
    public string Stage3Status { get; set; } = "Pending";

    // Stage 4: Discrepancy resolution and review
    public DateTime? DiscrepancyResolutionReceivedDate { get; set; }
    public DateTime? ResubmittedFormReceivedDate { get; set; }
    public string? Stage4ReviewOutcome { get; set; }
    public string Stage4Status { get; set; } = "Pending";

    // Stage 5: Submission to MOFSL
    public DateTime? SubmittedToMofslDate { get; set; }
    public DateTime? MofslQueryRaisedDate { get; set; }
    public string? MofslQueryType { get; set; }
    public DateTime? MofslQueryResolvedDate { get; set; }
    public string Stage5Status { get; set; } = "Pending";

    // Stage 6: Account opening
    public string? AccountNumber { get; set; }
    public DateTime? AccountOpeningDate { get; set; }
    public string Stage6Status { get; set; } = "Pending";

    // "MOFSL team provide the details of accounts opened": what MOFSL sent with
    // "Send account details to Operations". Operations then logs them in the tracker
    // (AccountNumber / AccountOpeningDate above).
    public string? MofslAccountNumber { get; set; }
    public DateTime? MofslAccountOpeningDate { get; set; }
    public string? MofslAccountDetails { get; set; } // optional note from MOFSL

    // Date the account-opening communication was sent and the case closed
    // ("Communication Sent — Case Closed"); ends the Stage 6 TAT.
    public DateTime? CommunicationSentDate { get; set; }

    // Overall status and operational tracking
    public string LatestStatus { get; set; } = "Request Received from CSE";
    public string CurrentStage { get; set; } = "Stage 1";
    public bool IsCurrentStageManuallyOverridden { get; set; }
    public string RftStatus { get; set; } = "RFT";

    public int QueryCount { get; set; }
    public string? QueryDetails { get; set; }
    public int QueryHoldDays { get; set; }
    public int TouchCount { get; set; }
    public string? Remarks { get; set; }


    // Turnaround time and SLA tracking
    public int GrossTatDays { get; set; }
    public int NetTatDays { get; set; }
    public int CurrentAgingDays { get; set; }

    public bool IsSlaBreached { get; set; }

    // Stage 1–6 TAT and stage-wise SLA. Derived from the stage dates on every
    // read (StageTatHelper); not stored.
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<StageTat> StageTats { get; set; } = new();

    // Status to return to when a case is resumed after "On Hold".
    public string? StatusBeforeHold { get; set; }

    // Record-keeping timestamps (UTC). Nullable so existing rows stay valid.
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

