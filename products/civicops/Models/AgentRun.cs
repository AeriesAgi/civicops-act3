namespace CivicOps.Models;

/// <summary>A recorded, bounded AI decision. Inputs are represented by a SHA-256 hash, never credentials.</summary>
public sealed record AgentRun(
    string AgentName,
    string InputHash,
    string Provider,
    string Model,
    string EndpointLabel,
    double LatencyMs,
    string Output,
    double Confidence,
    string Rationale,
    bool FallbackUsed,
    string? Error,
    DateTimeOffset At,
    IReadOnlyList<string> ToolPermissions)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}

/// <summary>Drafts require a dispatcher decision; generating a draft never sends a message.</summary>
public sealed record CommsDraft(
    string Id,
    string Status,
    string Language,
    string Text,
    DateTimeOffset CreatedAt)
{
    public string State { get; set; } = "PendingReview";
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? AgentRunId { get; init; }
}
