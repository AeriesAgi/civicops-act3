using System.Text.Json.Serialization;

namespace CivicOps.Models;

public enum IncidentStatus
{
    New, Triaged, PendingApproval, Approved, Assigned, InProgress,
    WaitingForCitizen, Resolved, Closed
}

public sealed class Incident
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ReferenceNumber { get; set; } = NewReference();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int Version { get; set; } = 1;
    public string Source { get; set; } = "web";
    public string SourceLabel { get; set; } = "Resident form";
    public string Description { get; set; } = string.Empty;
    public string RawDescription { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
    public string NormalizedArea { get; set; } = string.Empty;
    public string LocationProvenance { get; set; } = "Resident supplied; unverified";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeoPoint? Location { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LocationShared { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MunicipalityId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WardId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Synthetic { get; set; }
    public string? ContactEmail { get; set; }
    public string Category { get; set; } = "General";
    public string Department { get; set; } = "Public Works";
    public string Priority { get; set; } = "Medium";
    public IncidentStatus Status { get; set; } = IncidentStatus.Triaged;
    public string PublicUpdate { get; set; } = "Report received for human review.";
    public bool NeedsHumanReview { get; set; } = true;
    public string? DuplicateOf { get; set; }
    public double? DuplicateConfidence { get; set; }
    public ApprovalRecord? Approval { get; set; }
    public string? AssignedCrewId { get; set; }
    public string? AssignedCrewName { get; set; }
    public string? IntakeKeyHash { get; set; }
    public string? IntakeFingerprint { get; set; }
    public string ResidentLanguage { get; set; } = "en";
    public string IntakeSummary { get; set; } = "";
    public List<string> Hazards { get; set; } = [];
    public string ContactPreference { get; set; } = "none";
    public bool ConsentToContact { get; set; }
    public bool CallbackConsent { get; set; }
    public string? ContactPhone { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConsentReceipt? PrivacyConsent { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PrivacySeparated { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PrivacyAnonymised { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContactRecordHash { get; set; }
    public string? ConversationId { get; set; }
    public string? TranscriptSummary { get; set; }
    public string? RawTranscript { get; set; }
    public string? CallMetadata { get; set; }
    public string? RecommendedCrewId { get; set; }
    public string? RecommendedCrewReason { get; set; }
    public long? ZendeskTicketId { get; set; }
    public string? ZendeskTicketUrl { get; set; }
    public string? LastZendeskEventId { get; set; }
    public string? ServiceCode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkOrderTypeCode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CrewJobData? CrewWork { get; set; }
    public List<AgentRun> AgentRuns { get; set; } = [];
    public List<CommsDraft> CommsDrafts { get; set; } = [];
    public List<AuditEvent> Audit { get; set; } = [];

    public static string NewReference(DateTimeOffset? now = null) =>
        $"CIV-{(now?.UtcDateTime ?? DateTime.UtcNow):yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
}

// One constructor also makes these records readable after a process restart.
public sealed record ApprovalRecord(string ApprovedBy, string? Note, DateTimeOffset At);
public sealed record AuditEvent(string Action, string Actor, string Detail, DateTimeOffset At);

public sealed record IncidentRequest(
    string Description, string? Area = null, string? ContactEmail = null,
    string? Source = null, string? SourceLabel = null, string? LocationProvenance = null,
    string? ResidentLanguage = null, string? ContactPreference = null, bool ConsentToContact = false,
    bool CallbackConsent = false, string? ContactPhone = null, string? ConversationId = null, string? ServiceCode = null,
    bool? PrivacyConsentAccepted = null, string? PrivacyConsentVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GeoPoint? Location = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ShareLocation = false);
public sealed record ApprovalRequest(string ApprovedBy, string? Note = null, int? ExpectedVersion = null);
public sealed record AssignmentRequest(string CrewId, string ApprovedBy, string? Note = null, int? ExpectedVersion = null);
public sealed record StatusRequest(string Status, string? PublicUpdate = null, string? ApprovedBy = null,
    string? ApprovalNote = null, int? ExpectedVersion = null);

public sealed record Open311Request(
    [property: JsonPropertyName("service_code")] string ServiceCode,
    string? Description = null,
    [property: JsonPropertyName("address_string")] string? AddressString = null,
    string? Email = null,
    [property: JsonPropertyName("location_provenance")] string? LocationProvenance = null,
    [property: JsonPropertyName("privacy_consent_accepted")] bool? PrivacyConsentAccepted = null,
    [property: JsonPropertyName("privacy_consent_version")] string? PrivacyConsentVersion = null);
