using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CivicOps.Services;

namespace CivicOps.Tests;

/// <summary>Round 13 multi-agency guards. All fixtures are synthetic.</summary>
public sealed class AgencyTests
{
    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpClient client, HttpMethod method, string path, object? payload = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = payload is null ? null : JsonContent.Create(payload) };
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
    private static Task<(HttpStatusCode Status, JsonElement Body)> Post(HttpClient c, string p, object o) => Send(c, HttpMethod.Post, p, o);
    private static Task<(HttpStatusCode Status, JsonElement Body)> Get(HttpClient c, string p) => Send(c, HttpMethod.Get, p);
    private static object Crash(string op, params string[] agencies) => new
    {
        operationId = op, title = "Synthetic crash with downed streetlight pole", typeCode = "TRAFFIC.CRASH_SCENE", agencies,
        summary = "Synthetic two-vehicle crash; pole down across one lane.", location = new { longitude = 31.02, latitude = -29.86 }, addressOrLandmark = "Synthetic Main Rd"
    };

    [Fact]
    public void CatalogueIsValidAdditiveAndCoversEveryRequiredAgency()
    {
        var catalogue = AgencyStore.Catalogue;
        foreach (var id in new[] { "electricity", "water", "roads", "waste", "metro-police", "traffic", "fire", "ems", "disaster" })
            Assert.Contains(catalogue.Agencies, a => a.Id == id);
        foreach (var code in new[] { "TRAFFIC.CRASH_SCENE", "TRAFFIC.SIGNAL_OUTAGE_POINT_DUTY", "METRO.PUBLIC_SAFETY_PATROL_REQUEST", "FIRE.STRUCTURE_FIRE", "FIRE.VELD_FIRE",
            "FIRE.VEHICLE_EXTRICATION", "EMS.MEDICAL_RESPONSE", "EMS.MASS_CASUALTY", "DISASTER.FLOOD_EVACUATION", "DISASTER.SHELTER_OPS" })
            Assert.Contains(catalogue.IncidentTypes, t => t.Code == code && t.Certifications.Count > 0 && t.Ppe.Count > 0 && t.SlaMinutes > 0);
        Assert.Contains(catalogue.RoleMappings, m => m.Role == "metro-police.dispatcher" && m.BaseRole == "dispatcher");
        Assert.Contains(catalogue.RoleMappings, m => m.Role == "fire.station-officer" && m.BaseRole == "supervisor");
        Assert.All(catalogue.RoleMappings, m => Assert.Contains(m.BaseRole, PermissionMatrix.Roles));
        Assert.Single(catalogue.Agencies, a => a.Restricted);
    }

    [Fact]
    public async Task OnlySupervisorsToggleAgenciesAndDisabledAgenciesCannotJoinIncidents()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var dispatcher = await RbacTests.RoleClient(app, "dispatcher");
        using var supervisor = await RbacTests.RoleClient(app, "supervisor");
        var toggle = new { operationId = "synthetic-toggle-1", enabled = false, note = "Synthetic: waste not onboarded." };
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(dispatcher, HttpMethod.Put, "/api/v1/agencies/waste", toggle)).Status);
        var off = await Send(supervisor, HttpMethod.Put, "/api/v1/agencies/waste", toggle);
        Assert.Equal(HttpStatusCode.OK, off.Status); Assert.False(off.Body.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(dispatcher, "/api/v1/agency-incidents", Crash("synthetic-disabled", "waste"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(supervisor, HttpMethod.Put, "/api/v1/agencies/unknown", toggle)).Status);
        var catalogue = await Get(dispatcher, "/api/v1/agencies/catalogue");
        Assert.Equal(HttpStatusCode.OK, catalogue.Status);
    }

    [Fact]
    public async Task CrossAgencyIncidentRunsCommanderMutualAidAndAuditedTimeline()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var dispatcher = await RbacTests.RoleClient(app, "dispatcher");
        using var supervisor = await RbacTests.RoleClient(app, "supervisor");
        var created = await Post(dispatcher, "/api/v1/agency-incidents", Crash("synthetic-crash-1", "ems", "fire", "electricity"));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        var id = created.Body.GetProperty("id").GetString()!;
        Assert.Equal(4, created.Body.GetProperty("agencies").GetArrayLength());
        var replay = await Post(dispatcher, "/api/v1/agency-incidents", Crash("synthetic-crash-1", "ems", "fire", "electricity"));
        Assert.Equal(id, replay.Body.GetProperty("id").GetString());

        var commander = new { operationId = "synthetic-cmd-1", commander = "Synthetic Station Officer A", icsRole = "Incident Commander", note = "Fire holds command." };
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(dispatcher, $"/api/v1/agency-incidents/{id}/commander", commander)).Status);
        var cmd = await Post(supervisor, $"/api/v1/agency-incidents/{id}/commander", commander);
        Assert.Equal("Active", cmd.Body.GetProperty("status").GetString());

        var aid = await Post(dispatcher, $"/api/v1/agency-incidents/{id}/mutual-aid", new
        { operationId = "synthetic-aid-1", fromAgency = "fire", toAgency = "fire", toTenantId = "neighbour-metro", resources = "One rescue pumper", scope = "Summary", note = "Synthetic extrication support." });
        Assert.Equal(HttpStatusCode.OK, aid.Status);
        var aidId = aid.Body.GetProperty("mutualAid")[0].GetProperty("id").GetString()!;
        var decision = new { operationId = "synthetic-aid-decision", accept = true, note = "Synthetic human acceptance." };
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(dispatcher, $"/api/v1/agency-incidents/{id}/mutual-aid/{aidId}/decision", decision)).Status);
        var decided = await Post(supervisor, $"/api/v1/agency-incidents/{id}/mutual-aid/{aidId}/decision", decision);
        Assert.Equal("Accepted", decided.Body.GetProperty("mutualAid")[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(supervisor, $"/api/v1/agency-incidents/{id}/mutual-aid/{aidId}/decision",
            new { operationId = "synthetic-aid-decision-2", accept = false, note = "Second decision." })).Status);

        var bad = await Post(dispatcher, $"/api/v1/agency-incidents/{id}/mutual-aid", new
        { operationId = "synthetic-aid-bad", fromAgency = "waste", toAgency = "fire", toTenantId = (string?)null, resources = "x", scope = "Summary", note = "Not in incident." });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);

        var closed = await Post(dispatcher, $"/api/v1/agency-incidents/{id}/status", new { operationId = "synthetic-close", status = "Closed", note = "Scene cleared." });
        var actions = closed.Body.GetProperty("timeline").EnumerateArray().Select(t => t.GetProperty("action").GetString()!).ToArray();
        Assert.Equal(new[] { "created", "commander-assigned", "mutual-aid-requested", "mutual-aid-accepted", "status-closed" }, actions);
        Assert.All(closed.Body.GetProperty("timeline").EnumerateArray(), t => Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("actor").GetString())));
        Assert.Equal(HttpStatusCode.Conflict, (await Post(dispatcher, $"/api/v1/agency-incidents/{id}/status", new { operationId = "synthetic-reopen", status = "Open", note = "Reopen." })).Status);
    }

    [Fact]
    public async Task MetroPoliceDetailsAreRedactedOutsideTheAgencyButVisibleToSupervisors()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var dispatcher = await RbacTests.RoleClient(app, "dispatcher");
        using var supervisor = await RbacTests.RoleClient(app, "supervisor");
        var created = await Post(dispatcher, "/api/v1/agency-incidents", new
        {
            operationId = "synthetic-patrol-1", title = "Synthetic patrol request", typeCode = "METRO.PUBLIC_SAFETY_PATROL_REQUEST", agencies = Array.Empty<string>(),
            summary = "Synthetic restricted operational detail.", location = new { longitude = 31.0, latitude = -29.9 }, addressOrLandmark = "Synthetic park"
        });
        Assert.True(created.Body.GetProperty("redacted").GetBoolean());
        var id = created.Body.GetProperty("id").GetString()!;
        var asDispatcher = await Get(dispatcher, "/api/v1/agency-incidents/" + id);
        Assert.DoesNotContain("Synthetic restricted operational detail", asDispatcher.Body.ToString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, asDispatcher.Body.GetProperty("location").ValueKind);
        var asSupervisor = await Get(supervisor, "/api/v1/agency-incidents/" + id);
        Assert.Contains("Synthetic restricted operational detail", asSupervisor.Body.ToString(), StringComparison.Ordinal);
        var board = await Get(dispatcher, "/api/v1/agency-incidents");
        Assert.DoesNotContain("Synthetic restricted operational detail", board.Body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncidentsAreTenantIsolated()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var a = await RbacTests.RoleClient(app, "supervisor");
        var created = await Post(a, "/api/v1/agency-incidents", Crash("synthetic-tenant-1"));
        var id = created.Body.GetProperty("id").GetString()!;
        using var b = await RbacTests.RoleClient(app, "supervisor", tenant: "synthetic-coast");
        Assert.Empty((await Get(b, "/api/v1/agency-incidents")).Body.GetProperty("incidents").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Get(b, "/api/v1/agency-incidents/" + id)).Status);
    }

    [Theory]
    [InlineData("face_match")]
    [InlineData("plate_read")]
    [InlineData("loitering")]
    [InlineData("person_tracking")]
    public async Task DetectorRejectsEventTypesOutsideTheSceneAllowList(string eventType)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var dispatcher = await RbacTests.RoleClient(app, "dispatcher");
        var result = await Post(dispatcher, "/api/v1/detections", new
        { operationId = "synthetic-det-" + eventType, eventType, confidence = 0.9, cameraId = "SIM-CAM-01", location = new { longitude = 31.0, latitude = -29.9 }, observedAt = DateTimeOffset.UtcNow });
        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Empty((await Get(dispatcher, "/api/v1/detections")).Body.GetProperty("events").EnumerateArray());
    }

    [Theory]
    [InlineData("image")]
    [InlineData("faceEmbedding")]
    [InlineData("licensePlate")]
    [InlineData("trackId")]
    public async Task DetectorRejectsAnyImageryOrIdentificationField(string field)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var dispatcher = await RbacTests.RoleClient(app, "dispatcher");
        var payload = new Dictionary<string, object> { ["operationId"] = "synthetic-field", ["eventType"] = "crash", ["confidence"] = 0.9, ["cameraId"] = "SIM-CAM-01",
            ["location"] = new { longitude = 31.0, latitude = -29.9 }, ["observedAt"] = DateTimeOffset.UtcNow, [field] = "synthetic" };
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(dispatcher, "/api/v1/detections", payload)).Status);
    }

    [Fact]
    public void AllowListIsReviewedAndImmutable()
    {
        Assert.Equal(["blocked_lane", "crash", "debris", "fire_smoke", "flooding", "signal_outage", "stalled_vehicle"], AgencyGuards.DetectionEventTypes.Order(StringComparer.Ordinal));
        if (AgencyGuards.DetectionEventTypes is ICollection<string> collection) Assert.Throws<NotSupportedException>(() => collection.Add("face_match"));
    }

    [Fact]
    public async Task SimulatedFeedIsDeterministicAndOnlyHumansTurnDetectionsIntoIncidents()
    {
        var detector = new SimulatedCameraDetector(); var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(detector.Generate(7, 5, now).ToArray(), detector.Generate(7, 5, now).ToArray());
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var dispatcher = await RbacTests.RoleClient(app, "dispatcher");
        using var auditor = await RbacTests.RoleClient(app, "auditor");
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(auditor, "/api/v1/detections/simulate", new { operationId = "synthetic-sim-a", seed = 7, count = 5 })).Status);
        var feed = await Post(dispatcher, "/api/v1/detections/simulate", new { operationId = "synthetic-sim", seed = 7, count = 5 });
        Assert.Equal(HttpStatusCode.OK, feed.Status);
        var events = feed.Body.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(5, events.Length);
        Assert.All(events, e => { Assert.Equal("Proposed", e.GetProperty("status").GetString()); Assert.True(e.GetProperty("simulated").GetBoolean()); });
        Assert.Empty((await Get(dispatcher, "/api/v1/agency-incidents")).Body.GetProperty("incidents").EnumerateArray());
        Assert.Contains("SIMULATED", feed.Body.GetProperty("notice").GetString(), StringComparison.Ordinal);

        var first = events[0].GetProperty("id").GetString()!; var second = events[1].GetProperty("id").GetString()!;
        var confirmed = await Post(dispatcher, $"/api/v1/detections/{first}/confirm", new { operationId = "synthetic-confirm", note = "Operator verified on synthetic feed." });
        Assert.Equal("Confirmed", confirmed.Body.GetProperty("status").GetString());
        var incidentId = confirmed.Body.GetProperty("incidentId").GetString()!;
        var incident = await Get(dispatcher, "/api/v1/agency-incidents/" + incidentId);
        Assert.Equal(first, incident.Body.GetProperty("detectionId").GetString());
        var dismissed = await Post(dispatcher, $"/api/v1/detections/{second}/dismiss", new { operationId = "synthetic-dismiss", note = "Synthetic false positive." });
        Assert.Equal("Dismissed", dismissed.Body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, dismissed.Body.GetProperty("incidentId").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(dispatcher, $"/api/v1/detections/{second}/confirm", new { operationId = "synthetic-late", note = "Different decision." })).Status);
        Assert.Single((await Get(dispatcher, "/api/v1/agency-incidents")).Body.GetProperty("incidents").EnumerateArray());
    }

    [Fact]
    public async Task LowConfidenceEventsAreFlagged()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var dispatcher = await RbacTests.RoleClient(app, "dispatcher");
        var low = await Post(dispatcher, "/api/v1/detections", new
        { operationId = "synthetic-low", eventType = "debris", confidence = 0.4, cameraId = "SIM-CAM-02", location = new { longitude = 31.0, latitude = -29.9 }, observedAt = DateTimeOffset.UtcNow });
        Assert.True(low.Body.GetProperty("lowConfidence").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(dispatcher, "/api/v1/detections", new
        { operationId = "synthetic-oob", eventType = "debris", confidence = 1.5, cameraId = "SIM-CAM-02", location = new { longitude = 31.0, latitude = -29.9 }, observedAt = DateTimeOffset.UtcNow })).Status);
    }
}
