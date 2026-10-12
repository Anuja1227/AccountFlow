namespace AccountOpeningTracker.Api.Models;

public static class QueryTypes
{
    /// <summary>Stage 1: Operations → CSE, missing information.</summary>
    public const string MissingInformation = "MissingInformation";

    /// <summary>Stage 3: Operations → CSE, discrepancy on signed form/documents.</summary>
    public const string Discrepancy = "Discrepancy";

    /// <summary>Stage 3: Operations → CSE, form physically returned.</summary>
    public const string FormReturned = "FormReturned";

    /// <summary>Stage 5: MOFSL → Operations.</summary>
    public const string MofslQuery = "MofslQuery";

    public static readonly string[] All = { MissingInformation, Discrepancy, FormReturned, MofslQuery };

    public static string StageOf(string queryType) => queryType switch
    {
        MissingInformation => "Stage 1",
        Discrepancy or FormReturned => "Stage 3",
        MofslQuery => "Stage 5",
        _ => "Unknown"
    };

    public static string Label(string queryType) => queryType switch
    {
        MissingInformation => "Missing information",
        Discrepancy => "Discrepancy",
        FormReturned => "Form returned",
        MofslQuery => "MOFSL query",
        _ => queryType
    };
}

public static class QueryStatuses
{
    public const string Open = "Open";
    public const string Responded = "Responded";
    public const string Resolved = "Resolved";
}

public static class MessageTypes
{
    public const string Query = "Query";
    public const string Response = "Response";
    public const string FollowUp = "FollowUp";
    public const string Resolution = "Resolution";
}

/// <summary>
/// An inter-team query (Stage 1 query, Stage 3 discrepancy/return, or
/// MOFSL query). Messages preserve the full conversation.
/// </summary>
public class CaseQuery
{
    public int Id { get; set; }

    /// <summary>Record the query was raised against.</summary>
    public int AccountId { get; set; }
    public Account? Account { get; set; }

    public string ReferenceId { get; set; } = string.Empty;
    public string Stage { get; set; } = "Stage 1";
    public string QueryType { get; set; } = QueryTypes.MissingInformation;

    /// <summary>Discrepancy type or MOFSL query type.</summary>
    public string? Category { get; set; }

    public string FromTeam { get; set; } = WorkspaceTypes.Operations;
    public string ToTeam { get; set; } = WorkspaceTypes.Cse;

    public string Status { get; set; } = QueryStatuses.Open;

    public int RaisedByUserId { get; set; }
    public string RaisedByName { get; set; } = string.Empty;
    public DateTime RaisedAt { get; set; }

    /// <summary>When the query was last opened (raise or follow-up). Hold days count from here.</summary>
    public DateTime LastOpenedAt { get; set; }

    public DateTime? RespondedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int? ResolvedByUserId { get; set; }

    /// <summary>Cumulative working days the case was on hold waiting for responses.</summary>
    public int HoldDays { get; set; }

    public int FollowUpCount { get; set; }

    public List<CaseQueryMessage> Messages { get; set; } = new();
}

public class CaseQueryMessage
{
    public int Id { get; set; }
    public int CaseQueryId { get; set; }
    public CaseQuery? CaseQuery { get; set; }

    public int AuthorUserId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorTeam { get; set; } = string.Empty;

    public string MessageType { get; set; } = MessageTypes.Query;
    public string Body { get; set; } = string.Empty;
    public bool AiAssisted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Who did what, when. Details never contain client identifiers.</summary>
public class AuditEvent
{
    public long Id { get; set; }
    public int? AccountId { get; set; }
    public string? ReferenceId { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? Team { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Metadata about AI calls. Prompts and outputs are not stored.</summary>
public class AiInteractionLog
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string Feature { get; set; } = string.Empty;
    public string? ReferenceId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int DurationMs { get; set; }
    public bool? Accepted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
