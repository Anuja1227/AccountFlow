
using System.ComponentModel.DataAnnotations;

namespace AccountOpeningTracker.Api.Models;

public class UpdateAccountRequest
{
    // General information. ReferenceId, RequestId, and InwardDate
    // are intentionally excluded so an update cannot change case identity.
    public string EntryType { get; set; } = "New";

    [Required]
    public string ClientName { get; set; } = string.Empty;

    [Required]
    public string PanNo { get; set; } = string.Empty;

    public string AccountType { get; set; } = "Individual";
    public string Channel { get; set; } = "Physical";
    public string ActionBy { get; set; } = "Operations";

    public string Location { get; set; } = string.Empty;
    public string Segment { get; set; } = string.Empty;
    public string Cse { get; set; } = string.Empty;
    public string RelationshipManager { get; set; } = string.Empty;
    public string Processor { get; set; } = string.Empty;

    public DateTime? OutwardDate { get; set; }
    public DateTime? ResubmissionDate { get; set; }

    // Stage 1: Initial review
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

    // Stage 4: Discrepancy resolution
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

    // Operational tracking
    [Required]
    public string LatestStatus { get; set; } = "Request Received from CSE";

    public string? QueryDetails { get; set; }

    [Range(0, int.MaxValue)]
    public int? QueryCount { get; set; }

    [Range(0, int.MaxValue)]
    public int QueryHoldDays { get; set; }

    [Range(0, int.MaxValue)]
    public int TouchCount { get; set; }

    public string? Remarks { get; set; }
}
