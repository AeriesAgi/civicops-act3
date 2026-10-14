using System.Text.Json.Serialization;

namespace CivicOps.Models;

/// <summary>Coordinates are volunteered report locations or explicit synthetic scenario points.</summary>
public sealed record GeoPoint(double Longitude, double Latitude);
public sealed record GeoPolygon(double[][][] Coordinates);
public sealed record GeoMunicipality(string Id, string Name, GeoPolygon Polygon, bool Synthetic, string TenantId);
public sealed record GeoWard(string Id, string Name, string MunicipalityId, GeoPolygon Polygon, bool Synthetic, string TenantId);
public sealed record GeoCrewPosition(string CrewId, GeoPoint Location, DateTimeOffset LastSeenAt, string Status, bool Synthetic, string TenantId);
public sealed record GeoHazard(string Id, string EventType, string Title, GeoPoint Location, double Confidence,
    int ExclusionRadiusMeters, DateTimeOffset OccurredAt, string? CameraId, bool Synthetic, string TenantId);
public sealed record GeoAsset(string Id, string Name, string AssetType, GeoPoint Location, bool Synthetic, string TenantId);
public sealed record GeoSnapshot(string TenantId, int Version, List<GeoMunicipality> Municipalities, List<GeoWard> Wards,
    List<GeoCrewPosition> CrewPositions, List<GeoHazard> Hazards, List<GeoAsset> Assets, List<GeoAuditRecord> Audit);
public sealed record GeoAuditRecord(long Sequence, string Action, string Actor, string Detail, DateTimeOffset At,
    string PreviousHash, string StateHash, string Hash, string TenantId);
public sealed record GeoGeometry(string Type, object Coordinates);
public sealed record GeoFeature(string Type, string Id, GeoGeometry Geometry, object Properties);
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "FeatureCollection is the RFC 7946 GeoJSON interchange contract name.")]
public sealed record GeoFeatureCollection(string Type, IReadOnlyList<GeoFeature> Features, int Total,
    int? NextPage, DateTimeOffset GeneratedAt, bool Synthetic);
public sealed record GeoGridPoint(GeoPoint Location, DateTimeOffset At);
public sealed record GeoHeatCell(GeoPoint Location, int Count, double Weight);
public sealed record GeoCrewCandidate(Crew Crew, GeoPoint Location, DateTimeOffset LastSeenAt,
    string EligibilityStatus, bool CanAssign, IReadOnlyList<string> Reasons, bool Synthetic);
public sealed record GeoCrewSuggestion(string CrewId, string Name, string Department, double DistanceKm,
    string Status, string Availability, IReadOnlyList<string> Qualifications, IReadOnlyList<string> QualificationReasons, bool Synthetic);
public sealed record MapDispatchRequest(string CrewId, string Note, int ExpectedVersion);
public sealed record MapWardOverrideRequest(string MunicipalityId, string WardId, string Note, int ExpectedVersion);
public sealed record MapHazardRequest(string Id, string EventType, string Title, GeoPoint Location, double Confidence,
    int ExclusionRadiusMeters, DateTimeOffset OccurredAt, string? CameraId, string Reason);
public sealed record MapGeographiesRequest(int ExpectedVersion, List<GeoMunicipality> Municipalities, List<GeoWard> Wards, string Reason);
