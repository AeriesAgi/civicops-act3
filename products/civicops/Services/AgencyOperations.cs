using System.Collections.Frozen;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CivicOps.Models;

namespace CivicOps.Services;

/// <summary>Event-only incident detector contract. Implementations deliver allow-listed scene events; no imagery or identification ever crosses it.</summary>
public interface IIncidentDetector
{
    IEnumerable<DetectionEventRequest> Generate(int seed, int count, DateTimeOffset now);
}

/// <summary>Deterministic synthetic traffic-camera feed for demos and tests. It never reads a camera.</summary>
public sealed class SimulatedCameraDetector : IIncidentDetector
{
    private static readonly (double Lon, double Lat)[] Points = [(31.021, -29.858), (31.003, -29.871), (30.985, -29.915), (31.040, -29.840), (30.960, -29.890)];
    public IEnumerable<DetectionEventRequest> Generate(int seed, int count, DateTimeOffset now)
    {
        var random = new Random(seed); var types = AgencyGuards.DetectionEventTypes.Order(StringComparer.Ordinal).ToArray();
        for (var i = 0; i < count; i++)
        {
            var point = Points[random.Next(Points.Length)];
            yield return new DetectionEventRequest($"sim-{seed}-{i}", types[random.Next(types.Length)], Math.Round(0.55 + random.NextDouble() * 0.44, 2),
                $"SIM-CAM-{random.Next(1, 40):D2}", new GeoPoint(point.Lon, point.Lat), now.AddMinutes(-random.Next(0, 30)));
        }
    }
}

public static class AgencyGuards
{
    /// <summary>Reviewed allow-list. Scene events only; identity, biometric and plate events are deliberately absent and cannot be added at runtime.</summary>
    public static readonly IReadOnlySet<string> DetectionEventTypes = new[]
    { "crash", "stalled_vehicle", "flooding", "blocked_lane", "debris", "fire_smoke", "signal_outage" }.ToFrozenSet(StringComparer.Ordinal);
    public static readonly IReadOnlyDictionary<string, string[]> SuggestedAgencies = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["crash"] = ["traffic", "ems", "fire"], ["stalled_vehicle"] = ["traffic"], ["flooding"] = ["disaster", "roads"],
        ["blocked_lane"] = ["traffic", "roads"], ["debris"] = ["roads", "traffic"], ["fire_smoke"] = ["fire"], ["signal_outage"] = ["traffic", "electricity"]
    }.ToFrozenDictionary(StringComparer.Ordinal);
    public const double LowConfidenceThreshold = 0.7;
    public const string DetectionNotice = "SIMULATED camera events. Scene events only: no imagery, faces, plates or identities are accepted or stored. A human confirms every event before any incident exists; detections never dispatch.";
    public const string BoardNotice = "Cross-agency board. Humans assign the incident commander, decide every mutual-aid request and every police or safety action. Restricted agency details are redacted for staff outside that agency.";
}

public sealed class AgencyRequestException(int status, string message) : Exception(message) { public int Status { get; } = status; }

/// <summary>Tenant-bound multi-agency incidents, mutual aid and detection review. Records human decisions only.</summary>
public sealed class AgencyStore(IConfiguration configuration, IWebHostEnvironment environment, DurableStorage storage, TimeProvider time, IIncidentDetector detector) : IDisposable
{
    public void Dispose() => gate.Dispose();
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly Lazy<AgencyCatalogue> LoadedCatalogue = new(LoadCatalogue);
    public static AgencyCatalogue Catalogue => LoadedCatalogue.Value;
    public static readonly string[] FullDetailRoles = ["supervisor", "admin", "auditor"];

    private static AgencyCatalogue LoadCatalogue()
    {
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "catalog", "agencies.v1.json"), Path.Combine(Directory.GetCurrentDirectory(), "catalog", "agencies.v1.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "products", "civicops", "catalog", "agencies.v1.json") })
            if (File.Exists(path))
            {
                var catalogue = JsonSerializer.Deserialize<AgencyCatalogue>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Agency catalogue is empty.");
                var ids = catalogue.Agencies.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
                if (catalogue.Version != 1 || ids.Count != catalogue.Agencies.Count || catalogue.IncidentTypes.Any(t => !ids.Contains(t.Agency))
                    || catalogue.RoleMappings.Any(m => !ids.Contains(m.Agency) || !PermissionMatrix.Roles.Contains(m.BaseRole)))
                    throw new InvalidDataException("Invalid agency catalogue.");
                return catalogue;
            }
        throw new InvalidDataException("Versioned agency catalogue is missing.");
    }

