using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed record WorkforceAuditRecord(long Sequence, string Action, string Actor, string Detail,
    DateTimeOffset At, string PreviousHash, string StateHash, string Hash,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? TenantId = null);

/// <summary>Single-process atomic workforce snapshots with replay-safe certification grants.</summary>
public sealed class WorkforceStore : IDisposable
{
    public void Dispose() => _gate.Dispose();
    private readonly DurableStorage _storage;
    private readonly string _dataDirectory;
    private string _path => Path.Combine(TenantContext.DirectoryFor(_dataDirectory), "workforce.json");
    private readonly WorkOrderCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Dictionary<string, Snapshot> _partitions = new(StringComparer.Ordinal);
    private Snapshot? _snapshot
    { get => _partitions.GetValueOrDefault(TenantContext.CurrentId); set { if (value is not null) _partitions[TenantContext.CurrentId] = value; } }
    private sealed record Snapshot(List<Worker> Workers, List<Crew> Crews,
        Dictionary<string, string> GrantReceipts, List<WorkforceAuditRecord> Audit);
    public WorkforceStore(IConfiguration configuration, IWebHostEnvironment environment,
        WorkOrderCatalog catalog, TimeProvider time, DurableStorage? persistence = null)
    {
        _dataDirectory = DataPaths.Resolve(configuration, environment);
        _catalog = catalog; _time = time; _storage = persistence ?? new DurableStorage(configuration, environment);
    }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { await LoadAsync(ct); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<Worker>> ListWorkersAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return Clone(await LoadAsync(ct)).Workers; }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<Worker?> GetWorkerAsync(string id, CancellationToken ct) =>
        (await ListWorkersAsync(ct)).FirstOrDefault(worker => worker.Id == id);
    public async Task<IReadOnlyList<Crew>> ListCrewsAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return Clone(await LoadAsync(ct)).Crews; }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<bool> GrantCertificationAsync(string workerId, WorkerCertification certification,
        string operationId, CancellationToken ct)
    {
        var definition = _catalog.CertificationTypes.FirstOrDefault(type => type.Code == certification.Code);
        if (definition is null || certification.IssuedAt >= certification.ExpiresAt
            || certification.ExpiresAt > certification.IssuedAt.AddMonths(definition.ValidityMonths)
            || certification.IssuedAt > _time.GetUtcNow() || string.IsNullOrWhiteSpace(operationId) || operationId.Length > 200
            || string.IsNullOrWhiteSpace(certification.Issuer) || certification.Issuer.Length > 100
            || string.IsNullOrWhiteSpace(certification.EvidenceRef) || certification.EvidenceRef.Length > 200
            || certification.Source != definition.IssuingBasis)
            throw new InvalidOperationException("Invalid certification grant.");
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct);
            var fingerprint = IncidentStore.Hash(JsonSerializer.Serialize(new { workerId, certification }));
            if (current.GrantReceipts.TryGetValue(operationId, out var prior))
            {
                if (prior != fingerprint) throw new InvalidOperationException("Certification operation already belongs to another grant.");
                return false;
            }
            var next = Clone(current);
            var worker = next.Workers.FirstOrDefault(value => value.Id == workerId)
                ?? throw new InvalidOperationException("Unknown synthetic worker.");
            worker.Certifications.Add(certification);
            next.GrantReceipts[operationId] = fingerprint;
            await SaveAsync(next, "certification.granted", certification.Issuer,
                JsonSerializer.Serialize(new { workerId, certification.Code, operationId }), ct);
            return true;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task SetCrewAvailabilityAsync(string crewId, string availability, string actor, CancellationToken ct)
    {
        if (availability is not ("available" or "unavailable" or "off-shift") || string.IsNullOrWhiteSpace(actor))
            throw new InvalidOperationException("Invalid crew availability or actor.");
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var next = Clone(await LoadAsync(ct));
            var index = next.Crews.FindIndex(crew => crew.Id == crewId);
            if (index < 0) throw new InvalidOperationException("Unknown crew.");
            next.Crews[index] = next.Crews[index] with { Availability = availability };
            await SaveAsync(next, "crew.availability", actor, crewId + ":" + availability, ct);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<Crew> UpsertCrewAsync(Crew crew, string actor, CancellationToken ct)
    {
        if (!TenantContext.IsValidId(crew.Id) || InvalidText(crew.Name, 120) || InvalidText(crew.Capability, 300)
            || !_catalog.WorkOrderTypes.Any(type => type.Department == crew.Department)
            || crew.Availability is not ("available" or "unavailable" or "off-shift") || InvalidText(crew.Shift, 40)
            || crew.MemberIds is null || crew.MemberIds.Count > 50 || crew.MemberIds.Distinct().Count() != crew.MemberIds.Count
            || crew.MemberIds.Any(id => !TenantContext.IsValidId(id)) || InvalidText(actor, 200))
            throw new InvalidOperationException("Invalid crew: municipality-local id, name, catalogue department, shift, availability and unique members are required.");
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var next = Clone(await LoadAsync(ct));
            if (crew.MemberIds.Any(id => next.Workers.All(worker => worker.Id != id || worker.CrewId != crew.Id)))
                throw new InvalidOperationException("Crew members must be workers belonging to this municipality and crew.");
            var saved = crew with { TenantId = TenantContext.CurrentId, MemberIds = crew.MemberIds.ToArray() };
            var index = next.Crews.FindIndex(value => value.Id == crew.Id);
            if (index < 0) next.Crews.Add(saved); else next.Crews[index] = saved;
            await SaveAsync(next, index < 0 ? "crew.created" : "crew.updated", actor, crew.Id, ct);
            return saved with { MemberIds = saved.MemberIds!.ToArray() };
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<Worker> UpsertWorkerAsync(Worker worker, string actor, CancellationToken ct)
    {
        if (!TenantContext.IsValidId(worker.Id) || InvalidText(worker.DisplayName, 120) || InvalidText(actor, 200)
            || worker.Skills is null || worker.Skills.Count > 64 || worker.Skills.Select(skill => skill.Code).Distinct().Count() != worker.Skills.Count
            || worker.Skills.Any(skill => skill.Level is < 1 or > 5 || !_catalog.Skills.Any(type => type.Code == skill.Code))
            || worker.Certifications is null || worker.Certifications.Count > 100)
            throw new InvalidOperationException("Invalid worker: municipality-local id, display name, unique catalogue skills and bounded certifications are required.");
        foreach (var certification in worker.Certifications) ValidateCertification(certification);
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var next = Clone(await LoadAsync(ct));
            if (next.Crews.All(crew => crew.Id != worker.CrewId)) throw new InvalidOperationException("Unknown municipality crew.");
            var saved = worker with { TenantId = TenantContext.CurrentId, Skills = worker.Skills.ToList(), Certifications = worker.Certifications.ToList() };
            var index = next.Workers.FindIndex(value => value.Id == worker.Id);
            if (index < 0) next.Workers.Add(saved); else next.Workers[index] = saved;
            for (var crewIndex = 0; crewIndex < next.Crews.Count; crewIndex++)
            {
                var crew = next.Crews[crewIndex];
                var members = (crew.MemberIds ?? []).Where(id => id != worker.Id).ToList();
                if (crew.Id == worker.CrewId) members.Add(worker.Id);
                next.Crews[crewIndex] = crew with { MemberIds = members };
            }
            await SaveAsync(next, index < 0 ? "worker.created" : "worker.updated", actor, worker.Id, ct);
            return saved with { Skills = saved.Skills.ToList(), Certifications = saved.Certifications.ToList() };
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private void ValidateCertification(WorkerCertification certification)
    {
        var definition = _catalog.CertificationTypes.FirstOrDefault(type => type.Code == certification.Code);
        if (definition is null || certification.IssuedAt >= certification.ExpiresAt
            || certification.ExpiresAt > certification.IssuedAt.AddMonths(definition.ValidityMonths)
            || certification.IssuedAt > _time.GetUtcNow() || InvalidText(certification.Issuer, 100)
            || InvalidText(certification.EvidenceRef, 200) || certification.Source != definition.IssuingBasis)
            throw new InvalidOperationException("Invalid certification: catalogue basis, issuer, evidence reference and valid issue/expiry dates are required.");
    }
    private static bool InvalidText(string? text, int bound) => string.IsNullOrWhiteSpace(text) || text.Length > bound || text.Any(char.IsControl);
    public async Task<AuditVerification> VerifyAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return Verify(await LoadAsync(ct)); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        if (!_storage.IsPostgres && _snapshot is not null) return _snapshot;
        var persisted = await _storage.ReadAsync(_path, ct);
        _partitions.Remove(TenantContext.CurrentId);
        if (persisted is not null)
        {
            var loaded = JsonSerializer.Deserialize<Snapshot>(persisted, _json)
                ?? throw new InvalidDataException("Invalid workforce snapshot.");
            if (!Verify(loaded).Valid) throw new InvalidDataException("Workforce audit integrity check failed.");
            if (loaded.Workers.Any(worker => worker.TenantId is not null && worker.TenantId != TenantContext.CurrentId)
                || loaded.Crews.Any(crew => crew.TenantId is not null && crew.TenantId != TenantContext.CurrentId)
                || loaded.Audit.Any(entry => entry.TenantId is not null && entry.TenantId != TenantContext.CurrentId)
                || TenantContext.CurrentId != TenantStore.DefaultTenantId && (loaded.Workers.Any(worker => worker.TenantId is null)
                    || loaded.Crews.Any(crew => crew.TenantId is null) || loaded.Audit.Any(entry => entry.TenantId is null)))
                throw new InvalidDataException("Workforce partition municipality mismatch.");
            _snapshot = loaded;
            if (loaded.Workers.Any(worker => worker.TenantId is null) || loaded.Crews.Any(crew => crew.TenantId is null))
            {
                var migrated = Clone(loaded) with
                {
                    Workers = loaded.Workers.Select(worker => worker with { TenantId = TenantContext.CurrentId }).ToList(),
                    Crews = loaded.Crews.Select(crew => crew with { TenantId = TenantContext.CurrentId }).ToList()
                };
                await SaveAsync(migrated, "tenant.migrated", "CivicOps migration", "Legacy workforce bound to default municipality.", ct);
            }
            return _snapshot!;
        }
        var now = _time.GetUtcNow();
        var crews = IncidentWorkflow.Crews.Select(crew => crew with { TenantId = TenantContext.CurrentId, MemberIds = ["synthetic-" + crew.Id + "-a", "synthetic-" + crew.Id + "-b"] }).ToList();
        var workers = new List<Worker>();
        foreach (var crew in crews)
        {
            var types = _catalog.WorkOrderTypes.Where(type => type.Department == crew.Department).ToArray();
            var skills = types.SelectMany(type => type.RequiredSkills).GroupBy(skill => skill.Code)
                .Select(group => new WorkerSkill(group.Key, group.Max(skill => skill.MinimumLevel))).ToList();
            var certs = types.SelectMany(type => type.RequiredCertifications).Distinct().Select(code =>
            {
                var definition = _catalog.CertificationTypes.First(type => type.Code == code);
                // The electrical demonstration intentionally needs a renewed external authorisation.
                var expires = code == "ELEC.AUTHORISED_PERSON" ? now.AddDays(-1) : now.AddMonths(definition.ValidityMonths).AddDays(-1);
                return new WorkerCertification(code, expires.AddMonths(-definition.ValidityMonths), expires,
                    "Synthetic municipal supervisor", definition.IssuingBasis, "synthetic-seed:" + crew.Id + ":" + code);
            }).ToList();
            foreach (var id in crew.MemberIds!) workers.Add(new(id, "Synthetic " + crew.Name + (id.EndsWith("-a", StringComparison.Ordinal) ? " worker A" : " worker B"), crew.Id, skills.ToList(), certs.ToList(), TenantContext.CurrentId));
        }
        var seeded = new Snapshot(workers, crews, new(StringComparer.Ordinal), []);
        await SaveAsync(seeded, "workforce.seeded", "CivicOps synthetic seed", "Synthetic workers and crews only; no real personnel data.", ct);
        return _snapshot!;
    }
    private async Task SaveAsync(Snapshot next, string action, string actor, string detail, CancellationToken ct)
    {
        if (_snapshot is not null && !Verify(_snapshot).Valid) throw new InvalidDataException("Workforce audit integrity check failed; writes disabled.");
        var record = new WorkforceAuditRecord(next.Audit.Count + 1L, action, actor, detail, _time.GetUtcNow(),
            next.Audit.LastOrDefault()?.Hash ?? AuditLog.Genesis, StateHash(next), "", TenantContext.CurrentId);
        next.Audit.Add(record with { Hash = RecordHash(record) });
        await _storage.WriteAsync(_path, JsonSerializer.Serialize(next, _json), ct);
        _snapshot = next;
    }
    private Snapshot Clone(Snapshot snapshot) => JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(snapshot, _json), _json)!;
    private static string StateHash(Snapshot snapshot) => IncidentStore.Hash(JsonSerializer.Serialize(new { snapshot.Workers, snapshot.Crews, snapshot.GrantReceipts }));
    private static string RecordHash(WorkforceAuditRecord record) => IncidentStore.Hash(JsonSerializer.Serialize(record with { Hash = "" }));
    private static AuditVerification Verify(Snapshot snapshot)
    {
        var previous = AuditLog.Genesis;
        for (var i = 0; i < snapshot.Audit.Count; i++)
        {
            var record = snapshot.Audit[i];
            if (record.Sequence != i + 1L || record.PreviousHash != previous || record.Hash != RecordHash(record))
                return new(false, snapshot.Audit.Count, record.Sequence, "Workforce audit chain integrity check failed.");
            previous = record.Hash;
        }
        if (snapshot.Audit.Count == 0 || snapshot.Audit[^1].StateHash != StateHash(snapshot))
            return new(false, snapshot.Audit.Count, null, "Workforce snapshot differs from its authenticated state.");
        return new(true, snapshot.Audit.Count, null, "Workforce audit chain and snapshot verified.");
    }
}
