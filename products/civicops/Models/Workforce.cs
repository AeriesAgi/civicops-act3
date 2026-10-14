using System.Text.Json.Serialization;

namespace CivicOps.Models;

public sealed record Skill(string Code, string Name, string Discipline);
public sealed record SkillRequirement(string Code, int MinimumLevel);
public sealed record CertificationType(string Code, string Name, int ValidityMonths, string IssuingBasis);
public sealed record WorkOrderType(string Code, string ServiceCode, string Category, string Department,
    string Title, string Description, string HazardClass, IReadOnlyList<SkillRequirement> RequiredSkills,
    IReadOnlyList<string> RequiredCertifications, IReadOnlyList<string> Ppe,
    IReadOnlyList<string> ProcedureSteps, int SlaTargetMinutes);
public sealed record WorkerSkill(string Code, int Level);
public sealed record WorkerCertification(string Code, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
    string Issuer, string Source, string EvidenceRef);
public sealed record Worker(string Id, string DisplayName, string CrewId,
    List<WorkerSkill> Skills, List<WorkerCertification> Certifications,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantId = null);
public sealed record Crew(string Id, string Name, string Department, string Capability,
    IReadOnlyList<string>? MemberIds = null, string Shift = "day", string Availability = "available",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantId = null);
public sealed record CrewEligibility(Crew Crew, string Status, bool CanAssign,
    IReadOnlyList<string> Reasons, IReadOnlyList<Worker> Qualifications);
public sealed record WorkOrderOverrideRequest(string WorkOrderTypeCode, string? Note = null, int? ExpectedVersion = null);