    private string Dir => TenantContext.DirectoryFor(DataPaths.Resolve(configuration, environment));
    private async Task<T> Read<T>(string file, Func<T> empty, CancellationToken ct)
    { var payload = await storage.ReadAsync(Path.Combine(Dir, file), ct); return string.IsNullOrWhiteSpace(payload) ? empty() : JsonSerializer.Deserialize<T>(payload, Json) ?? empty(); }
    private Task Write<T>(string file, T value, CancellationToken ct) => storage.WriteAsync(Path.Combine(Dir, file), JsonSerializer.Serialize(value, Json), ct);
    private async Task<TResult> Locked<TResult>(Func<Task<TResult>> action, CancellationToken ct)
    { await gate.WaitAsync(ct); try { return await action(); } finally { gate.Release(); } }

    private static void Text(string? value, string name, int max, int min = 1)
    { if (value is null || value.Trim().Length < min || value.Length > max) throw new AgencyRequestException(400, $"{name} must be {min}-{max} characters."); }
    private static void Operation(string? id) => Text(id, "operationId", 128);

    public async Task<IReadOnlyList<AgencyView>> AgenciesAsync(CancellationToken ct)
    {
        var disabled = await Read("agency-settings.json", () => new HashSet<string>(StringComparer.Ordinal), ct);
        return Catalogue.Agencies.Select(a => new AgencyView(a.Id, a.Name, a.Group, a.Colour, a.Icon, a.Restricted, !disabled.Contains(a.Id), a.RoleNames)).ToArray();
    }
    public Task<AgencyView> ToggleAsync(string id, AgencyToggleRequest request, string actor, CancellationToken ct) => Locked(async () =>
    {
        Operation(request.OperationId); Text(request.Note, "note", 500);
        if (Catalogue.Agencies.All(a => a.Id != id)) throw new AgencyRequestException(404, "Unknown agency.");
        var disabled = await Read("agency-settings.json", () => new HashSet<string>(StringComparer.Ordinal), ct);
        if (request.Enabled) disabled.Remove(id); else disabled.Add(id);
        await Write("agency-settings.json", disabled, ct);
        return (await AgenciesAsync(ct)).Single(a => a.Id == id);
    }, ct);

    private Task<List<AgencyIncident>> Incidents(CancellationToken ct) => Read("agency-incidents.json", () => new List<AgencyIncident>(), ct);

    public static bool CanSeeRestricted(ClaimsPrincipal user, AgencyIncident incident) =>
        FullDetailRoles.Any(user.IsInRole) || incident.Agencies.Where(IsRestricted).All(a => user.HasClaim("civicops:agency", a));
    private static bool IsRestricted(string agency) => Catalogue.Agencies.Any(a => a.Id == agency && a.Restricted);
    public static AgencyIncident View(AgencyIncident incident, ClaimsPrincipal user) => !incident.Restricted || CanSeeRestricted(user, incident) ? incident
        : incident with { Summary = "Restricted agency details. Visible to that agency, supervisors and auditors only.", Location = null, AddressOrLandmark = null,
            Timeline = incident.Timeline.Select(t => t with { Note = null }).ToArray(), Redacted = true };

