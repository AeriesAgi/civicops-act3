using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed record CreateResult(Incident Incident, bool Created, string? Error = null);
public sealed record ChangeResult(Incident? Incident, string? Error = null, bool Conflict = false);

public sealed class IncidentStore : IDisposable
{
    private readonly DurableStorage _storage;
    public DurableStorage Persistence => _storage;
    private readonly string _dataDirectory;
    private string _path => Path.Combine(TenantContext.DirectoryFor(_dataDirectory), "incidents.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };
    private readonly Dictionary<string, List<Incident>> _incidentPartitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AuditRecord>> _auditPartitions = new(StringComparer.Ordinal);
    private List<Incident>? _incidents
    { get => _incidentPartitions.GetValueOrDefault(TenantContext.CurrentId); set { if (value is not null) _incidentPartitions[TenantContext.CurrentId] = value; } }
    private List<AuditRecord> _audit
    { get { if (!_auditPartitions.TryGetValue(TenantContext.CurrentId, out var value)) _auditPartitions[TenantContext.CurrentId] = value = []; return value; }
        set => _auditPartitions[TenantContext.CurrentId] = value; }
    private readonly TimeProvider _time;
    private readonly CaseEventStream _events;
    private readonly PrivacyContactStore _contacts;
    private readonly GeoStore? _geo;
    private readonly bool _demo;

    public IncidentStore(IConfiguration configuration, IWebHostEnvironment environment, TimeProvider? time = null, CaseEventStream? events = null, DurableStorage? persistence = null, GeoStore? geo = null)
    {
        _dataDirectory = DataPaths.Resolve(configuration, environment);
        _time = time ?? TimeProvider.System;
        _events = events ?? new CaseEventStream();
        _storage = persistence ?? new DurableStorage(configuration, environment);
        _contacts = new PrivacyContactStore(_dataDirectory, _storage);
        _geo = geo;
        _demo = bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demo) && demo;
    }

