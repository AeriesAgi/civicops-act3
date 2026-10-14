namespace CivicOps.Models;

public enum AgencyIncidentStatus { Open, Active, Contained, Closed }
public enum MutualAidStatus { Requested, Accepted, Declined }
public enum MutualAidScope { Summary, SummaryAndLocation }
public enum DetectionStatus { Proposed, Confirmed, Dismissed }

public sealed record AgencyDefinition(string Id, string Name, string Group, string Colour, string Icon, bool Restricted, IReadOnlyList<string> RoleNames);
public sealed record AgencyView(string Id, string Name, string Group, string Colour, string Icon, bool Restricted, bool Enabled, IReadOnlyList<string> RoleNames);
public sealed record AgencyRoleMapping(string Role, string Agency, string BaseRole);
public sealed record AgencyIncidentType(string Code, string Agency, string Title, string DefaultPriority, int SlaMinutes,
    IReadOnlyList<string> Hazards, IReadOnlyList<string> Ppe, IReadOnlyList<string> Skills, IReadOnlyList<string> Certifications);
public sealed record AgencyCatalogue(int Version, bool Synthetic, IReadOnlyList<AgencyDefinition> Agencies, IReadOnlyList<AgencyRoleMapping> RoleMappings,
    IReadOnlyList<AgencyIncidentType> IncidentTypes, IReadOnlyList<string> IcsRoles);
public sealed record AgencyToggleRequest(string OperationId, bool Enabled, string Note);

public sealed record AgencyIncidentCreateRequest(string OperationId, string Title, string TypeCode, IReadOnlyList<string> Agencies,
    string Summary, GeoPoint? Location, string? AddressOrLandmark, IReadOnlyList<string>? LinkedCaseIds = null);
public sealed record AgencyCommanderRequest(string OperationId, string Commander, string IcsRole, string Note);
public sealed record AgencyStatusRequest(string OperationId, AgencyIncidentStatus Status, string Note);
public sealed record MutualAidCreateRequest(string OperationId, string FromAgency, string ToAgency, string? ToTenantId, string Resources, MutualAidScope Scope, string Note);
public sealed record MutualAidDecisionRequest(string OperationId, bool Accept, string Note);

public sealed record AgencyTimelineEntry(string Action, string Actor, string? Note, DateTimeOffset At);
public sealed record MutualAidRequestRecord(string Id, string FromAgency, string ToAgency, string? ToTenantId, string Resources, MutualAidScope Scope,
    MutualAidStatus Status, string RequestedBy, DateTimeOffset RequestedAt, string? DecidedBy, DateTimeOffset? DecidedAt, string? DecisionNote);
public sealed record AgencyIncident(string Id, string TenantId, string Title, string TypeCode, IReadOnlyList<string> Agencies, string Summary,
    GeoPoint? Location, string? AddressOrLandmark, IReadOnlyList<string> LinkedCaseIds, AgencyIncidentStatus Status, string Priority,
    string? Commander, string? CommanderIcsRole, string? DetectionId, IReadOnlyList<MutualAidRequestRecord> MutualAid,
    IReadOnlyList<AgencyTimelineEntry> Timeline, bool Restricted, bool Redacted, DateTimeOffset CreatedAt, bool Synthetic);
public sealed record AgencyBoard(IReadOnlyList<AgencyIncident> Incidents, int Open, int Active, int PendingMutualAid, string Notice);

public sealed record DetectionEventRequest(string OperationId, string EventType, double Confidence, string CameraId, GeoPoint Location, DateTimeOffset ObservedAt);
public sealed record DetectionSimulateRequest(string OperationId, int Seed, int Count);
public sealed record DetectionDecisionRequest(string OperationId, string Note, IReadOnlyList<string>? Agencies = null);
public sealed record DetectionEvent(string Id, string TenantId, string EventType, double Confidence, string CameraId, GeoPoint Location,
    DateTimeOffset ObservedAt, DetectionStatus Status, bool LowConfidence, IReadOnlyList<string> SuggestedAgencies,
    string? IncidentId, string? DecidedBy, string? DecisionNote, bool Simulated);
public sealed record DetectionFeed(IReadOnlyList<DetectionEvent> Events, IReadOnlyList<string> AllowedEventTypes, string Notice);
