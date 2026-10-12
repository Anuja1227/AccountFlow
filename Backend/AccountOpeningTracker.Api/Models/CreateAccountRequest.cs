
using System.ComponentModel.DataAnnotations;

namespace AccountOpeningTracker.Api.Models;

public class CreateAccountRequest
{
    [Required]
    public string ReferenceId { get; set; } = string.Empty;

    [Required]
    public string RequestId { get; set; } = string.Empty;

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

    [Required]
    public DateTime? InwardDate { get; set; }

    public DateTime? OutwardDate { get; set; }
    public DateTime? ResubmissionDate { get; set; }

    public DateTime? Stage1QueryRaisedDate { get; set; }
    public string? Stage1QueryDetails { get; set; }
    public DateTime? Stage1ResubmissionDate { get; set; }
    public string Stage1Status { get; set; } = "Request Received from CSE";

    public DateTime? FormPreparedDate { get; set; }
    public DateTime? PhysicalFormSubmittedToCseDate { get; set; }
    public DateTime? DigitalFormSentToClientDate { get; set; }
    public string Stage2Status { get; set; } = "Pending";

    public DateTime? SignedFormReceivedDate { get; set; }
    public DateTime? DiscrepancyRaisedDate { get; set; }
    public string? DiscrepancyType { get; set; }
    public DateTime? FormReturnedToCseDate { get; set; }
    public string Stage3Status { get; set; } = "Pending";

    public DateTime? DiscrepancyResolutionReceivedDate { get; set; }
    public DateTime? ResubmittedFormReceivedDate { get; set; }
    public string? Stage4ReviewOutcome { get; set; }
    public string Stage4Status { get; set; } = "Pending";

    public DateTime? SubmittedToMofslDate { get; set; }
    public DateTime? MofslQueryRaisedDate { get; set; }
    public string? MofslQueryType { get; set; }
    public DateTime? MofslQueryResolvedDate { get; set; }
    public string Stage5Status { get; set; } = "Pending";

    public string? AccountNumber { get; set; }
    public DateTime? AccountOpeningDate { get; set; }
    public string Stage6Status { get; set; } = "Pending";

    [Required]
    public string LatestStatus { get; set; } = "Request Received from CSE";

    public string? QueryDetails { get; set; }
    public int QueryCount { get; set; }
    public int QueryHoldDays { get; set; }
    public int TouchCount { get; set; }
    public string? Remarks { get; set; }

    /// <summary>The CSE request this entry is logged from (optional).</summary>
    public int? SourceRequestId { get; set; }
}
