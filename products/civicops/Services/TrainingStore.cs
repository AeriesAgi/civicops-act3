using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed class TrainingStore : IDisposable
{
    public void Dispose() => _gate.Dispose();
    private sealed record Snapshot(List<TrainingCompletion> Completions, List<TrainingCertificationGrant> Grants,
        List<TrainingAuditRecord> Audit);
    private readonly DurableStorage _storage;
    private readonly string _dataDirectory;
    private string _path => Path.Combine(TenantContext.DirectoryFor(_dataDirectory), "training.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly WorkOrderCatalog _catalog;
    private readonly WorkforceStore _workforce;
    private readonly TrainingAccess _access;
    private readonly IncidentStore _incidents;
    private readonly CaseEventStream _events;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Snapshot> _partitions = new(StringComparer.Ordinal);
    private Snapshot? _snapshot
    { get => _partitions.GetValueOrDefault(TenantContext.CurrentId); set { if (value is not null) _partitions[TenantContext.CurrentId] = value; } }

    public TrainingStore(IConfiguration configuration, IWebHostEnvironment environment, WorkOrderCatalog catalog,
        WorkforceStore workforce, TrainingAccess access, IncidentStore incidents, CaseEventStream events, TimeProvider time, DurableStorage? persistence = null)
    {
        _dataDirectory = DataPaths.Resolve(configuration, environment);
        _storage = persistence ?? new DurableStorage(configuration, environment);
        _catalog = catalog; _workforce = workforce; _access = access; _incidents = incidents; _events = events; _time = time;
    }

    public async Task<TrainingWriteResult<TrainingCompletion>> CompleteAsync(TrainingCompletionRequest request,
        string key, string client, CancellationToken ct)
    {
        var error = Validate(request, key);
        if (error is not null) return new(null, Error: error);
        if (await _workforce.GetWorkerAsync(request.WorkerId, ct) is null) return new(null, Error: "Unknown synthetic worker.", NotFound: true);
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await LoadAsync(ct);
            var keyHash = Hash(client + ":completion:" + key); var requestHash = Hash(JsonSerializer.Serialize(request));
            var replay = snapshot.Completions.SingleOrDefault(record => record.IdempotencyKeyHash == keyHash);
            if (replay is not null)
            {
                if (replay.RequestHash != requestHash) return new(Clone(replay), Error: "Idempotency key belongs to a different completion.", Conflict: true);
                await EnsureGlobalAuditAsync(replay, ct); return new(Clone(replay));
            }
            var completion = new TrainingCompletion("trn-" + Guid.NewGuid().ToString("N"),
                request with { SkillsPractised = request.SkillsPractised.ToArray() }, client, _time.GetUtcNow(), keyHash, requestHash, TenantContext.CurrentId);
            var next = new Snapshot([.. snapshot.Completions, completion], snapshot.Grants.ToList(), snapshot.Audit.ToList());
            next.Audit.Add(Append(next.Audit, "training.completed", completion.Id, request.WorkerId, client, completion.RecordedAt, completion));
            await SaveAsync(next, ct);
            await EnsureGlobalAuditAsync(completion, ct);
            _events.Publish("training.completed");
            return new(Clone(completion), true);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<TrainingWriteResult<TrainingCertificationGrant>> GrantAsync(TrainingCertificationRequest request,
        string key, string client, CancellationToken ct)
    {
        var error = Key(key) ?? Text(request.WorkerId, "workerId", 80) ?? Text(request.CertificationTypeCode, "certificationTypeCode", 80)
            ?? Text(request.CompletionId, "completionId", 80) ?? Text(request.SignOffReference, "signOffReference", 128);
        if (error is not null) return new(null, Error: error);
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await LoadAsync(ct);
            var keyHash = Hash(client + ":certification:" + key); var requestHash = Hash(JsonSerializer.Serialize(request));
            var replay = snapshot.Grants.SingleOrDefault(record => record.IdempotencyKeyHash == keyHash);
            if (replay is not null)
            {
                if (replay.RequestHash != requestHash) return new(replay, Error: "Idempotency key belongs to a different certification request.", Conflict: true);
                await ProjectAsync(replay, ct); await EnsureGlobalAuditAsync(replay, ct); return new(replay);
            }
            if (!_access.CertificationGrantsEnabled) return new(null, Error: "Training certification grants are disabled by CIVICOPS_TRAINING_CERTIFICATIONS_ENABLED.");
            var type = _catalog.CertificationTypes.SingleOrDefault(type => type.Code == request.CertificationTypeCode);
            if (type is null) return new(null, Error: "Unknown certification type.");
            if (type.IssuingBasis != "simulation") return new(null, Error: "Only simulation certifications may be granted by training clients; external and other issuing bases require a separate verified workflow.");
            if (!request.SupervisorSignOff) return new(null, Error: "Supervisor sign-off is required.");
            var completion = snapshot.Completions.SingleOrDefault(record => record.Id == request.CompletionId);
            if (completion is null || completion.Record.WorkerId != request.WorkerId) return new(null, Error: "A matching worker completion is required.");
            if (!completion.Record.Passed || completion.Record.Score < _access.PassScore) return new(null, Error: "Completion must pass and meet CIVICOPS_TRAINING_PASS_SCORE.");
            var workType = _catalog.Find(completion.Record.WorkOrderTypeCode)!;
            if (!workType.RequiredCertifications.Contains(type.Code)) return new(null, Error: "Certification does not apply to the completion work-order type.");
            if (workType.RequiredSkills.Any(skill => !completion.Record.SkillsPractised.Contains(skill.Code))) return new(null, Error: "Completion must practise all skills required by the work-order type.");
            if (await _workforce.GetWorkerAsync(request.WorkerId, ct) is null) return new(null, Error: "Unknown synthetic worker.", NotFound: true);
            var issued = _time.GetUtcNow();
            var certification = new WorkerCertification(type.Code, issued, issued.AddMonths(type.ValidityMonths), client, "simulation", "sha256:" + completion.Record.EvidenceHash.ToLowerInvariant());
            var grant = new TrainingCertificationGrant("cert-" + Guid.NewGuid().ToString("N"), request, certification, client, issued, keyHash, requestHash, TenantContext.CurrentId);
            var next = new Snapshot(snapshot.Completions.ToList(), [.. snapshot.Grants, grant], snapshot.Audit.ToList());
            next.Audit.Add(Append(next.Audit, "certification.granted", grant.Id, request.WorkerId, client, issued, grant));
            await SaveAsync(next, ct); // Save the recoverable grant intent before mutating the workforce projection.
            await ProjectAsync(grant, ct);
            await EnsureGlobalAuditAsync(grant, ct);
            _events.Publish("certification.granted");
            return new(grant, true);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await LoadAsync(ct);
            foreach (var completion in snapshot.Completions) await EnsureGlobalAuditAsync(completion, ct);
            foreach (var grant in snapshot.Grants) { await ProjectAsync(grant, ct); await EnsureGlobalAuditAsync(grant, ct); }
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    private Task EnsureGlobalAuditAsync(TrainingCompletion completion, CancellationToken ct) => EnsureGlobalAuditAsync(
        "training.completed", completion.Id, completion.Client,
        $"Training completion {completion.Id} recorded for synthetic worker {completion.Record.WorkerId}.", completion.RecordedAt, ct);

    private Task EnsureGlobalAuditAsync(TrainingCertificationGrant grant, CancellationToken ct) => EnsureGlobalAuditAsync(
        "certification.granted", grant.Id, grant.Client,
        $"Simulation certification {grant.Certification.Code} granted to synthetic worker {grant.Record.WorkerId}; grant {grant.Id}; supervisor attestation {grant.Record.SignOffReference}.", grant.RecordedAt, ct);

    private async Task EnsureGlobalAuditAsync(string action, string id, string client, string detail, DateTimeOffset at, CancellationToken ct)
    {
        // The local atomic chain is authoritative; repair a crash between it and the shared incident chain.
        if (!(await _incidents.AuditAsync(ct)).Any(record => record.Action == action && record.Detail.Contains(id, StringComparison.Ordinal)))
            await _incidents.RecordSystemEventAsync(new(action, client, detail, at), ct);
    }

    private async Task ProjectAsync(TrainingCertificationGrant grant, CancellationToken ct) =>
        await _workforce.GrantCertificationAsync(grant.Record.WorkerId, grant.Certification, grant.Id, ct);

    public async Task<IReadOnlyList<TrainingCompletion>> QualificationsAsync(string workerId, CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Completions.Where(record => record.Record.WorkerId == workerId).Select(Clone).ToArray(); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TrainingAuditRecord>> AuditAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Audit.ToArray(); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<AuditVerification> VerifyAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return Verify(await LoadAsync(ct)); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    private string? Validate(TrainingCompletionRequest request, string key)
    {
        var invalid = Key(key) ?? Text(request.WorkerId, "workerId", 80) ?? Text(request.ModuleId, "moduleId", 128)
            ?? Text(request.WorkOrderTypeCode, "workOrderTypeCode", 80) ?? Text(request.DeviceClientLabel, "deviceClientLabel", 80);
        if (invalid is not null) return invalid;
        if (!IsHash(request.EvidenceHash)) return "evidenceHash must be a 64-character SHA-256 hex digest; do not submit raw evidence or personal information.";
        var type = _catalog.Find(request.WorkOrderTypeCode);
        if (type is null) return "Unknown workOrderTypeCode.";
        if (request.SkillsPractised is null || request.SkillsPractised.Count is < 1 or > 32
            || request.SkillsPractised.Any(code => string.IsNullOrWhiteSpace(code) || !_catalog.Skills.Any(skill => skill.Code == code))
            || request.SkillsPractised.Distinct(StringComparer.Ordinal).Count() != request.SkillsPractised.Count)
            return "skillsPractised requires 1–32 unique catalogue skill codes.";
        if (!double.IsFinite(request.Score) || request.Score is < 0 or > 100) return "score must be between 0 and 100.";
        if (request.DurationSeconds is < 1 or > 86400 || request.Attempt is < 1 or > 1000) return "durationSeconds or attempt exceeds its bound.";
        if (request.CompletedAt == default || request.CompletedAt > _time.GetUtcNow().AddMinutes(5)) return "completedAt must be an ISO timestamp at or before the current time (five minutes clock tolerance).";
        return null;
    }

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        if (!_storage.IsPostgres && _snapshot is not null) return _snapshot;
        var persisted = await _storage.ReadAsync(_path, ct);
        _partitions.Remove(TenantContext.CurrentId);
        if (persisted is null) return _snapshot = new([], [], []);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(persisted, _json) ?? throw new InvalidDataException("Invalid training snapshot.");
        if (!Verify(snapshot).Valid) throw new InvalidDataException("Training audit integrity check failed; reads and writes are disabled.");
        if (snapshot.Completions.Any(record => record.TenantId is not null && record.TenantId != TenantContext.CurrentId)
            || snapshot.Grants.Any(record => record.TenantId is not null && record.TenantId != TenantContext.CurrentId)
            || snapshot.Audit.Any(entry => entry.TenantId is not null && entry.TenantId != TenantContext.CurrentId)
            || TenantContext.CurrentId != TenantStore.DefaultTenantId && (snapshot.Completions.Any(record => record.TenantId is null)
                || snapshot.Grants.Any(record => record.TenantId is null) || snapshot.Audit.Any(entry => entry.TenantId is null)))
            throw new InvalidDataException("Training partition municipality mismatch.");
        if (snapshot.Completions.Any(record => record.TenantId is null) || snapshot.Grants.Any(record => record.TenantId is null))
        {
            snapshot = snapshot with
            {
                Completions = snapshot.Completions.Select(record => record with { TenantId = TenantContext.CurrentId }).ToList(),
                Grants = snapshot.Grants.Select(record => record with { TenantId = TenantContext.CurrentId }).ToList()
            };
            // Keep legacy chain hashes intact; historical record state hashes omit new routing metadata.
            await _storage.WriteAsync(_path, JsonSerializer.Serialize(snapshot, _json), ct);
        }
        return _snapshot = snapshot;
    }

    private async Task SaveAsync(Snapshot snapshot, CancellationToken ct)
    {
        if (!Verify(_snapshot ?? new([], [], [])).Valid || !Verify(snapshot).Valid) throw new InvalidDataException("Training audit integrity check failed; writes are disabled.");
        await _storage.WriteAsync(_path, JsonSerializer.Serialize(snapshot, _json), ct);
        _snapshot = snapshot;
    }

    private static TrainingAuditRecord Append<T>(List<TrainingAuditRecord> audit, string action, string id,
        string workerId, string client, DateTimeOffset at, T record)
    {
        var entry = new TrainingAuditRecord(audit.Count + 1L, action, id, workerId, client, at,
            RecordStateHash(record), audit.LastOrDefault()?.Hash ?? AuditLog.Genesis, "", TenantContext.CurrentId);
        return entry with { Hash = Compute(entry) };
    }

    private static TrainingCompletion Clone(TrainingCompletion completion) => completion with
    { Record = completion.Record with { SkillsPractised = completion.Record.SkillsPractised.ToArray() } };

    private static AuditVerification Verify(Snapshot snapshot)
    {
        var previous = AuditLog.Genesis;
        for (var index = 0; index < snapshot.Audit.Count; index++)
        {
            var entry = snapshot.Audit[index];
            if (entry.Sequence != index + 1L || entry.PreviousHash != previous || entry.Hash != Compute(entry))
                return new(false, snapshot.Audit.Count, entry.Sequence, "Training audit chain integrity check failed.");
            previous = entry.Hash;
        }
        if (snapshot.Audit.Count != snapshot.Completions.Count + snapshot.Grants.Count
            || snapshot.Audit.Select(record => record.RecordId).Distinct().Count() != snapshot.Audit.Count)
            return new(false, snapshot.Audit.Count, null, "Training audit record coverage differs from the durable snapshot.");
        foreach (var record in snapshot.Completions)
            if (!Authenticated(snapshot, record.Id, "training.completed", record.Record.WorkerId, record.Client, record.RecordedAt, record))
                return new(false, snapshot.Audit.Count, null, "Training completion snapshot differs from its audit state.");
        foreach (var record in snapshot.Grants)
            if (!Authenticated(snapshot, record.Id, "certification.granted", record.Record.WorkerId, record.Client, record.RecordedAt, record))
                return new(false, snapshot.Audit.Count, null, "Training grant snapshot differs from its audit state.");
        return new(true, snapshot.Audit.Count, null, "Training audit chain and snapshots verified; external anchoring is not configured.");
    }

    private static bool Authenticated<T>(Snapshot snapshot, string id, string action, string workerId, string client, DateTimeOffset at, T record)
    {
        var entry = snapshot.Audit.SingleOrDefault(entry => entry.RecordId == id);
        return entry is not null && entry.Action == action && entry.WorkerId == workerId && entry.Actor == client
            && entry.At == at && entry.StateHash == RecordStateHash(record);
    }
    private static string RecordStateHash<T>(T record) => Hash(JsonSerializer.Serialize<object?>(record switch
    {
        TrainingCompletion completion => completion with { TenantId = null },
        TrainingCertificationGrant grant => grant with { TenantId = null },
        _ => record
    }));
    private static string Compute(TrainingAuditRecord entry)
    {
        var fields = new Dictionary<string, object?>
        {
            [nameof(entry.Sequence)] = entry.Sequence, [nameof(entry.Action)] = entry.Action,
            [nameof(entry.RecordId)] = entry.RecordId, [nameof(entry.WorkerId)] = entry.WorkerId,
            [nameof(entry.Actor)] = entry.Actor, [nameof(entry.At)] = entry.At,
            [nameof(entry.StateHash)] = entry.StateHash, [nameof(entry.PreviousHash)] = entry.PreviousHash
        };
        if (entry.TenantId is not null) fields[nameof(entry.TenantId)] = entry.TenantId;
        return Hash(JsonSerializer.Serialize(fields));
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static string? Key(string? key) => Text(key, "Idempotency-Key", 128);
    private static string? Text(string? value, string name, int maximum) => string.IsNullOrWhiteSpace(value) || value.Length > maximum
        || value.Any(char.IsControl) ? $"{name} requires 1–{maximum} characters without control characters." : null;
}

public sealed class TrainingRecoveryService(TrainingStore store, TenantStore? tenants = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (tenants is null) { await store.RecoverAsync(cancellationToken); return; }
        foreach (var tenant in await tenants.ListAsync(cancellationToken))
        { using var lease = TenantContext.Use(tenant.Id); await store.RecoverAsync(cancellationToken); }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