    public async Task<AgencyBoard> BoardAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var all = (await Incidents(ct)).Where(i => i.TenantId == TenantContext.CurrentId).OrderByDescending(i => i.CreatedAt).ToArray();
        return new AgencyBoard(all.Select(i => View(i, user)).ToArray(), all.Count(i => i.Status == AgencyIncidentStatus.Open), all.Count(i => i.Status == AgencyIncidentStatus.Active),
            all.Sum(i => i.MutualAid.Count(m => m.Status == MutualAidStatus.Requested)), AgencyGuards.BoardNotice);
    }
    public async Task<AgencyIncident?> GetAsync(string id, ClaimsPrincipal user, CancellationToken ct) =>
        (await Incidents(ct)).FirstOrDefault(i => i.Id == id && i.TenantId == TenantContext.CurrentId) is { } incident ? View(incident, user) : null;

    public Task<AgencyIncident> CreateAsync(AgencyIncidentCreateRequest request, string actor, CancellationToken ct) => Locked(() => CreateCoreAsync(request, actor, null, ct), ct);
    private async Task<AgencyIncident> CreateCoreAsync(AgencyIncidentCreateRequest request, string actor, string? detectionId, CancellationToken ct)
    {
        Operation(request.OperationId); Text(request.Title, "title", 160, 3); Text(request.Summary, "summary", 2000, 3);
        var type = Catalogue.IncidentTypes.FirstOrDefault(t => t.Code == request.TypeCode) ?? throw new AgencyRequestException(400, "Unknown agency incident type.");
        var enabled = (await AgenciesAsync(ct)).Where(a => a.Enabled).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var agencies = request.Agencies.Append(type.Agency).Distinct(StringComparer.Ordinal).ToArray();
        if (agencies.Length > 9 || agencies.Any(a => !enabled.Contains(a))) throw new AgencyRequestException(400, "Every agency must exist and be enabled for this municipality.");
        if (request.LinkedCaseIds is { Count: > 20 }) throw new AgencyRequestException(400, "At most 20 linked cases.");
        var values = await Incidents(ct);
        var existing = values.FirstOrDefault(i => i.TenantId == TenantContext.CurrentId && i.Timeline.Any(t => t.Action == "created" && t.Note == "operation:" + request.OperationId));
        if (existing is not null) return existing;
        var now = time.GetUtcNow();
        var incident = new AgencyIncident("AI-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), TenantContext.CurrentId, request.Title.Trim(), type.Code, agencies,
            request.Summary.Trim(), request.Location, request.AddressOrLandmark, request.LinkedCaseIds ?? [], AgencyIncidentStatus.Open, type.DefaultPriority,
            null, null, detectionId, [], [new("created", actor, "operation:" + request.OperationId, now)], agencies.Any(IsRestricted), false, now, true);
        values.Add(incident); await Write("agency-incidents.json", values, ct);
        return incident;
    }

    private Task<AgencyIncident> Mutate(string id, string operationId, string action, string actor, string? note, Func<AgencyIncident, DateTimeOffset, AgencyIncident> change, CancellationToken ct) => Locked(async () =>
    {
        Operation(operationId);
        var values = await Incidents(ct);
        var index = values.FindIndex(i => i.Id == id && i.TenantId == TenantContext.CurrentId);
        if (index < 0) throw new AgencyRequestException(404, "Unknown incident.");
        var marker = "operation:" + operationId;
        if (values[index].Timeline.Any(t => t.Note?.StartsWith(marker, StringComparison.Ordinal) is true)) return values[index];
        if (values[index].Status == AgencyIncidentStatus.Closed) throw new AgencyRequestException(409, "Incident is closed.");
        var now = time.GetUtcNow(); var next = change(values[index], now);
        values[index] = next with { Timeline = next.Timeline.Append(new AgencyTimelineEntry(action, actor, marker + (note is null ? "" : " " + note), now)).ToArray() };
        await Write("agency-incidents.json", values, ct);
        return values[index];
    }, ct);

    public Task<AgencyIncident> CommanderAsync(string id, AgencyCommanderRequest request, string actor, CancellationToken ct)
    {
        Text(request.Commander, "commander", 120); Text(request.Note, "note", 500);
        if (!Catalogue.IcsRoles.Contains(request.IcsRole)) throw new AgencyRequestException(400, "Unknown ICS role.");
        return Mutate(id, request.OperationId, "commander-assigned", actor, request.Commander + " as " + request.IcsRole + ": " + request.Note,
            (i, _) => i with { Commander = request.Commander.Trim(), CommanderIcsRole = request.IcsRole, Status = i.Status == AgencyIncidentStatus.Open ? AgencyIncidentStatus.Active : i.Status }, ct);
    }
    public Task<AgencyIncident> StatusAsync(string id, AgencyStatusRequest request, string actor, CancellationToken ct)
    { Text(request.Note, "note", 500); return Mutate(id, request.OperationId, "status-" + request.Status.ToString().ToLowerInvariant(), actor, request.Note, (i, _) => i with { Status = request.Status }, ct); }

    public Task<AgencyIncident> MutualAidAsync(string id, MutualAidCreateRequest request, string actor, CancellationToken ct)
    {
        Text(request.Resources, "resources", 300); Text(request.Note, "note", 500);
        if (Catalogue.Agencies.All(a => a.Id != request.FromAgency) || Catalogue.Agencies.All(a => a.Id != request.ToAgency)) throw new AgencyRequestException(400, "Unknown agency.");
        if (request.ToTenantId is not null && (!TenantContext.IsValidId(request.ToTenantId) || request.ToTenantId == TenantContext.CurrentId)) throw new AgencyRequestException(400, "Neighbouring municipality id is invalid.");
        if (request.ToTenantId is null && request.FromAgency == request.ToAgency) throw new AgencyRequestException(400, "Mutual aid within a municipality must target another agency.");
        return Mutate(id, request.OperationId, "mutual-aid-requested", actor, request.FromAgency + " -> " + (request.ToTenantId is null ? "" : request.ToTenantId + "/") + request.ToAgency,
            (i, now) =>
            {
                if (!i.Agencies.Contains(request.FromAgency)) throw new AgencyRequestException(400, "The requesting agency must be part of the incident.");
                return i with { MutualAid = i.MutualAid.Append(new MutualAidRequestRecord("MA-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), request.FromAgency, request.ToAgency,
                    request.ToTenantId, request.Resources.Trim(), request.Scope, MutualAidStatus.Requested, actor, now, null, null, null)).ToArray() };
            }, ct);
    }
    public Task<AgencyIncident> DecideAsync(string id, string requestId, MutualAidDecisionRequest request, string actor, CancellationToken ct)
    {
        Text(request.Note, "note", 500);
        return Mutate(id, request.OperationId, request.Accept ? "mutual-aid-accepted" : "mutual-aid-declined", actor, requestId + ": " + request.Note, (i, now) =>
        {
            var aid = i.MutualAid.FirstOrDefault(m => m.Id == requestId) ?? throw new AgencyRequestException(404, "Unknown mutual-aid request.");
            if (aid.Status != MutualAidStatus.Requested) throw new AgencyRequestException(409, "Mutual-aid request is already decided.");
            var decided = aid with { Status = request.Accept ? MutualAidStatus.Accepted : MutualAidStatus.Declined, DecidedBy = actor, DecidedAt = now, DecisionNote = request.Note.Trim() };
            var agencies = request.Accept && aid.ToTenantId is null ? i.Agencies.Append(aid.ToAgency).Distinct(StringComparer.Ordinal).ToArray() : i.Agencies;
            return i with { MutualAid = i.MutualAid.Select(m => m.Id == requestId ? decided : m).ToArray(), Agencies = agencies, Restricted = agencies.Any(IsRestricted) };
        }, ct);
    }

    private Task<List<DetectionEvent>> Detections(CancellationToken ct) => Read("detections.json", () => new List<DetectionEvent>(), ct);
    public async Task<DetectionFeed> FeedAsync(CancellationToken ct) => new(
        (await Detections(ct)).Where(d => d.TenantId == TenantContext.CurrentId).OrderByDescending(d => d.ObservedAt).Take(200).ToArray(),
        AgencyGuards.DetectionEventTypes.Order(StringComparer.Ordinal).ToArray(), AgencyGuards.DetectionNotice);

    public Task<DetectionEvent> IngestAsync(DetectionEventRequest request, CancellationToken ct) => Locked(() => IngestCoreAsync(request, ct), ct);
    private async Task<DetectionEvent> IngestCoreAsync(DetectionEventRequest request, CancellationToken ct)
    {
        Operation(request.OperationId); Text(request.CameraId, "cameraId", 64);
        if (!AgencyGuards.DetectionEventTypes.Contains(request.EventType)) throw new AgencyRequestException(400, "Event type is not on the reviewed scene-event allow-list.");
        if (request.Confidence is < 0 or > 1 || double.IsNaN(request.Confidence)) throw new AgencyRequestException(400, "confidence must be between 0 and 1.");
        if (request.Location.Latitude is < -90 or > 90 || request.Location.Longitude is < -180 or > 180) throw new AgencyRequestException(400, "Invalid location.");
        var id = "DT-" + IncidentStore.Hash(TenantContext.CurrentId + ":" + request.OperationId)[..10].ToUpperInvariant();
        var values = await Detections(ct);
        if (values.FirstOrDefault(d => d.Id == id) is { } existing) return existing;
        var detection = new DetectionEvent(id, TenantContext.CurrentId, request.EventType, request.Confidence, request.CameraId.Trim(), request.Location, request.ObservedAt,
            DetectionStatus.Proposed, request.Confidence < AgencyGuards.LowConfidenceThreshold, AgencyGuards.SuggestedAgencies[request.EventType], null, null, null, true);
        values.Add(detection); if (values.Count > 5000) values = values[^5000..];
        await Write("detections.json", values, ct);
        return detection;
    }
    public async Task<DetectionFeed> SimulateAsync(DetectionSimulateRequest request, CancellationToken ct)
    {
        Operation(request.OperationId);
        if (request.Count is < 1 or > 25) throw new AgencyRequestException(400, "count must be 1-25.");
        foreach (var item in detector.Generate(request.Seed, request.Count, time.GetUtcNow())) await IngestAsync(item, ct);
        return await FeedAsync(ct);
    }
    public Task<DetectionEvent> DecideDetectionAsync(string id, bool confirm, DetectionDecisionRequest request, string actor, CancellationToken ct) => Locked(async () =>
    {
        Operation(request.OperationId); Text(request.Note, "note", 500);
        var values = await Detections(ct);
        var index = values.FindIndex(d => d.Id == id && d.TenantId == TenantContext.CurrentId);
        if (index < 0) throw new AgencyRequestException(404, "Unknown detection.");
        var detection = values[index];
        if (detection.Status != DetectionStatus.Proposed) return detection.DecisionNote == request.Note ? detection : throw new AgencyRequestException(409, "Detection is already decided.");
        string? incidentId = null;
        if (confirm)
        {
            var type = detection.EventType switch
            {
                "crash" => "TRAFFIC.CRASH_SCENE", "fire_smoke" => "FIRE.VELD_FIRE", "flooding" => "DISASTER.FLOOD_EVACUATION",
                "signal_outage" => "TRAFFIC.SIGNAL_OUTAGE_POINT_DUTY", _ => "ROADS.DEBRIS_CLEARANCE"
            };
            var incident = await CreateCoreAsync(new AgencyIncidentCreateRequest("detection-" + id, "Confirmed " + detection.EventType.Replace('_', ' ') + " at " + detection.CameraId, type,
                request.Agencies ?? detection.SuggestedAgencies, "Human-confirmed simulated scene event: " + request.Note.Trim(), detection.Location, null), actor, id, ct);
            incidentId = incident.Id;
        }
        values[index] = detection with { Status = confirm ? DetectionStatus.Confirmed : DetectionStatus.Dismissed, IncidentId = incidentId, DecidedBy = actor, DecisionNote = request.Note };
        await Write("detections.json", values, ct);
        return values[index];
    }, ct);
}

