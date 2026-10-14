namespace CivicOps.Models;

public enum IntegrationDeliveryState { Pending, Delivered, DeadLetter }

public sealed class IntegrationOutboxItem
{
    public string? TenantId { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DeduplicationKey { get; set; } = string.Empty;
    public string IncidentId { get; set; } = string.Empty;
    public string CaseReference { get; set; } = string.Empty;
    public string Operation { get; set; } = "create";
    public string RequestJson { get; set; } = string.Empty;
    public IntegrationDeliveryState State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? LastError { get; set; }
    public long? TicketId { get; set; }
}

public sealed record IntegrationOutboxStatus(string Mode, int Pending, int Delivered, int DeadLetter);
public sealed record ZendeskDelivery(long TicketId, string? TicketUrl, bool Simulated, bool Replayed);

public sealed record ZendeskWebhookEvent(string EventId, string CaseReference, string? Status,
    string? Comment, string? Source, long? TicketId);