    public async Task<IReadOnlyList<Incident>> ListAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).OrderByDescending(x => x.CreatedAt).Select(Clone).ToList(); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<Incident?> GetAsync(string id, CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var found = (await LoadAsync(ct)).FirstOrDefault(x => Matches(x, id));
            return found is null ? null : Clone(found);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    /// <summary>Map positions must come from a freshly authenticated durable case snapshot, even in file mode.</summary>
    public async Task<IReadOnlyList<Incident>> ListVerifiedAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin(); await transaction.InitializeAsync(ct); await _gate.WaitAsync(ct);
        try
        {
            _incidentPartitions.Remove(TenantContext.CurrentId); _auditPartitions.Remove(TenantContext.CurrentId);
            var incidents = await LoadAsync(ct);
            if (!AuditLog.VerifySnapshot(_audit, incidents).Valid)
                throw new GeoCaseIntegrityException("Map case positions are unavailable because durable incident integrity failed.");
            return incidents.OrderByDescending(value => value.CreatedAt).Select(Clone).ToList();
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<Incident?> GetVerifiedAsync(string id, CancellationToken ct) =>
        (await ListVerifiedAsync(ct)).FirstOrDefault(incident => Matches(incident, id));

    public async Task<CreateResult> CreateAsync(Incident incident, string? requestKey,
        DeterministicClassifier classifier, CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var incidents = await LoadAsync(ct);
            // Ignore body-selected municipality fields; trusted request context owns tenancy.
            incident.TenantId = TenantContext.CurrentId;
            if (incident.Synthetic && !_demo) return new(incident, false, "Synthetic scenario creation requires explicit demo mode.");
            if (incident.Location is not null && (!incident.LocationShared || !GeoGeofencing.IsValidPoint(incident.Location)
                || !incident.Synthetic && incident.PrivacyConsent?.Accepted is not true))
                return new(incident, false, "A report location requires explicit sharing and accepted purpose consent.");
            // Coordinates are never inferred from narrative, addresses, areas or provider metadata.
            (incident.MunicipalityId, incident.WardId) = _geo is null ? (null, null) : await _geo.AssignAsync(incident.Location, ct);
            var validation = RequestValidation.Intake(new IncidentRequest(incident.Description, incident.Area, incident.ContactEmail,
                incident.Source, incident.SourceLabel, incident.LocationProvenance, incident.ResidentLanguage,
                incident.ContactPreference, incident.ConsentToContact, incident.CallbackConsent, incident.ContactPhone,
                incident.ConversationId, incident.ServiceCode), requestKey);
            if (validation is not null) return new(incident, false, validation);
            var fingerprint = incident.IntakeFingerprint ?? Hash(JsonSerializer.Serialize(new
            {
                incident.Description, incident.NormalizedArea, incident.ContactEmail,
                incident.Source, incident.SourceLabel, incident.LocationProvenance, incident.ServiceCode,
                incident.ResidentLanguage, incident.ContactPreference, incident.ConsentToContact,
                incident.CallbackConsent, incident.ContactPhone, incident.ConversationId
            }));
            var keyHash = string.IsNullOrEmpty(requestKey) ? null : Hash(requestKey);
            if (keyHash is not null && _audit.Any(record => record.Action == "intake-key-retired" && record.Detail == keyHash))
                return new(incident, false, "This submission key belongs to a case removed by retention. Use a new key for a new report.");
            var replay = keyHash is null ? null : incidents.FirstOrDefault(x => x.IntakeKeyHash == keyHash);
            if (replay is not null)
                return new(Clone(replay), false, replay.IntakeFingerprint == fingerprint
                    ? null : "This submission key already belongs to a different report.");

            incident.IntakeKeyHash = keyHash;
            incident.IntakeFingerprint = fingerprint;
            var duplicate = incident.Synthetic ? null : classifier.FindDuplicate(incidents, incident);
            if (duplicate is not null)
            {
                incident.DuplicateOf = duplicate.ReferenceNumber;
                incident.DuplicateConfidence = duplicate.Confidence;
                incident.Status = IncidentStatus.PendingApproval;
                incident.PublicUpdate = $"Possible duplicate of {duplicate.ReferenceNumber}; human review required.";
            }
            incident.WorkOrderTypeCode ??= classifier.Classify(incident.Description).WorkOrderTypeCode;
            incident.PrivacySeparated = true;
            incident.ContactRecordHash = await _contacts.SaveAsync(incident, ct);
            for (var index = 0; index < incident.Audit.Count; index++)
                incident.Audit[index] = incident.Audit[index] with { Detail = PiiRedactor.Redact(incident.Audit[index].Detail) };
            incident.Audit.Add(new("received", "CivicOps intake", incident.PublicUpdate, incident.CreatedAt));
            if (incident.Location is not null)
                incident.Audit.Add(new("geo.assigned", "CivicOps geofencing", incident.WardId is null
                    ? "Voluntarily shared coordinate is outside configured ward polygons; human location confirmation required."
                    : $"Polygon assignment to {incident.MunicipalityId}/{incident.WardId}; source location remains unverified.", incident.CreatedAt));
            var audit = _audit.ToList();
            foreach (var entry in incident.Audit) audit.Add(AuditLog.Append(audit, incident, entry));
            await SaveAsync(incidents.Append(Clone(incident)).ToList(), audit, ct);
            return new(Clone(incident), true);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public Task<ChangeResult> ChangeAsync(string id, int? expectedVersion,
        Func<Incident, string?> apply, CancellationToken ct) =>
        ChangeCoreAsync(id, expectedVersion, apply, ct);

    // Workflow drafting runs outside the lock. Only an omitted caller version may
    // tolerate delivery receipts that changed no human, safety or privacy state.
    internal Task<ChangeResult> ChangeWorkflowAsync(string id, Incident baseline, bool allowDeliveryRebase,
        Func<Incident, string?> apply, CancellationToken ct) =>
        ChangeCoreAsync(id, baseline.Version, apply, ct, allowDeliveryRebase ? baseline : null);

    private async Task<ChangeResult> ChangeCoreAsync(string id, int? expectedVersion,
        Func<Incident, string?> apply, CancellationToken ct, Incident? deliveryBaseline = null)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var incidents = await LoadAsync(ct);
            var index = incidents.FindIndex(x => Matches(x, id));
            if (index < 0) return new(null);
            var candidate = Clone(incidents[index]);
            if (expectedVersion is not null && candidate.Version != expectedVersion
                && (deliveryBaseline is null || !OnlyDeliveryReceiptsChanged(deliveryBaseline, candidate)))
                return new(candidate, "This case changed. Refresh before trying again.", true);
            var error = apply(candidate);
            if (candidate.TenantId != TenantContext.CurrentId) return new(Clone(incidents[index]), "Municipality binding cannot be changed.");
            if (error is not null) return new(candidate, error);
            if (candidate.Audit.Count < incidents[index].Audit.Count || !candidate.Audit.Take(incidents[index].Audit.Count).SequenceEqual(incidents[index].Audit))
                return new(Clone(incidents[index]), "Existing audit events cannot be modified or removed.");
            for (var addition = incidents[index].Audit.Count; addition < candidate.Audit.Count; addition++)
                candidate.Audit[addition] = candidate.Audit[addition] with { Detail = PiiRedactor.Redact(candidate.Audit[addition].Detail) };
            candidate.PrivacySeparated = true;
            candidate.ContactRecordHash = await _contacts.SaveAsync(candidate, ct);
            candidate.WorkOrderTypeCode ??= new DeterministicClassifier().Classify(candidate.Description).WorkOrderTypeCode;
            candidate.Version++;
            candidate.UpdatedAt = _time.GetUtcNow();
            var next = incidents.ToList();
            next[index] = candidate;
            var audit = _audit.ToList();
            var additions = candidate.Audit.Skip(incidents[index].Audit.Count).ToList();
            if (additions.Count == 0)
            {
                var entry = new AuditEvent("metadata-updated", "CivicOps backend", "Case metadata updated.", _time.GetUtcNow());
                candidate.Audit.Add(entry); additions.Add(entry);
            }
            foreach (var entry in additions) audit.Add(AuditLog.Append(audit, candidate, entry));
            await SaveAsync(next, audit, ct);
            return new(Clone(candidate));
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    internal bool OnlyDeliveryReceiptsChanged(Incident baseline, Incident current)
    {
        var additions = current.Audit.Count - baseline.Audit.Count;
        if (additions <= 0 || (long)current.Version - baseline.Version != additions
            || !current.Audit.Take(baseline.Audit.Count).SequenceEqual(baseline.Audit)
            || current.Audit.Skip(baseline.Audit.Count).Any(entry =>
                !DeliveryReceipt.IsReceipt(entry.Action, entry.Actor))) return false;

        // Compare every field, including contacts and any future model additions.
        // AuditLog.SerializeState omits contact values and is unsuitable here.
        var comparable = Clone(current);
        comparable.Version = baseline.Version;
        comparable.UpdatedAt = baseline.UpdatedAt;
        comparable.ZendeskTicketId = baseline.ZendeskTicketId;
        comparable.ZendeskTicketUrl = baseline.ZendeskTicketUrl;
        comparable.Audit = baseline.Audit;
        return JsonSerializer.Serialize(comparable, _json) == JsonSerializer.Serialize(baseline, _json);
    }

    /// <summary>One bounded demo-only commit; every synthetic case keeps its own authenticated audit events.</summary>
    internal async Task<int> CreateSyntheticScenarioAsync(IReadOnlyList<Incident> scenario, CancellationToken ct)
    {
        if (!_demo || scenario.Count != 120 || scenario.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != scenario.Count
            || scenario.Select(value => value.ReferenceNumber).Distinct(StringComparer.Ordinal).Count() != scenario.Count)
            throw new InvalidOperationException("A bounded 120-case synthetic scenario requires explicit demo mode.");
        await using var transaction = _storage.Begin(); await transaction.InitializeAsync(ct); await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct); var next = current.Select(Clone).ToList(); var audit = _audit.ToList();
            var boundaries = _geo is null ? null : await _geo.ReadAsync(ct); var created = 0;
            var retired = _audit.Where(value => value.Action == "intake-key-retired").Select(value => value.Detail).ToHashSet(StringComparer.Ordinal);
            foreach (var original in scenario)
            {
                var incident = Clone(original);
                if (!incident.Synthetic || !incident.LocationShared || !GeoGeofencing.IsValidPoint(incident.Location) || incident.Source != "demo-scenario"
                    || !incident.Id.StartsWith("synthetic-map-", StringComparison.Ordinal) || !incident.ReferenceNumber.StartsWith("CIV-DEMO-", StringComparison.Ordinal)
                    || incident.ContactEmail is not null || incident.ContactPhone is not null || incident.ConsentToContact || incident.CallbackConsent
                    || incident.RawTranscript is not null || incident.TranscriptSummary is not null || incident.ConversationId is not null
                    || incident.AgentRuns.Count != 0 || incident.CommsDrafts.Count != 0)
                    throw new InvalidDataException("Scenario seed accepts synthetic exercise incidents only, with no contacts, transcripts or provider activity.");
                var invalid = RequestValidation.Intake(new IncidentRequest(incident.Description, incident.Area, ServiceCode: incident.ServiceCode), null);
                if (invalid is not null) throw new InvalidDataException("Synthetic scenario does not conform to intake bounds.");
                incident.TenantId = TenantContext.CurrentId;
                var key = Hash("map-scenario-v1-" + incident.Id);
                if (retired.Contains(key)) throw new InvalidOperationException("A scenario submission key was retired by retention; removed cases are not recreated.");
                if (current.Any(value => value.Id == incident.Id || value.IntakeKeyHash == key)) continue;
                if (current.Any(value => value.ReferenceNumber == incident.ReferenceNumber)) throw new InvalidDataException("Scenario reference already belongs to a different case.");
                incident.IntakeKeyHash = key; incident.IntakeFingerprint = Hash("SYNTHETIC scenario v1:" + incident.Id);
                incident.PrivacySeparated = true; incident.ContactRecordHash = null;
                (incident.MunicipalityId, incident.WardId) = boundaries is null ? (null, null)
                    : GeoGeofencing.Assign(incident.Location, boundaries.Municipalities, boundaries.Wards);
                incident.Audit.Add(new("received", "CivicOps synthetic intake", "SYNTHETIC exercise report; no resident or external provider involved.", incident.CreatedAt));
                incident.Audit.Add(new("geo.assigned", "CivicOps geofencing", incident.WardId is null ? "Synthetic coordinate is outside configured ward polygons."
                    : $"Synthetic polygon assignment to {incident.MunicipalityId}/{incident.WardId}.", incident.CreatedAt));
                foreach (var entry in incident.Audit) audit.Add(AuditLog.Append(audit, incident, entry));
                next.Add(incident); created++;
            }
            if (created > 0) await SaveAsync(next, audit, ct);
            return created;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    private async Task<List<Incident>> LoadAsync(CancellationToken ct)
    {
        if (!_storage.IsPostgres && _incidents is not null) return _incidents;
        var persisted = await _storage.ReadAsync(_path, ct);
        _incidentPartitions.Remove(TenantContext.CurrentId); _auditPartitions.Remove(TenantContext.CurrentId);
        if (persisted is null)
        {
            await _contacts.PruneUnreferencedAsync(new HashSet<string>(StringComparer.Ordinal), ct);
            return _incidents = [];
        }
        using var document = JsonDocument.Parse(persisted);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            _incidents = document.RootElement.Deserialize<List<Incident>>(_json) ?? [];
            foreach (var incident in _incidents)
                foreach (var entry in incident.Audit) _audit.Add(AuditLog.Append(_audit, incident, entry));
        }
        else
        {
            var snapshot = document.RootElement.Deserialize<StoreSnapshot>(_json) ?? throw new InvalidDataException("Invalid store.");
            _incidents = snapshot.Incidents; _audit = snapshot.Audit;
        }
        if (_incidents!.Any(incident => incident.TenantId is not null && incident.TenantId != TenantContext.CurrentId)
            || _audit.Any(record => record.TenantId is not null && record.TenantId != TenantContext.CurrentId)
            || TenantContext.CurrentId != TenantStore.DefaultTenantId && (_incidents.Any(incident => incident.TenantId is null) || _audit.Any(record => record.TenantId is null)))
        {
            _incidentPartitions.Remove(TenantContext.CurrentId); _auditPartitions.Remove(TenantContext.CurrentId);
            throw new InvalidDataException("Incident partition municipality mismatch.");
        }
        if (_incidents.Any(incident => incident.TenantId is null) && AuditLog.VerifySnapshot(_audit, _incidents).Valid)
        {
            // Extend the historical chain; never rewrite or discard pre-migration audit hashes.
            var next = _incidents.Select(Clone).ToList(); var audit = _audit.ToList();
            foreach (var incident in next.Where(incident => incident.TenantId is null))
            {
                incident.TenantId = TenantContext.CurrentId;
                var migrated = new AuditEvent("tenant.migrated", "CivicOps migration", "Legacy case bound to default municipality.", _time.GetUtcNow());
                incident.Audit.Add(migrated); audit.Add(AuditLog.Append(audit, incident, migrated));
            }
            await SaveAsync(next, audit, ct);
        }
        if (AuditLog.VerifySnapshot(_audit, _incidents).Valid)
        {
            var legacyContacts = _incidents.Where(incident => !incident.PrivacySeparated
                && (incident.ContactEmail is not null || incident.ContactPhone is not null)).ToArray();
            if (legacyContacts.Length > 0)
            {
                var next = _incidents.Select(Clone).ToList(); var audit = _audit.ToList();
                foreach (var incident in next.Where(incident => legacyContacts.Any(legacy => legacy.Id == incident.Id)))
                {
                    incident.PrivacySeparated = true;
                    incident.ContactRecordHash = await _contacts.SaveAsync(incident, ct);
                    var entry = new AuditEvent("privacy.contact-separated", "CivicOps migration", "Legacy optional contact moved to separate storage; historical audit retained. Consent remains unverified.", _time.GetUtcNow());
                    incident.Audit.Add(entry); audit.Add(AuditLog.Append(audit, incident, entry));
                }
                await SaveAsync(next, audit, ct);
            }
            try
            {
                // Contacts are one municipality snapshot, so read/validate it once per case snapshot.
                // Re-reading it for every case caused N extra database commands on every queue/write.
                var contacts = await _contacts.ReadAllAsync(ct);
                foreach (var incident in _incidents.Where(incident => incident.PrivacySeparated))
                {
                    var contact = contacts.GetValueOrDefault(incident.Id);
                    if ((contact is null ? null : PrivacyContactStore.Hash(contact)) != incident.ContactRecordHash)
                        throw new InvalidDataException("Contact record differs from authenticated case state; writes are disabled.");
                    incident.ContactEmail = contact?.Email; incident.ContactPhone = contact?.Phone;
                }
            }
            catch
            {
                _incidentPartitions.Remove(TenantContext.CurrentId); _auditPartitions.Remove(TenantContext.CurrentId);
                throw;
            }
            await _contacts.PruneUnreferencedAsync(_incidents.Select(incident => incident.Id).ToHashSet(StringComparer.Ordinal), ct);
        }
        return _incidents!;
    }

    public async Task<IReadOnlyList<AuditRecord>> AuditAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { await LoadAsync(ct); return _audit.ToArray(); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public async Task RecordSystemEventAsync(AuditEvent entry, CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var incidents = await LoadAsync(ct);
            var system = new Incident { TenantId = TenantContext.CurrentId, Id = "system", ReferenceNumber = "SYSTEM", CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow() };
            var audit = _audit.ToList(); audit.Add(AuditLog.Append(audit, system, entry with { Detail = PiiRedactor.Redact(entry.Detail) }));
            await SaveAsync(incidents, audit, ct);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<AuditVerification> VerifyAuditAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { var incidents = await LoadAsync(ct); return AuditLog.VerifySnapshot(_audit, incidents); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private sealed record StoreSnapshot(List<Incident> Incidents, List<AuditRecord> Audit);

    public async Task<int> PurgeClosedAsync(DateTimeOffset cutoff, CancellationToken ct, IReadOnlySet<string>? protectedCaseIds = null)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var incidents = await LoadAsync(ct);
            // Use the closing milestone, not report age: an old report can have just been closed.
            var expired = incidents.Where(incident => incident.Status == IncidentStatus.Closed
                && !(protectedCaseIds?.Contains(incident.Id) ?? false)
                && (incident.Audit.LastOrDefault(entry => entry.Action == "closed")?.At ?? incident.UpdatedAt) < cutoff).ToArray();
            if (expired.Length == 0) return 0;
            var removed = expired.Select(incident => incident.Id).ToHashSet(StringComparer.Ordinal);
            var next = incidents.Where(incident => !removed.Contains(incident.Id)).ToList();
            var system = new Incident { TenantId = TenantContext.CurrentId, Id = "system", ReferenceNumber = "SYSTEM" };
            var audit = _audit.ToList();
            foreach (var incident in expired.Where(incident => incident.IntakeKeyHash is not null))
                audit.Add(AuditLog.Append(audit, system, new AuditEvent("intake-key-retired", "CivicOps retention", incident.IntakeKeyHash!, _time.GetUtcNow())));
            foreach (var incident in expired)
                audit.Add(AuditLog.Append(audit, system, new AuditEvent("privacy.contact-purged", "CivicOps retention", $"Case {incident.ReferenceNumber}: optional contact erased; no contact value retained in tombstone.", _time.GetUtcNow())));
            audit.Add(AuditLog.Append(audit, system, new AuditEvent("retention-purged", "CivicOps retention",
                $"Removed {expired.Length} closed case snapshots with closing milestones before {cutoff:O}. Audit chain retained.", _time.GetUtcNow())));
            await SaveAsync(next, audit, ct);
            await _contacts.DeleteAsync(removed, ct);
            return expired.Length;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<int> PurgeContactFieldsAsync(DateTimeOffset cutoff, CancellationToken ct, IReadOnlySet<string>? protectedCaseIds = null)
    {
        var expired = (await ListAsync(ct)).Where(incident => incident.Status == IncidentStatus.Closed
            && !(protectedCaseIds?.Contains(incident.Id) ?? false) && (incident.ContactRecordHash is not null || incident.Location is not null)
            && (incident.Audit.LastOrDefault(entry => entry.Action == "closed")?.At ?? incident.UpdatedAt) < cutoff).ToArray();
        var count = 0;
        foreach (var incident in expired)
        {
            var result = await ChangeAsync(incident.Id, incident.Version, candidate =>
            {
                candidate.ContactEmail = candidate.ContactPhone = null;
                candidate.Location = null; candidate.LocationShared = false; candidate.MunicipalityId = candidate.WardId = null;
                candidate.ConsentToContact = candidate.CallbackConsent = false;
                candidate.ContactPreference = "none";
                candidate.Audit.Add(new("privacy.contact-purged", "CivicOps retention", "Optional contact fields erased under configured retention; no contact values retained in tombstone.", _time.GetUtcNow()));
                return null;
            }, ct);
            if (result.Error is null && result.Incident is not null) count++;
        }
        return count;
    }

    public async Task<int> PurgeLocationFieldsAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var expired = (await ListAsync(ct)).Where(incident => incident.Location is not null && incident.CreatedAt < cutoff).ToArray();
        var count = 0;
        foreach (var incident in expired)
        {
            var result = await ChangeAsync(incident.Id, incident.Version, candidate =>
            {
                candidate.Location = null; candidate.LocationShared = false; candidate.MunicipalityId = candidate.WardId = null;
                candidate.Audit.Add(new("geo.location-purged", "CivicOps automatic retention",
                    "Voluntarily shared report coordinate removed after the default 30-day purpose window; no coordinate value retained in this event.", _time.GetUtcNow()));
                return null;
            }, ct);
            if (result.Error is null && result.Incident is not null) count++;
        }
        return count;
    }

    private async Task SaveAsync(List<Incident> incidents, List<AuditRecord> audit, CancellationToken ct)
    {
        if (!AuditLog.VerifySnapshot(_audit, _incidents ?? []).Valid) throw new InvalidDataException("Audit chain integrity check failed; writes are disabled.");
        var snapshots = incidents.Select(incident => JsonSerializer.Deserialize<Incident>(AuditLog.SerializeState(incident))!).ToList();
        await _storage.WriteAsync(_path, JsonSerializer.Serialize(new StoreSnapshot(snapshots, audit), _json), ct);
        await _storage.StageIncidentIntentsAsync(Path.Combine(TenantContext.DirectoryFor(_dataDirectory), "integration-outbox.json"), incidents, _time.GetUtcNow(), ct);
        _incidents = incidents; // Readers see the change only after the durable write succeeds.
        _audit = audit;
        _events.Publish();
    }

    private Incident Clone(Incident incident) =>
        JsonSerializer.Deserialize<Incident>(JsonSerializer.Serialize(incident, _json), _json)!;
    private static bool Matches(Incident incident, string id) =>
        incident.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
        || incident.ReferenceNumber.Equals(id, StringComparison.OrdinalIgnoreCase);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() => _gate.Dispose();
}
