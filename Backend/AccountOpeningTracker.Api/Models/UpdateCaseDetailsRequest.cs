using System.ComponentModel.DataAnnotations;

namespace AccountOpeningTracker.Api.Models;

/// <summary>
/// General case information Operations may edit (cases.edit). Workflow
/// dates and statuses are deliberately excluded: they change only through
/// workflow actions, or through the audited override endpoint.
/// </summary>
public class UpdateCaseDetailsRequest
{
    [Required, StringLength(200)]
    public string ClientName { get; set; } = string.Empty;

    [Required, StringLength(10, MinimumLength = 10)]
    public string PanNo { get; set; } = string.Empty;

    public string AccountType { get; set; } = "Individual";
    public string Channel { get; set; } = "Physical";

    [StringLength(100)] public string Location { get; set; } = string.Empty;
    [StringLength(100)] public string Segment { get; set; } = string.Empty;
    [StringLength(150)] public string Cse { get; set; } = string.Empty;
    [StringLength(150)] public string RelationshipManager { get; set; } = string.Empty;
    [StringLength(150)] public string Processor { get; set; } = string.Empty;

    public DateTime? OutwardDate { get; set; }

    /// <summary>Cumulative, user-entered per the requirements; workflow actions also add to it.</summary>
    [Range(0, 3650)]
    public int QueryHoldDays { get; set; }

    [StringLength(2000)]
    public string? Remarks { get; set; }

    /// <summary>
    /// "Current Stage: derived from which stage-fields are populated; manually
    /// overridable". Null or empty = derive it automatically.
    /// </summary>
    [StringLength(20)]
    public string? CurrentStageOverride { get; set; }
}
