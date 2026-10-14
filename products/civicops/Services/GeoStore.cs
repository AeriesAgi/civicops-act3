using System.Text.Json;
using System.Text.Json.Serialization;
using CivicOps.Models;

namespace CivicOps.Services;

/// <summary>Tenant-local durable polygons and structured hazards. No images, live worker positions or position history.</summary>
public sealed class GeoStore : IDisposable
{
    public static readonly TimeSpan HazardRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan SyntheticCrewRetention = TimeSpan.FromHours(24);
    public static readonly TimeSpan ReportLocationRetention = TimeSpan.FromDays(30);
    private readonly string _dataDirectory;
    private readonly DurableStorage _storage;
    private readonly TimeProvider _time;
    private readonly CaseEventStream _events;
    private readonly bool _demo;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private string PathForTenant => Path.Combine(TenantContext.DirectoryFor(_dataDirectory), "geo.json");
    public GeoStore(IConfiguration configuration, IWebHostEnvironment environment, DurableStorage storage,
        TimeProvider time, CaseEventStream events)
    { _dataDirectory = DataPaths.Resolve(configuration, environment); _storage = storage; _time = time; _events = events; _demo = bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demo) && demo; }
    public async Task<GeoSnapshot> ReadAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin(); await transaction.InitializeAsync(ct); await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await LoadAsync(ct); var now = _time.GetUtcNow();
            // A stopped purger cannot expose expired events; physical cleanup also runs hourly.
            return snapshot with
            {
                CrewPositions = _demo ? snapshot.CrewPositions.Where(value => value.LastSeenAt >= now - SyntheticCrewRetention).ToList() : [],
                Hazards = snapshot.Hazards.Where(value => value.OccurredAt >= now - HazardRetention && (_demo || !value.Synthetic)).ToList(),
                Assets = snapshot.Assets.Where(value => _demo || !value.Synthetic).ToList()
            };
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<(string? MunicipalityId, string? WardId)> AssignAsync(GeoPoint? location, CancellationToken ct)
    { var snapshot = await ReadAsync(ct); return GeoGeofencing.Assign(location, snapshot.Municipalities, snapshot.Wards); }
    public Task<GeoSnapshot> SetGeographiesAsync(MapGeographiesRequest request, string actor, CancellationToken ct) => MutateAsync(snapshot =>
    {
        if (request.ExpectedVersion != snapshot.Version) throw new GeoConflictException("Map geography changed. Refresh before saving.");
        if (request.Municipalities is null || request.Wards is null || request.Municipalities.Count > 20 || request.Wards.Count > 64
            || request.Municipalities.Any(value => value is null) || request.Wards.Any(value => value is null))
            throw new InvalidDataException("Bounded municipalities and wards are required.");
        var municipalities = request.Municipalities.Select(value => value with { TenantId = TenantContext.CurrentId }).ToList();
        var wards = request.Wards.Select(value => value with { TenantId = TenantContext.CurrentId }).ToList();
        return snapshot with { Municipalities = municipalities, Wards = wards };
    }, "geo.boundaries-reviewed", actor, request.Reason, ct);
    public Task<GeoSnapshot> AddHazardAsync(MapHazardRequest request, string actor, CancellationToken ct) => MutateAsync(snapshot =>
    {
        if (snapshot.Hazards.Count >= 1000) throw new InvalidDataException("Hazard limit reached; resolve retention before adding events.");
        if (snapshot.Hazards.Any(value => value.Id == request.Id)) throw new GeoConflictException("This hazard id already exists.");
        var hazard = new GeoHazard(request.Id, request.EventType, PiiRedactor.Redact(request.Title), request.Location, request.Confidence,
            request.ExclusionRadiusMeters, request.OccurredAt, request.CameraId, false, TenantContext.CurrentId);
        var hazards = snapshot.Hazards.ToList(); hazards.Add(hazard);
        return snapshot with { Hazards = hazards };
    }, "geo.hazard-reviewed", actor, request.Reason, ct);
    public Task<GeoSnapshot> SeedScenarioAsync(IReadOnlyList<Crew> crews, string actor, CancellationToken ct)
    {
        if (!_demo) throw new InvalidOperationException("Synthetic map seed is disabled outside explicit demo mode.");
        var now = _time.GetUtcNow(); var random = new Random(GeoScenario.Seed + 2);
        return MutateAsync(snapshot =>
        {
            var positions = crews.OrderBy(value => value.Id, StringComparer.Ordinal).Take(25).Select((crew, index) => new GeoCrewPosition(crew.Id,
                GeoScenario.Position(0.1 + random.NextDouble() * 0.8, 0.08 + random.NextDouble() * 0.84),
                now.AddMinutes(index % 8 == 0 ? -24 : -1 - index % 10), crew.Availability == "available" ? "ready" : crew.Availability,
                true, TenantContext.CurrentId)).ToList();
            return snapshot with
            {
                CrewPositions = positions,
                Hazards = snapshot.Hazards.Where(value => !value.Synthetic).Concat(GeoScenario.Hazards(TenantContext.CurrentId, now)).ToList(),
                Assets = snapshot.Assets.Where(value => !value.Synthetic).Concat(GeoScenario.Assets(TenantContext.CurrentId)).ToList()
            };
        }, "geo.scenario-seeded", actor, "SYNTHETIC deterministic evening positions and hazards; no live tracking or external dispatch.", ct);
    }
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin(); await transaction.InitializeAsync(ct); await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await LoadAsync(ct); var now = _time.GetUtcNow();
            var positions = snapshot.CrewPositions.Where(value => _demo && value.LastSeenAt >= now - SyntheticCrewRetention).ToList();
            var hazards = snapshot.Hazards.Where(value => value.OccurredAt >= now - HazardRetention && (_demo || !value.Synthetic)).ToList();
            var removed = snapshot.CrewPositions.Count + snapshot.Hazards.Count - positions.Count - hazards.Count;
            if (removed > 0) await SaveAsync(snapshot with { CrewPositions = positions, Hazards = hazards }, "geo.retention-purged", "CivicOps automatic retention",
                $"Removed {removed} expired structured hazards or synthetic latest-position records; no location values retained in this event.", ct);
            return removed;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<GeoSnapshot> MutateAsync(Func<GeoSnapshot, GeoSnapshot> apply, string action, string actor, string reason, CancellationToken ct)
    {
        if (RequestValidation.Text(actor, "actor", 200, 1) is not null || RequestValidation.Text(reason, "reason", 2000, 5) is not null)
            throw new InvalidDataException("A named authorized human and a reason of 5–2000 characters are required.");
        await using var transaction = _storage.Begin(); await transaction.InitializeAsync(ct); await _gate.WaitAsync(ct);
        try
        {
            var snapshot = await LoadAsync(ct); var now = _time.GetUtcNow();
            var priorPointCount = snapshot.CrewPositions.Count + snapshot.Hazards.Count;
            snapshot = snapshot with
            {
                CrewPositions = snapshot.CrewPositions.Where(value => _demo && value.LastSeenAt >= now - SyntheticCrewRetention).ToList(),
                Hazards = snapshot.Hazards.Where(value => value.OccurredAt >= now - HazardRetention && (_demo || !value.Synthetic)).ToList()
            };
            var removed = priorPointCount - snapshot.CrewPositions.Count - snapshot.Hazards.Count;
            var detail = removed == 0 ? reason : reason + $" Automatic purpose retention also removed {removed} expired latest positions or structured hazards; no location values retained in this audit detail.";
            return await SaveAsync(apply(snapshot), action, actor, detail, ct);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<GeoSnapshot> LoadAsync(CancellationToken ct)
    {
        var persisted = await _storage.ReadAsync(PathForTenant, ct);
        if (persisted is null) return _demo ? GeoScenario.Boundaries(TenantContext.CurrentId) : new(TenantContext.CurrentId, 0, [], [], [], [], [], []);
        var snapshot = JsonSerializer.Deserialize<GeoSnapshot>(persisted, Json) ?? throw new InvalidDataException("Invalid map state.");
        Validate(snapshot, allowRetainedSynthetic: true);
        var previous = AuditLog.Genesis;
        for (var index = 0; index < snapshot.Audit.Count; index++)
        {
            var record = snapshot.Audit[index];
            if (record.TenantId != TenantContext.CurrentId || record.Sequence != index + 1L || record.PreviousHash != previous || record.Hash != RecordHash(record))
                throw new InvalidDataException("Map audit integrity failed.");
            previous = record.Hash;
        }
        if (snapshot.Audit.Count == 0 || snapshot.Audit[^1].StateHash != StateHash(snapshot)) throw new InvalidDataException("Map state differs from authenticated snapshot.");
        return snapshot;
    }
    private async Task<GeoSnapshot> SaveAsync(GeoSnapshot snapshot, string action, string actor, string detail, CancellationToken ct)
    {
        var next = snapshot with { Version = snapshot.Version + 1, Audit = snapshot.Audit.ToList() }; Validate(next);
        var record = new GeoAuditRecord(next.Audit.Count + 1L, action, actor, PiiRedactor.Redact(detail), _time.GetUtcNow(),
            next.Audit.LastOrDefault()?.Hash ?? AuditLog.Genesis, StateHash(next), "", TenantContext.CurrentId);
        next.Audit.Add(record with { Hash = RecordHash(record) });
        await _storage.WriteAsync(PathForTenant, JsonSerializer.Serialize(next, Json), ct); _events.Publish(); return next;
    }
    private void Validate(GeoSnapshot snapshot, bool allowRetainedSynthetic = false)
    {
        if (snapshot.TenantId != TenantContext.CurrentId || snapshot.Version < 0 || snapshot.Municipalities is null || snapshot.Wards is null
            || snapshot.CrewPositions is null || snapshot.Hazards is null || snapshot.Assets is null || snapshot.Audit is null
            || snapshot.Municipalities.Count > 20 || snapshot.Wards.Count > 64 || snapshot.CrewPositions.Count > 1000 || snapshot.Hazards.Count > 1000 || snapshot.Assets.Count > 2000)
            throw new InvalidDataException("Invalid map partition or bounds.");
        static bool Text(string? value, int max = 160) => RequestValidation.Text(value, "value", max, 1) is null;
        static bool Unique(IEnumerable<string> ids) => ids.Distinct(StringComparer.Ordinal).Count() == ids.Count();
        if (snapshot.Municipalities.Any(value => value is null) || snapshot.Wards.Any(value => value is null)
            || snapshot.CrewPositions.Any(value => value is null) || snapshot.Hazards.Any(value => value is null) || snapshot.Assets.Any(value => value is null) || snapshot.Audit.Any(value => value is null))
            throw new InvalidDataException("Map records cannot be null.");
        if (!Unique(snapshot.Municipalities.Select(value => value.Id)) || !Unique(snapshot.Wards.Select(value => value.Id))
            || !Unique(snapshot.CrewPositions.Select(value => value.CrewId)) || !Unique(snapshot.Hazards.Select(value => value.Id)) || !Unique(snapshot.Assets.Select(value => value.Id)))
            throw new InvalidDataException("Map entity ids must be unique.");
        foreach (var municipality in snapshot.Municipalities)
            if (municipality.TenantId != snapshot.TenantId || !TenantContext.IsValidId(municipality.Id) || !Text(municipality.Name)
                || !GeoGeofencing.IsValidPolygon(municipality.Polygon)) throw new InvalidDataException("Invalid municipal polygon.");
        foreach (var ward in snapshot.Wards)
        {
            var municipality = snapshot.Municipalities.FirstOrDefault(value => value.Id == ward.MunicipalityId);
            if (ward.TenantId != snapshot.TenantId || !TenantContext.IsValidId(ward.Id) || !Text(ward.Name) || !GeoGeofencing.IsValidPolygon(ward.Polygon)
                || municipality is null || !GeoGeofencing.ContainsPolygon(municipality.Polygon, ward.Polygon))
                throw new InvalidDataException("Ward must be a valid polygon inside its municipality.");
        }
        var now = _time.GetUtcNow();
        foreach (var position in snapshot.CrewPositions)
            if (position.TenantId != snapshot.TenantId || !position.Synthetic || !_demo && !allowRetainedSynthetic
                || !TenantContext.IsValidId(position.CrewId) || !GeoGeofencing.IsValidPoint(position.Location)
                || position.LastSeenAt > now || position.Status is not ("ready" or "available" or "unavailable" or "off-shift"))
                throw new InvalidDataException("Only explicit-demo synthetic latest crew positions are permitted; live tracking is disabled.");
        foreach (var hazard in snapshot.Hazards)
            if (hazard.TenantId != snapshot.TenantId || !TenantContext.IsValidId(hazard.Id) || hazard.EventType is null || !GeoPrivacyGuards.HazardEventTypes.Contains(hazard.EventType)
                || !Text(hazard.Title) || !GeoGeofencing.IsValidPoint(hazard.Location) || !double.IsFinite(hazard.Confidence) || hazard.Confidence is < 0 or > 1
                || hazard.ExclusionRadiusMeters is < 0 or > 2000 || hazard.OccurredAt > now || hazard.OccurredAt < now - HazardRetention
                && !allowRetainedSynthetic || hazard.CameraId is not null && !TenantContext.IsValidId(hazard.CameraId))
                throw new InvalidDataException("Hazards must be bounded allow-listed object/situation events; no imagery or identification is accepted.");
        foreach (var asset in snapshot.Assets)
            if (asset.TenantId != snapshot.TenantId || !TenantContext.IsValidId(asset.Id) || !Text(asset.Name) || asset.AssetType is not ("substation" or "reservoir" or "depot")
                || !GeoGeofencing.IsValidPoint(asset.Location)) throw new InvalidDataException("Invalid infrastructure asset.");
    }
    private static string StateHash(GeoSnapshot snapshot) => IncidentStore.Hash(JsonSerializer.Serialize(new
    { snapshot.TenantId, snapshot.Version, snapshot.Municipalities, snapshot.Wards, snapshot.CrewPositions, snapshot.Hazards, snapshot.Assets }));
    private static string RecordHash(GeoAuditRecord record) => IncidentStore.Hash(JsonSerializer.Serialize(record with { Hash = "" }));
    public void Dispose() => _gate.Dispose();
}

public sealed class GeoConflictException(string message) : InvalidOperationException(message);
public sealed class GeoCaseIntegrityException(string message) : InvalidOperationException(message);

public sealed class GeoRetentionService(GeoStore store, IncidentStore incidents, TenantStore tenants, TimeProvider time, ILogger<GeoRetentionService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(LogLevel.Error,
        new EventId(4402, "MapRetentionFailed"), "Map retention purge failed; inspect authenticated geo state.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var tenant in await tenants.ListAsync(stoppingToken))
                {
                    using var partition = TenantContext.Use(tenant.Id); await store.PurgeExpiredAsync(stoppingToken);
                    await incidents.PurgeLocationFieldsAsync(time.GetUtcNow() - GeoStore.ReportLocationRetention, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { Failed(logger, null); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
