namespace CivicOps.Models;

public sealed record CrewPolicy(int IntervalSeconds = 60, int AccuracyMeters = 100, int RetentionDays = 30, int MaxShiftHours = 12);
public sealed record CrewPolicyRequest(int IntervalSeconds, int AccuracyMeters, int RetentionDays, int MaxShiftHours, string Reason);
public sealed record CrewOperationRequest(string OperationId);
public sealed record CrewShiftEndRequest(string OperationId, string ShiftId);
public sealed record CrewConsentRequest(string OperationId, string NoticeVersion, bool Accepted);
public sealed record CrewShift(string Id, string WorkerId, string CrewId, DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? EndedAt, string OwnerId);
public sealed record CrewConsent(string Id, string WorkerId, string NoticeVersion, string NoticeText,
    DateTimeOffset GrantedAt, DateTimeOffset? WithdrawnAt);
public sealed record CrewLocationInput(double Longitude, double Latitude, double AccuracyMeters, DateTimeOffset CapturedAt);
public sealed record CrewLocationRequest(string OperationId, string ShiftId, string ConsentId, bool VisibleIndicator, List<CrewLocationInput> Points);
public sealed record CrewLocationPoint(string Id, string ShiftId, double Longitude, double Latitude, double AccuracyMeters, DateTimeOffset CapturedAt);
public sealed record CrewLocationHistory(IReadOnlyList<CrewLocationPoint> Points, int RetentionDays, DateTimeOffset GeneratedAt, int Total, int? NextPage);
public sealed record CrewLocationResult(int Accepted, bool Replayed);
public sealed record CrewPairingRequest(string WorkerId, string DeviceName, string Reason);
public sealed record CrewPairingExchangeRequest(string PairingId, string Code, string TenantId);
public sealed record CrewPairingIssued(string PairingId, string Code, string TenantId, DateTimeOffset ExpiresAt);
public sealed record CrewDeviceView(string Id, string WorkerId, string CrewId, string DeviceName, DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
public sealed record CrewTokenIssued(string Token, string DeviceId, string TenantId, string WorkerId, string CrewId, DateTimeOffset ExpiresAt);
public sealed record CrewRevokeRequest(string Reason);
public sealed record CrewJobStatusRequest(string OperationId, string Status, string Reason, int ExpectedVersion);
public sealed record CrewJobNoteRequest(string OperationId, string Text, int ExpectedVersion);
public sealed record CrewJobPhotoRequest(string OperationId, string ContentType, string DataBase64, bool LocationOptIn, int ExpectedVersion);
public sealed record CrewJobNote(string Id, string WorkerId, string Text, DateTimeOffset CreatedAt);
public sealed record CrewJobPhoto(string Id, string WorkerId, string ContentType, int SizeBytes, bool LocationOptIn, bool MetadataStripped, DateTimeOffset CreatedAt);
public sealed record CrewJobReceipt(string RequestHash, DateTimeOffset At);
public sealed class CrewJobData
{
    public string Status { get; set; } = "assigned";
    public List<CrewJobNote> Notes { get; set; } = [];
    public List<CrewJobPhoto> Photos { get; set; } = [];
    public Dictionary<string, CrewJobReceipt> Receipts { get; set; } = new(StringComparer.Ordinal);
}
public sealed record CrewJob(string Id, string ReferenceNumber, string Title, string Department, string Priority, string Status,
    string CrewStatus, int Version, GeoPoint? Location, string HazardClass, IReadOnlyList<string> Hazards, IReadOnlyList<string> Ppe,
    IReadOnlyList<string> RequiredCertifications, IReadOnlyList<string> Steps, IReadOnlyList<CrewJobNote> Notes,
    IReadOnlyList<CrewJobPhoto> Photos, bool Synthetic);
public sealed record CrewJobs(IReadOnlyList<CrewJob> Jobs, DateTimeOffset GeneratedAt);
public sealed record CrewJobResult(CrewJob Job, bool Replayed);
public sealed record CrewMe(string WorkerId, string CrewId, string TenantId, string? DeviceId, CrewShift? Shift,
    CrewConsent? Consent, CrewPolicy Policy, string NoticeVersion, string PrivacyNotice, bool DemoMode);
public sealed record CrewSosRequest(string OperationId, string Note, GeoPoint? Location, bool ShareLocation);
public sealed record CrewSosResult(string Id, string Status, string Message, bool Simulated, bool HumanDispatchRequired);
public sealed record CrewAuthConfiguration(bool Enabled, string Authority, string NativeClientId);
public sealed record CrewOidcChallenge(string ChallengeId, string Nonce, DateTimeOffset ExpiresAt);
public sealed record CrewOidcRequest(string ChallengeId, string IdToken);
