namespace AccountOpeningTracker.Api.Models;

public static class CaseRequestStatuses
{
    /// <summary>Sent by the CSE, waiting for Operations to log it.</summary>
    public const string Submitted = "Submitted";

    /// <summary>Operations logged it in the tracker (a case now exists).</summary>
    public const string Logged = "Logged";
}

/// <summary>
/// A CSE's request to Operations (Stage 1: "CSE sends request to Operations").
/// It is not a case yet: Operations makes the entry in the tracker, which
/// creates the case and links this request (and its attachments) to it.
/// </summary>
public class CaseRequest
{
    public int Id { get; set; }

    public int CseUserId { get; set; }
    public string CseUserName { get; set; } = string.Empty;

    /// <summary>The CSE name used on case records (from the CSE user's profile).</summary>
    public string CseName { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;
    public string Status { get; set; } = CaseRequestStatuses.Submitted;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when Operations logs the case.</summary>
    public string? ReferenceId { get; set; }
    public int? LoggedByUserId { get; set; }
    public string? LoggedByName { get; set; }
    public DateTime? LoggedAt { get; set; }

    public List<RequestAttachment> Attachments { get; set; } = new();
}

/// <summary>A file the CSE attached to a request (stored in the database).</summary>
public class RequestAttachment
{
    public int Id { get; set; }
    public int CaseRequestId { get; set; }
    public CaseRequest? CaseRequest { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
