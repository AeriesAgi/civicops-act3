using System.Text.Json.Serialization;

namespace CivicOps.Models;

public sealed record TrainingCompletionRequest(string WorkerId, string ModuleId, string WorkOrderTypeCode,
    IReadOnlyList<string> SkillsPractised, double Score, bool Passed, int DurationSeconds, int Attempt,
    string DeviceClientLabel, string EvidenceHash, DateTimeOffset CompletedAt);

public sealed record TrainingCompletion(string Id, TrainingCompletionRequest Record, string Client,
    DateTimeOffset RecordedAt, string IdempotencyKeyHash, string RequestHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantId = null);

public sealed record TrainingCertificationRequest(string WorkerId, string CertificationTypeCode,
    string CompletionId, bool SupervisorSignOff, string SignOffReference);

// The grant is a durable intent. Workforce projection uses Id as its idempotent operation receipt.
public sealed record TrainingCertificationGrant(string Id, TrainingCertificationRequest Record,
    WorkerCertification Certification, string Client, DateTimeOffset RecordedAt,
    string IdempotencyKeyHash, string RequestHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantId = null);

public sealed record TrainingAuditRecord(long Sequence, string Action, string RecordId, string WorkerId,
    string Actor, DateTimeOffset At, string StateHash, string PreviousHash, string Hash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantId = null);

public sealed record TrainingWriteResult<T>(T? Record, bool Created = false, string? Error = null,
    bool Conflict = false, bool NotFound = false);
