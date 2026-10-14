namespace CivicOps.Models;

public enum SosStatus
{
    AwaitingLocation,
    Triggered,
    Acknowledged,
    Assessing,
    Dispatched,
    Resolved,
    FalseAlarm,
    Cancelled
}

public sealed record SosCreateRequest(
    string OperationId,
    string Channel,
    string? Note,
    string? Contact,
    string? AddressOrLandmark,
    bool Silent,
    GeoPoint? Location,
    double? AccuracyMeters,
    int? BatteryPercent);

public sealed record SosLocationRequest(string OperationId, GeoPoint? Location, double? AccuracyMeters, string? AddressOrLandmark,
    bool VisibleIndicator = false, string? ConsentId = null);
public sealed record SosActionRequest(string OperationId, string Note);
public sealed record SosInboundRequest(string Channel, string Sender, string Message, string? TenantId = null);
public sealed record SosUssdRequest(string SessionId, string Text, string? Sender = null, string? TenantId = null);
public sealed record SosAuditEntry(string Action, string Actor, string? Note, DateTimeOffset At);
public sealed record SosAlert(
    string Id, string TenantId, SosStatus Status, string Channel, string ContactHash,
    string? StaffName, string? CrewName, bool Silent, DateTimeOffset TriggeredAt,
    DateTimeOffset FollowUpUntil, DateTimeOffset CancelUntil, GeoPoint? Location, double? AccuracyMeters, int? BatteryPercent,
    string? AddressOrLandmark, DateTimeOffset? AcknowledgedAt, DateTimeOffset? ResolvedAt,
    string? EscalationLevel, string? LastHumanActor, IReadOnlyList<SosAuditEntry> Audit,
    bool RepeatedFalseAlarmReview, bool Synthetic);
public sealed record SosResult(SosAlert Alert, bool Replayed, bool LocationRequired, string Message, string EmergencyNotice);
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "SosQueue is the bounded municipal control-room queue contract, not a System.Collections queue implementation.")]
public sealed record SosQueue(IReadOnlyList<SosAlert> Alerts, DateTimeOffset GeneratedAt, int Unacknowledged, int EscalationTargetSeconds);