public static class AgencyEndpoints
{
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, Converters = { new JsonStringEnumConverter() } };
    public static IServiceCollection AddCivicOpsAgencies(this IServiceCollection services)
    { services.AddSingleton<IIncidentDetector, SimulatedCameraDetector>(); services.AddSingleton<AgencyStore>(); return services; }
    private static T Read<T>(JsonElement payload)
    { if (payload.ValueKind != JsonValueKind.Object) throw new JsonException(); return payload.Deserialize<T>(Strict) ?? throw new JsonException(); }
    private static string Actor(HttpContext http) => http.User.Identity?.Name ?? "Authenticated municipal operator";

    public static IEndpointRouteBuilder MapCivicOpsAgencyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").RequireRateLimiting("dispatch");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (AgencyRequestException error) { return Results.Problem(error.Message, statusCode: error.Status); }
            catch (JsonException) { return Results.BadRequest(new { error = "Payload must match the closed schema. Imagery, identification, biometric and plate fields are rejected." }); }
        });
        group.MapGet("/agencies", async (AgencyStore store, CancellationToken ct) => Results.Ok(await store.AgenciesAsync(ct))).RequireAuthorization("operations-read");
        group.MapGet("/agencies/catalogue", () => Results.Ok(AgencyStore.Catalogue)).RequireAuthorization("workforce-read");
        group.MapPut("/agencies/{id}", async (string id, JsonElement payload, HttpContext http, AgencyStore store, CancellationToken ct) =>
            Results.Ok(await store.ToggleAsync(id, Read<AgencyToggleRequest>(payload), Actor(http), ct))).RequireAuthorization("supervise");
        group.MapGet("/agency-incidents", async (HttpContext http, AgencyStore store, CancellationToken ct) => Results.Ok(await store.BoardAsync(http.User, ct))).RequireAuthorization("operations-read");
        group.MapPost("/agency-incidents", async (JsonElement payload, HttpContext http, AgencyStore store, CancellationToken ct) =>
            Results.Ok(AgencyStore.View(await store.CreateAsync(Read<AgencyIncidentCreateRequest>(payload), Actor(http), ct), http.User))).RequireAuthorization("dispatch");
        group.MapGet("/agency-incidents/{id}", async (string id, HttpContext http, AgencyStore store, CancellationToken ct) =>
            await store.GetAsync(id, http.User, ct) is { } incident ? Results.Ok(incident) : Results.NotFound()).RequireAuthorization("operations-read");
        group.MapPost("/agency-incidents/{id}/commander", async (string id, JsonElement payload, HttpContext http, AgencyStore store, CancellationToken ct) =>
            Results.Ok(AgencyStore.View(await store.CommanderAsync(id, Read<AgencyCommanderRequest>(payload), Actor(http), ct), http.User))).RequireAuthorization("supervise");
        group.MapPost("/agency-incidents/{id}/status", async (string id, JsonElement payload, HttpContext http, AgencyStore store, CancellationToken ct) =>
            Results.Ok(AgencyStore.View(await store.StatusAsync(id, Read<AgencyStatusRequest>(payload), Actor(http), ct), http.User))).RequireAuthorization("dispatch");
        group.MapPost("/agency-incidents/{id}/mutual-aid", async (string id, JsonElement payload, HttpContext http, AgencyStore store, CancellationToken ct) =>
            Results.Ok(AgencyStore.View(await store.MutualAidAsync(id, Read<MutualAidCreateRequest>(payload), Actor(http), ct), http.User))).RequireAuthorization("dispatch");
        group.MapPost("/agency-incidents/{id}/mutual-aid/{requestId}/decision", async (string id, string requestId, JsonElement payload, HttpContext http, AgencyStore store, CancellationToken ct) =>
            Results.Ok(AgencyStore.View(await store.DecideAsync(id, requestId, Read<MutualAidDecisionRequest>(payload), Actor(http), ct), http.User))).RequireAuthorization("supervise");
        group.MapGet("/detections", async (AgencyStore store, CancellationToken ct) => Results.Ok(await store.FeedAsync(ct))).RequireAuthorization("operations-read");
        group.MapPost("/detections", async (JsonElement payload, AgencyStore store, CancellationToken ct) =>
            Results.Ok(await store.IngestAsync(Read<DetectionEventRequest>(payload), ct))).RequireAuthorization("dispatch");
        group.MapPost("/detections/simulate", async (JsonElement payload, AgencyStore store, CancellationToken ct) =>
            Results.Ok(await store.SimulateAsync(Read<DetectionSimulateRequest>(payload), ct))).RequireAuthorization("dispatch");
        group.MapPost("/detections/{id}/confirm", async (string id, JsonElement payload, HttpContext http, AgencyStore store, CancellationToken ct) =>
            Results.Ok(await store.DecideDetectionAsync(id, true, Read<DetectionDecisionRequest>(payload), Actor(http), ct))).RequireAuthorization("dispatch");
        group.MapPost("/detections/{id}/dismiss", async (string id, JsonElement payload, HttpContext http, AgencyStore store, CancellationToken ct) =>
            Results.Ok(await store.DecideDetectionAsync(id, false, Read<DetectionDecisionRequest>(payload), Actor(http), ct))).RequireAuthorization("dispatch");
        return app;
    }
}
