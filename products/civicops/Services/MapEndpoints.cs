using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CivicOps.Models;
using Microsoft.AspNetCore.Mvc;

namespace CivicOps.Services;

public static class MapEndpoints
{
    private static readonly string[] LayerNames = ["municipalities", "wards", "cases", "crews", "hazards", "assets"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter() } };
    private static readonly Action<ILogger, string, string, string, int, Exception?> AccessLog = LoggerMessage.Define<string, string, string, int>(LogLevel.Information,
        new EventId(4401, "MapAccess"), "Authorized map access by {Actor} in {TenantId} to {MapRoute}: {FeatureCount} features; coordinate and narrative values omitted.");
    public static IServiceCollection AddCivicOpsMap(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(MapOptions.From(configuration)); services.AddSingleton<GeoStore>();
        services.AddSingleton<GeoScenarioSeeder>(); services.AddHostedService<GeoRetentionService>(); return services;
    }
    public static IEndpointRouteBuilder MapCivicOpsMapEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/map").WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (GeoCaseIntegrityException)
            {
                return Results.Problem("Verified incident positions are unavailable. A supervisor must investigate audit integrity before using the map.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        group.MapGet("/config", async (HttpContext http, MapOptions options, GeoStore geo, DispatcherAccess access, ILogger<GeoStore> logger, CancellationToken ct) =>
        {
            var snapshot = await geo.ReadAsync(ct);
            var outlinePoints = snapshot.Municipalities.SelectMany(value => value.Polygon.Coordinates).SelectMany(ring => ring).ToArray();
            var bounds = outlinePoints.Length == 0 ? GeoGeofencing.SouthAfricaBounds.ToArray() : new[]
            { Math.Clamp(outlinePoints.Min(point => point[0]) - 0.012, 16, 33), Math.Clamp(outlinePoints.Min(point => point[1]) - 0.012, -35, -22),
                Math.Clamp(outlinePoints.Max(point => point[0]) + 0.012, 16, 33), Math.Clamp(outlinePoints.Max(point => point[1]) + 0.012, -35, -22) };
            if (access.DemoMode && snapshot.Municipalities.All(value => value.Synthetic)) bounds = GeoScenario.Bounds;
            Log(http, access, logger, 0);
            return Results.Ok(new
            {
                tileUrl = options.TileEnabled ? options.TileUrl : "none", options.TileEnabled, options.Attribution, options.CrewStaleMinutes, geoVersion = snapshot.Version,
                bounds, center = new[] { (bounds[1] + bounds[3]) / 2, (bounds[0] + bounds[2]) / 2 }, demoMode = access.DemoMode,
                synthetic = snapshot.Municipalities.Count > 0 && snapshot.Municipalities.All(value => value.Synthetic), privacyNotice = "Residents choose report locations. Crew positions require an active shift, explicit revocable consent and visible sharing indicator; latest positions are available only to supervisors. Demo positions are synthetic. No biometric identification or person scoring.",
                tileMode = options.TileEnabled ? "configured; subject to tile-server availability and usage policy" : "offline outlines",
                heatmap = "Aggregated incident/hazard grid only; no raw person locations."
            });
        });
        group.MapGet("/layers", async (HttpContext http, GeoStore geo, IncidentStore incidents, WorkforceStore workforce,
            WorkOrderCatalog catalog, TimeProvider time, DispatcherAccess access, CrewStore crewStore, ILogger<GeoStore> logger, CancellationToken ct) =>
        {
            var error = Query(http.Request, out var bbox, out var layers, out var since, out var page, out var limit);
            if (error is not null) return Results.BadRequest(new { error });
            var snapshot = await geo.ReadAsync(ct); var cases = await incidents.ListVerifiedAsync(ct); var now = time.GetUtcNow();
            var features = new List<GeoFeature>();
            if (layers.Contains("municipalities", StringComparer.Ordinal))
                features.AddRange(snapshot.Municipalities.Select(value => Polygon(value.Id, value.Polygon, new
                { layer = "municipalities", value.Id, value.Name, value.Synthetic })));
            if (layers.Contains("wards", StringComparer.Ordinal)) features.AddRange(Wards(snapshot, cases, catalog, now, access.DemoMode));
            if (layers.Contains("cases", StringComparer.Ordinal))
                features.AddRange(cases.Where(value => value.Location is not null && value.LocationShared && value.CreatedAt >= now - GeoStore.ReportLocationRetention && (!value.Synthetic || access.DemoMode)
                    && (since is null || value.UpdatedAt >= since)).Select(value => Point("case-" + value.Id, value.Location!, new
                    {
                        layer = "cases", value.Id, value.ReferenceNumber, title = catalog.Find(value.WorkOrderTypeCode)?.Title ?? value.Category,
                        value.Priority, status = value.Status.ToString(), value.Department, value.WardId, value.MunicipalityId,
                        slaBreached = Breached(value, catalog, now), value.CreatedAt, value.UpdatedAt, value.Version, value.Synthetic,
                        value.NeedsHumanReview, humanApprovalRecorded = value.Approval is not null,
                        detailUrl = "/api/dispatch/incidents/" + value.Id, locationProvenance = value.LocationProvenance
                    })));
            if (layers.Contains("crews", StringComparer.Ordinal))
            {
                var crews = await workforce.ListCrewsAsync(ct); var workers = await workforce.ListWorkersAsync(ct);
                var positions = await CrewPositions(http, snapshot, crewStore, ct);
                features.AddRange(positions.Where(value => since is null || value.LastSeenAt >= since)
                    .Join(crews, position => position.CrewId, crew => crew.Id, (position, crew) => Point("crew-" + crew.Id, position.Location, new
                    {
                        layer = "crews", crew.Id, crew.Name, crew.Department, status = position.Status,
                        crew.Availability, crew.Shift, position.LastSeenAt, position.Synthetic,
                        onShift = position.Status == "on-shift / sharing", sharing = position.Status == "on-shift / sharing",
                        skills = workers.Where(worker => worker.CrewId == crew.Id).SelectMany(worker => worker.Skills).Select(skill => skill.Code).Distinct().Order().Take(64).ToArray(),
                        detailUrl = "/api/v1/map/crews/" + crew.Id
                    })));
            }
            if (layers.Contains("hazards", StringComparer.Ordinal))
                features.AddRange(snapshot.Hazards.Where(value => since is null || value.OccurredAt >= since).Select(value => Point(value.Id, value.Location, new
                { layer = "hazards", value.Id, value.Title, value.EventType, value.ExclusionRadiusMeters, value.Confidence, value.OccurredAt, value.CameraId, value.Synthetic,
                    provenance = value.Synthetic ? "Synthetic exercise; no camera feed" : "Structured object/situation event reviewed by an authorized human; no image retained" })));
            if (layers.Contains("assets", StringComparer.Ordinal)) features.AddRange(snapshot.Assets.Select(value => Point(value.Id, value.Location, new
            { layer = "assets", value.Id, value.Name, value.AssetType, value.Synthetic })));
            var filtered = features.Where(feature => InBounds(feature, bbox)).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
            var selected = filtered.Skip((page - 1) * limit).Take(limit).ToArray();
            var result = new GeoFeatureCollection("FeatureCollection", selected, filtered.Length,
                page * limit < filtered.Length ? page + 1 : null, now, access.DemoMode);
            Log(http, access, logger, selected.Length); return Conditional(http, result,
                new { TenantContext.CurrentId, layers, bbox, since, page, limit, result.Features, result.Total, result.NextPage });
        });
        group.MapGet("/wards", async (HttpContext http, GeoStore geo, IncidentStore incidents, WorkOrderCatalog catalog,
            TimeProvider time, DispatcherAccess access, ILogger<GeoStore> logger, CancellationToken ct) =>
        {
            var now = time.GetUtcNow(); var snapshot = await geo.ReadAsync(ct);
            var features = Wards(snapshot, await incidents.ListVerifiedAsync(ct), catalog, now, access.DemoMode).ToArray();
            var result = new GeoFeatureCollection("FeatureCollection", features, features.Length, null, now, access.DemoMode);
            Log(http, access, logger, features.Length); return Conditional(http, result, new { TenantContext.CurrentId, result.Features });
        });
        group.MapGet("/heatmap", async (HttpContext http, GeoStore geo, IncidentStore incidents, TimeProvider time,
            DispatcherAccess access, ILogger<GeoStore> logger, CancellationToken ct) =>
        {
            var metric = http.Request.Query["metric"].ToString(); if (metric.Length == 0) metric = "cases";
            var window = http.Request.Query["window"].ToString(); if (window.Length == 0) window = "24h";
            if (metric is not ("cases" or "hazards") || window is not ("24h" or "7d"))
                return Results.BadRequest(new { error = "metric must be cases|hazards and window must be 24h|7d." });
            var now = time.GetUtcNow();
            var points = metric == "cases" ? (await incidents.ListVerifiedAsync(ct)).Where(value => value.Location is not null && value.LocationShared
                    && value.CreatedAt >= now - GeoStore.ReportLocationRetention && (!value.Synthetic || access.DemoMode)).Select(value => new GeoGridPoint(value.Location!, value.CreatedAt))
                : (await geo.ReadAsync(ct)).Hazards.Select(value => new GeoGridPoint(value.Location, value.OccurredAt));
            var features = GeoHeatAggregation.Aggregate(points, now, window == "24h" ? TimeSpan.FromHours(24) : TimeSpan.FromDays(7))
                .Select((cell, index) => Point("grid-" + index, cell.Location, new { layer = "heatmap", cell.Count, cell.Weight, metric,
                    cellSizeDegrees = GeoHeatAggregation.CellSizeDegrees, aggregated = true })).ToArray();
            var result = new GeoFeatureCollection("FeatureCollection", features, features.Length, null, now, access.DemoMode);
            Log(http, access, logger, features.Length); return Conditional(http, result, new { TenantContext.CurrentId, metric, window, result.Features });
        });
        group.MapGet("/crews/{id}", async (HttpContext http, string id, GeoStore geo, WorkforceStore workforce,
            DispatcherAccess access, CrewStore crewStore, ILogger<GeoStore> logger, CancellationToken ct) =>
        {
            var crew = (await workforce.ListCrewsAsync(ct)).FirstOrDefault(value => value.Id == id);
            if (crew is null) return Results.NotFound();
            var position = (await CrewPositions(http, await geo.ReadAsync(ct), crewStore, ct)).FirstOrDefault(value => value.CrewId == id);
            var workers = (await workforce.ListWorkersAsync(ct)).Where(value => value.CrewId == id).ToArray();
            Log(http, access, logger, position is null ? 0 : 1);
            return Results.Ok(new
            {
                crew, position, synthetic = position?.Synthetic is true,
                qualifications = workers.SelectMany(value => value.Skills).GroupBy(value => value.Code).Select(group => new { code = group.Key, level = group.Max(value => value.Level) }),
                certifications = workers.SelectMany(value => value.Certifications).Distinct().Select(value => new
                { value.Code, value.IssuedAt, value.ExpiresAt, value.Source, synthetic = value.EvidenceRef.StartsWith("synthetic", StringComparison.Ordinal) }),
                privacy = "Team qualification codes and latest consented on-shift position for supervisors (synthetic positions in demo); individual workers and raw trails are omitted."
            });
        });
        group.MapGet("/cases/{id}/nearest-crews", async (HttpContext http, string id, GeoStore geo, IncidentStore incidents,
            CrewEligibilityService eligibility, MapOptions options, TimeProvider time, DispatcherAccess access, CrewStore crewStore, ILogger<GeoStore> logger, CancellationToken ct) =>
        {
            var incident = await incidents.GetVerifiedAsync(id, ct); if (incident is null) return Results.NotFound();
            if (incident.Location is null || !incident.LocationShared || incident.CreatedAt < time.GetUtcNow() - GeoStore.ReportLocationRetention) return Results.BadRequest(new { error = "A current voluntarily shared case location is required for a distance suggestion." });
            var candidates = await Candidates(http, incident, geo, crewStore, eligibility, ct);
            var suggestions = GeoNearestCrewRanking.Rank(incident.Location, candidates, time.GetUtcNow(), TimeSpan.FromMinutes(options.CrewStaleMinutes));
            Log(http, access, logger, suggestions.Count);
            return Results.Ok(new { caseId = incident.Id, caseVersion = incident.Version, humanApprovalRequired = true,
                requiresCaseApproval = incident.Approval is null || incident.NeedsHumanReview, suggestions,
                distanceBasis = "Straight-line distance to a fresh latest position; no road ETA or travel-time promise.",
                emptyReason = suggestions.Count == 0 ? "No crew has fresh consent-safe position, availability and all current skills/certificates. Round 9 crew positions exist only in explicit demo." : null });
        });
        group.MapPost("/cases/{id}/dispatch", async (JsonElement payload, HttpContext http, string id, GeoStore geo, IncidentStore incidents,
            IncidentCoordinator coordinator, CrewEligibilityService eligibility, MapOptions options, TimeProvider time, CrewStore crewStore,
            DispatcherAccess access, CancellationToken ct) =>
        {
            var request = Read<MapDispatchRequest>(payload, ["crewId", "note", "expectedVersion"], out var error);
            if (error is not null) return Results.BadRequest(new { error });
            error = RequestValidation.Action(null, request!.Note, request.ExpectedVersion)
                ?? RequestValidation.Text(request.Note, "note", 2000, 5) ?? RequestValidation.Text(request.CrewId, "crewId", 64, 1);
            if (error is not null) return Results.BadRequest(new { error });
            var incident = await incidents.GetVerifiedAsync(id, ct); if (incident is null) return Results.NotFound();
            if (HighHazard(incident, http) && !http.User.IsInRole("supervisor") && !http.User.IsInRole("admin"))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (incident.Version != request.ExpectedVersion) return Results.Conflict(new { error = "This case changed. Refresh the suggestion before confirming." });
            if (incident.Approval is null || incident.NeedsHumanReview) return Results.BadRequest(new { error = "Review and approve the case through the case workflow before confirming dispatch." });
            if (incident.Location is null || !incident.LocationShared || incident.CreatedAt < time.GetUtcNow() - GeoStore.ReportLocationRetention) return Results.BadRequest(new { error = "A current voluntarily shared case location is required." });
            var suggestions = GeoNearestCrewRanking.Rank(incident.Location, await Candidates(http, incident, geo, crewStore, eligibility, ct), time.GetUtcNow(), TimeSpan.FromMinutes(options.CrewStaleMinutes), 10);
            var chosen = suggestions.FirstOrDefault(value => value.CrewId == request.CrewId);
            if (chosen is null) return Results.BadRequest(new { error = "Crew is no longer a fresh, available, fully qualified distance suggestion. Refresh and review." });
            return Changed(await coordinator.AssignAsync(id, new(request.CrewId, access.Identity(http),
                $"Human confirmed nearest-qualified suggestion ({chosen.DistanceKm:F2} km straight-line). Reason: {request.Note}", request.ExpectedVersion), access.Identity(http), ct));
        }).WithMetadata(new RequestSizeLimitAttribute(8192));
        group.MapPost("/cases/{id}/ward-override", async (JsonElement payload, HttpContext http, string id, GeoStore geo,
            IncidentStore incidents, DispatcherAccess access, TimeProvider time, CancellationToken ct) =>
        {
            var request = Read<MapWardOverrideRequest>(payload, ["municipalityId", "wardId", "note", "expectedVersion"], out var error);
            if (error is not null) return Results.BadRequest(new { error });
            error = RequestValidation.Action(null, request!.Note, request.ExpectedVersion) ?? RequestValidation.Text(request.Note, "note", 2000, 5);
            if (error is not null) return Results.BadRequest(new { error });
            var snapshot = await geo.ReadAsync(ct);
            if (!snapshot.Wards.Any(value => value.Id == request.WardId && value.MunicipalityId == request.MunicipalityId))
                return Results.BadRequest(new { error = "Choose a ward in this municipality's configured map boundaries." });
            return Changed(await incidents.ChangeAsync(id, request.ExpectedVersion, incident =>
            {
                var prior = $"{incident.MunicipalityId ?? "unassigned"}/{incident.WardId ?? "unassigned"}";
                incident.MunicipalityId = request.MunicipalityId; incident.WardId = request.WardId;
                incident.Audit.Add(new("geo.ward-overridden", access.Identity(http),
                    $"{prior} -> {request.MunicipalityId}/{request.WardId}. Human reason: {request.Note}", time.GetUtcNow())); return null;
            }, ct));
        }).WithMetadata(new RequestSizeLimitAttribute(8192));
        group.MapPost("/hazards", async (JsonElement payload, HttpContext http, GeoStore geo, IncidentStore incidents, DurableStorage storage,
            DispatcherAccess access, TimeProvider time, CancellationToken ct) =>
        {
            var request = Read<MapHazardRequest>(payload,
                ["id", "eventType", "title", "location", "confidence", "exclusionRadiusMeters", "occurredAt", "cameraId", "reason"], out var error);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                return await storage.RunAsync<IResult>(async () =>
                {
                    var snapshot = await geo.AddHazardAsync(request!, access.Identity(http), ct);
                    await incidents.RecordSystemEventAsync(new("geo.hazard-reviewed", access.Identity(http),
                        $"Structured {request!.EventType} event {request.Id} reviewed. Reason: {request.Reason}. No imagery or identification stored.", time.GetUtcNow()), ct);
                    return Results.Created("/api/v1/map/layers?layers=hazards", new { hazardId = request.Id, snapshot.Version, humanReviewed = true });
                }, ct);
            }
            catch (GeoConflictException conflict) { return Results.Conflict(new { error = conflict.Message }); }
            catch (InvalidDataException invalid) { return Results.BadRequest(new { error = invalid.Message }); }
        }).WithMetadata(new RequestSizeLimitAttribute(8192));
        group.MapPut("/geographies", async (JsonElement payload, HttpContext http, GeoStore geo, IncidentStore incidents, DurableStorage storage,
            DispatcherAccess access, TimeProvider time, CancellationToken ct) =>
        {
            var request = Read<MapGeographiesRequest>(payload, ["expectedVersion", "municipalities", "wards", "reason"], out var error);
            if (error is not null) return Results.BadRequest(new { error });
            try
            {
                return await storage.RunAsync<IResult>(async () =>
                {
                    var snapshot = await geo.SetGeographiesAsync(request!, access.Identity(http), ct);
                    await incidents.RecordSystemEventAsync(new("geo.boundaries-reviewed", access.Identity(http),
                        $"Map boundary revision {snapshot.Version} reviewed. Reason: {request!.Reason}; new shared-coordinate cases use these polygons.", time.GetUtcNow()), ct);
                    return Results.Ok(new { snapshot.Version, municipalityCount = snapshot.Municipalities.Count, wardCount = snapshot.Wards.Count });
                }, ct);
            }
            catch (GeoConflictException conflict) { return Results.Conflict(new { error = conflict.Message }); }
            catch (InvalidDataException invalid) { return Results.BadRequest(new { error = invalid.Message }); }
        }).WithMetadata(new RequestSizeLimitAttribute(262144));
        group.MapPost("/demo/seed", async (HttpContext http, DispatcherAccess access, GeoScenarioSeeder seeder, CancellationToken ct) =>
            !access.DemoMode ? Results.NotFound() : Results.Ok(await seeder.SeedAsync(access.Identity(http), ct)));
        group.MapGet("/stream", Stream);
        return app;
    }
    private static bool HighHazard(Incident incident, HttpContext http) => incident.Priority == "Urgent"
        || http.RequestServices.GetRequiredService<WorkOrderCatalog>().Find(incident.WorkOrderTypeCode)?.HazardClass is "high" or "critical";
    private static async Task<IReadOnlyList<GeoCrewPosition>> CrewPositions(HttpContext http, GeoSnapshot snapshot, CrewStore crewStore, CancellationToken ct)
    {
        var latest = http.User.IsInRole("supervisor") || http.User.IsInRole("admin") ? await crewStore.PositionsAsync(ct) : [];
        return snapshot.CrewPositions.Where(value => latest.All(current => current.CrewId != value.CrewId)).Concat(latest).ToArray();
    }
    private static async Task<IReadOnlyList<GeoCrewCandidate>> Candidates(HttpContext http, Incident incident, GeoStore geo, CrewStore crewStore, CrewEligibilityService eligibility, CancellationToken ct)
    {
        var positions = await CrewPositions(http, await geo.ReadAsync(ct), crewStore, ct); var assessed = await eligibility.ListAsync(incident, ct);
        return assessed.Join(positions, value => value.Crew.Id, value => value.CrewId, (value, position) => new GeoCrewCandidate(value.Crew, position.Location,
            position.LastSeenAt, value.Status, value.CanAssign,
            value.Qualifications.SelectMany(worker => worker.Skills).GroupBy(skill => skill.Code)
                .Select(group => $"{group.Key}: skill level {group.Max(skill => skill.Level)}")
                .Concat(value.Qualifications.SelectMany(worker => worker.Certifications).Distinct().Select(cert => $"{cert.Code}: valid to {cert.ExpiresAt:yyyy-MM-dd}; {cert.Source}; synthetic evidence is unverified"))
                .Take(64).ToArray(), position.Synthetic)).ToArray();
    }
    private static IEnumerable<GeoFeature> Wards(GeoSnapshot snapshot, IReadOnlyList<Incident> cases, WorkOrderCatalog catalog, DateTimeOffset now, bool demo) => snapshot.Wards.Select(value => Polygon(value.Id, value.Polygon, new
    {
        layer = "wards", value.Id, value.Name, value.MunicipalityId, value.Synthetic,
        openCases = cases.Count(incident => (!incident.Synthetic || demo) && (incident.Location is null || incident.CreatedAt >= now - GeoStore.ReportLocationRetention)
            && incident.WardId == value.Id && incident.Status is not (IncidentStatus.Closed or IncidentStatus.Resolved)),
        slaBreaches = cases.Count(incident => (!incident.Synthetic || demo) && (incident.Location is null || incident.CreatedAt >= now - GeoStore.ReportLocationRetention)
            && incident.WardId == value.Id && Breached(incident, catalog, now))
    }));
    private static bool Breached(Incident value, WorkOrderCatalog catalog, DateTimeOffset now) => value.Status is not (IncidentStatus.Resolved or IncidentStatus.Closed)
        && value.CreatedAt.AddMinutes(catalog.Find(value.WorkOrderTypeCode)?.SlaTargetMinutes ?? 1440) < now;
    private static GeoFeature Point(string id, GeoPoint location, object properties) => new("Feature", id, new("Point", new[] { location.Longitude, location.Latitude }), properties);
    private static GeoFeature Polygon(string id, GeoPolygon polygon, object properties) => new("Feature", id, new("Polygon", polygon.Coordinates), properties);
    private static bool InBounds(GeoFeature feature, double[] bbox)
    {
        if (feature.Geometry.Type == "Point") { var point = (double[])feature.Geometry.Coordinates; return point[0] >= bbox[0] && point[0] <= bbox[2] && point[1] >= bbox[1] && point[1] <= bbox[3]; }
        var points = ((double[][][])feature.Geometry.Coordinates).SelectMany(ring => ring).ToArray();
        return points.Min(point => point[0]) <= bbox[2] && points.Max(point => point[0]) >= bbox[0]
            && points.Min(point => point[1]) <= bbox[3] && points.Max(point => point[1]) >= bbox[1];
    }
    private static string? Query(HttpRequest request, out double[] bbox, out string[] layers, out DateTimeOffset? since, out int page, out int limit)
    {
        bbox = GeoGeofencing.SouthAfricaBounds.ToArray(); layers = LayerNames; since = null; page = 1; limit = 500;
        if (request.Query.Any(pair => pair.Value.Count != 1)) return "Map parameters must appear once.";
        var rawBounds = request.Query["bbox"].ToString();
        if (rawBounds.Length > 0)
        {
            var parts = rawBounds.Split(','); bbox = new double[4];
            if (parts.Length != 4) return "bbox must be west,south,east,north.";
            for (var index = 0; index < 4; index++)
                if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out bbox[index]) || !double.IsFinite(bbox[index])) return "bbox must contain finite numbers.";
            if (bbox[0] >= bbox[2] || bbox[1] >= bbox[3] || !GeoGeofencing.IsValidPoint(new(bbox[0], bbox[1])) || !GeoGeofencing.IsValidPoint(new(bbox[2], bbox[3])))
                return "bbox must be a non-crossing rectangle inside the South African bounds.";
        }
        var rawLayers = request.Query["layers"].ToString();
        if (rawLayers.Length > 0)
        {
            layers = rawLayers.Split(',');
            if (layers.Length > LayerNames.Length || layers.Distinct(StringComparer.Ordinal).Count() != layers.Length || layers.Any(value => !LayerNames.Contains(value, StringComparer.Ordinal)))
                return "layers must be unique names from municipalities,wards,cases,crews,hazards,assets.";
        }
        var rawSince = request.Query["since"].ToString();
        if (rawSince.Length > 0)
        {
            if (!DateTimeOffset.TryParseExact(rawSince, ["O", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return "since must be an ISO-8601 timestamp.";
            since = parsed;
        }
        if (request.Query.TryGetValue("page", out var rawPage) && (!int.TryParse(rawPage, CultureInfo.InvariantCulture, out page) || page is < 1 or > 10000)) return "page must be 1–10000.";
        if (request.Query.TryGetValue("limit", out var rawLimit) && (!int.TryParse(rawLimit, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 500)) return "limit must be 1–500.";
        return null;
    }
    private static IResult Conditional(HttpContext http, object result, object authenticatedState)
    {
        var etag = "\"" + IncidentStore.Hash(JsonSerializer.Serialize(authenticatedState, Json)) + "\""; http.Response.Headers.ETag = etag;
        return http.Request.Headers.IfNoneMatch.Any(header => header?.Split(',').Any(value => value.Trim() == etag || value.Trim() == "*") == true)
            ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Json(result, contentType: "application/geo+json");
    }
    private static void Log(HttpContext http, DispatcherAccess access, ILogger logger, int count) =>
        AccessLog(logger, access.Identity(http), TenantContext.CurrentId, http.GetEndpoint()?.DisplayName ?? "map", count, null);
    private static T? Read<T>(JsonElement payload, string[] fields, out string? error)
    {
        error = null;
        if (payload.ValueKind != JsonValueKind.Object) { error = "A bounded schema object is required."; return default; }
        var keys = payload.EnumerateObject().Select(value => value.Name).ToArray();
        if (keys.Length != fields.Length || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || fields.Any(field => !keys.Contains(field, StringComparer.Ordinal)))
        { error = "Provide every schema field exactly once; unknown identification, imagery and tracking fields are rejected."; return default; }
        try { return payload.Deserialize<T>(Json); }
        catch (JsonException) { error = "Payload does not match the map schema."; return default; }
    }
    private static IResult Changed(ChangeResult result) => result.Incident is null ? Results.NotFound()
        : result.Conflict ? Results.Conflict(new { error = result.Error }) : result.Error is not null ? Results.BadRequest(new { error = result.Error })
        : Results.Ok(new { incident = IncidentView.Dispatcher(result.Incident) });
    private static async Task Stream(HttpContext http, CaseEventStream events, DispatcherAccess access, ILogger<GeoStore> logger)
    {
        if (!events.TrySubscribe(out var subscription)) { http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
        http.Response.ContentType = "text/event-stream"; http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        Log(http, access, logger, 0);
        // Reconnect every five minutes so identity/session/tenant permissions are checked again.
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted); lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await http.Response.WriteAsync("event: ready\ndata: {}\n\n", lifetime.Token); await http.Response.Body.FlushAsync(lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await subscription.Reader.WaitToReadAsync(heartbeat.Token)) break;
                    // Coalesce all queued metadata; client fetches fresh bounded tenant-authenticated data.
                    while (subscription.Reader.TryRead(out _)) { }
                    await http.Response.WriteAsync("event: refresh\ndata: {}\n\n", lifetime.Token);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                { await http.Response.WriteAsync("event: heartbeat\ndata: {}\n\n", lifetime.Token); }
                await http.Response.Body.FlushAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { events.Unsubscribe(subscription.Id); }
    }
}

public sealed class GeoScenarioSeeder(GeoStore geo, IncidentStore incidents, WorkforceStore workforce,
    WorkOrderCatalog catalog, DispatcherAccess access, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<object> SeedAsync(string actor, CancellationToken ct)
    {
        if (!access.DemoMode) throw new InvalidOperationException("Map scenario is available only in explicit demo mode.");
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow(); var crews = (await workforce.ListCrewsAsync(ct)).ToList();
            var bases = IncidentWorkflow.Crews.Where(crew => catalog.WorkOrderTypes.Any(type => type.Department == crew.Department)).ToArray();
            for (var index = 0; crews.Count < 25; index++)
            {
                var basis = bases[index % bases.Length]; var id = $"map-{basis.Id}-{index / bases.Length + 2:00}";
                if (crews.Any(value => value.Id == id)) continue;
                var available = index % 11 == 10 ? "off-shift" : index % 9 == 8 ? "unavailable" : "available";
                var crew = await workforce.UpsertCrewAsync(new(id, $"{basis.Name} unit {index / bases.Length + 2:00} · SYNTHETIC", basis.Department,
                    basis.Capability + "; synthetic exercise qualifications only", [], "synthetic-evening", available), actor, ct);
                var types = catalog.WorkOrderTypes.Where(value => value.Department == crew.Department).ToArray();
                var skills = types.SelectMany(value => value.RequiredSkills).GroupBy(value => value.Code).Select(group => new WorkerSkill(group.Key, group.Max(value => value.MinimumLevel))).ToList();
                var certificates = types.SelectMany(value => value.RequiredCertifications).Distinct().Select(code =>
                {
                    var definition = catalog.CertificationTypes.First(value => value.Code == code); var issued = now.AddDays(-1);
                    return new WorkerCertification(code, issued, issued.AddMonths(definition.ValidityMonths), "Synthetic exercise supervisor — unverified", definition.IssuingBasis, "synthetic-map:" + crew.Id + ":" + code);
                }).ToList();
                await workforce.UpsertWorkerAsync(new("synthetic-" + id + "-a", "Synthetic exercise worker " + index, crew.Id, skills, certificates), actor, ct);
                crews.Add(crew);
            }
            crews = (await workforce.ListCrewsAsync(ct)).ToList(); var snapshot = await geo.SeedScenarioAsync(crews, actor, ct);
            var scenario = GeoScenario.Cases(TenantContext.CurrentId, now, catalog);
            foreach (var incident in scenario)
            {
                if (incident.Status is IncidentStatus.Assigned or IncidentStatus.InProgress)
                {
                    var crew = crews.Where(value => value.Department == incident.Department && value.Availability == "available").OrderByDescending(value => value.Id.StartsWith("map-", StringComparison.Ordinal)).FirstOrDefault();
                    incident.AssignedCrewId = crew?.Id; incident.AssignedCrewName = crew?.Name;
                }
            }
            var created = await incidents.CreateSyntheticScenarioAsync(scenario, ct);
            await incidents.RecordSystemEventAsync(new("geo.scenario-seeded", actor,
                "Human requested 120-case deterministic synthetic evening scenario; utility catalogue only, 25 simulated latest crew positions, 10 situation hazards. No external service or dispatch called.", now), ct);
            return new { count = 120, created, crewCount = snapshot.CrewPositions.Count, hazardCount = snapshot.Hazards.Count, simulated = true, synthetic = true, seed = GeoScenario.Seed };
        }
        finally { _gate.Release(); }
    }
    public void Dispose() => _gate.Dispose();
}
