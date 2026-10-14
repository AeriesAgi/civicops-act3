using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class MapEndpointTests
{
    [Theory]
    [InlineData("/api/v1/map/config")] [InlineData("/api/v1/map/layers")] [InlineData("/api/v1/map/wards")]
    [InlineData("/api/v1/map/heatmap")] [InlineData("/api/v1/map/stream")] [InlineData("/api/v1/map/crews/water-01")]
    [InlineData("/api/v1/map/cases/missing/nearest-crews")]
    public async Task NonDemoMapReadsRequireStaffAuthorization(string route)
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false"; using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
    }
    [Theory]
    [InlineData("bbox=31,-30,30,-29")] [InlineData("bbox=170,-30,-170,-29")] [InlineData("bbox=NaN,-30,31,-29")]
    [InlineData("bbox=31,-30,32")] [InlineData("layers=faces")] [InlineData("layers=cases,cases")]
    [InlineData("limit=501")] [InlineData("limit=0")] [InlineData("page=-1")] [InlineData("since=yesterday")]
    [InlineData("limit=10&limit=20")]
    public async Task InvalidLayerParametersFailClosed(string query)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/map/layers?" + query)).StatusCode);
    }
    [Fact]
    public async Task ReadOnlyDemoOutlinesHaveNoFabricatedCasesOrCrewLocations()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var result = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/layers");
        Assert.Equal(7, result.GetProperty("total").GetInt32());
        Assert.All(result.GetProperty("features").EnumerateArray(), feature => Assert.Equal("Polygon", feature.GetProperty("geometry").GetProperty("type").GetString()));
        Assert.False(File.Exists(Path.Combine(app.DataDirectory, "geo.json")));
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }
    [Fact]
    public async Task SharedCoordinateIsGeofencedWithoutExposingLocationThroughResidentLookup()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var incident = await CreateLocated(client); Assert.Equal("demo-metro", incident.GetProperty("municipalityId").GetString());
        Assert.Equal("demo-ward-04", incident.GetProperty("wardId").GetString());
        var id = incident.GetProperty("id").GetString();
        var resident = await client.GetStringAsync("/api/incidents/" + id);
        Assert.DoesNotContain("longitude", resident); Assert.DoesNotContain("latitude", resident); Assert.DoesNotContain("wardId", resident);
        var staff = await client.GetFromJsonAsync<JsonElement>("/api/dispatch/incidents/" + id);
        Assert.Equal(GeoScenario.Position(0.7, 0.5).Longitude, staff.GetProperty("location").GetProperty("longitude").GetDouble());
        var wards = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/wards");
        Assert.Equal(1, wards.GetProperty("features").EnumerateArray().Single(value => value.GetProperty("id").GetString() == "demo-ward-04").GetProperty("properties").GetProperty("openCases").GetInt32());
    }
    [Theory]
    [InlineData(false, true, 31, -29.9)] [InlineData(true, false, 31, -29.9)] [InlineData(true, true, 180, -29.9)]
    public async Task ReportLocationRequiresSharingConsentAndCountryBounds(bool sharing, bool consent, double longitude, double latitude)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic pipe leak by the hall", location = new { longitude, latitude }, shareLocation = sharing,
            privacyConsentAccepted = consent, privacyConsentVersion = PrivacyConsent.Version });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }
    [Fact]
    public async Task UnsharedNarrativeIsUnlocatedInsteadOfGuessedFromArea()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var created = await FoundationTests.Create(client);
        var stored = await app.Services.GetRequiredService<IncidentStore>().GetAsync(created.GetProperty("id").GetString()!, default);
        Assert.Null(stored!.Location); Assert.Null(stored.WardId); Assert.Null(stored.MunicipalityId);
        var points = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/layers?layers=cases"); Assert.Equal(0, points.GetProperty("total").GetInt32());
    }
    [Fact]
    public async Task LayersAreBoundedPagedConditionalAndFilteredByBoundingBoxAndSince()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var located = await CreateLocated(client);
        using var first = await client.GetAsync("/api/v1/map/layers?limit=2"); first.EnsureSuccessStatusCode();
        var collection = await first.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(2, collection.GetProperty("features").GetArrayLength()); Assert.Equal(2, collection.GetProperty("nextPage").GetInt32());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/map/layers?limit=2"); request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(request)).StatusCode);
        var outside = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/layers?layers=cases&bbox=17,-34,18,-33"); Assert.Equal(0, outside.GetProperty("total").GetInt32());
        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        var since = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/layers?layers=cases&since=" + future); Assert.Equal(0, since.GetProperty("total").GetInt32());
        var points = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/layers?layers=cases");
        Assert.Equal(located.GetProperty("id").GetString(), points.GetProperty("features")[0].GetProperty("properties").GetProperty("id").GetString());
    }
    [Theory]
    [InlineData("metric=people")] [InlineData("window=all")] [InlineData("metric=crews")]
    public async Task HeatmapCannotAggregatePeopleOrWorkerLocations(string query)
    {
        using var app = new TestApp(); using var client = app.CreateClient(); Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/map/heatmap?" + query)).StatusCode);
    }
    [Fact]
    public async Task EveryMapProjectionIsTenantBound()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var created = await CreateLocated(client);
        var reference = created.GetProperty("referenceNumber").GetString(); var id = created.GetProperty("id").GetString();
        foreach (var route in new[] { "/api/v1/map/layers", "/api/v1/map/heatmap", "/api/v1/map/wards" })
            Assert.DoesNotContain(reference!, await client.GetStringAsync("/m/coast" + route));
        var foreignPoints = await client.GetFromJsonAsync<JsonElement>("/m/coast/api/v1/map/layers?layers=cases"); Assert.Equal(0, foreignPoints.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/m/coast/api/v1/map/cases/{id}/nearest-crews")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/m/coast/api/v1/map/cases/{id}/dispatch", new { crewId = "water-01", note = "Synthetic foreign attempt", expectedVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/m/coast/api/v1/map/cases/{id}/ward-override", new { municipalityId = "demo-metro", wardId = "demo-ward-01", note = "Synthetic foreign override", expectedVersion = 1 })).StatusCode);
    }
    [Fact]
    public async Task ManualWardOverrideRecordsNamedHumanAndReason()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var created = await CreateLocated(client); var id = created.GetProperty("id").GetString();
        var response = await client.PostAsJsonAsync($"/api/v1/map/cases/{id}/ward-override", new
        { municipalityId = "demo-metro", wardId = "demo-ward-01", note = "Synthetic supervisor confirmed service jurisdiction", expectedVersion = created.GetProperty("version").GetInt32() });
        response.EnsureSuccessStatusCode(); var incident = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident");
        Assert.Equal("demo-ward-01", incident.GetProperty("wardId").GetString());
        var audit = incident.GetProperty("audit").EnumerateArray().Single(value => value.GetProperty("action").GetString() == "geo.ward-overridden");
        Assert.Contains("confirmed service jurisdiction", audit.GetProperty("detail").GetString()); Assert.Equal("Demo supervisor (not signed in, unverified)", audit.GetProperty("actor").GetString());
        Assert.True((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task WardOverrideRejectsStaleExplicitVersionAfterDeliveryAndAcceptsRefreshedVersion(string backend)
    {
        using var selected = StoreBackends.Use(backend);
        // Own delivery scheduling so both sides of the version conflict are deterministic.
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var created = await CreateLocated(client); var id = created.GetProperty("id").GetString()!;
        var store = app.Services.GetRequiredService<IncidentStore>();
        var baseline = (await store.GetAsync(id, default))!;
        var queue = app.Services.GetRequiredService<IntegrationOutbox>();
        Assert.Equal("simulated", (await queue.GetStatusAsync(default)).Mode);
        Assert.Equal(1, await queue.ProcessDueAsync(default));
        var delivered = (await store.GetAsync(id, default))!;
        Assert.Equal(baseline.Version + 1, delivered.Version);
        Assert.NotNull(delivered.ZendeskTicketId);
        Assert.Equal(baseline.WardId, delivered.WardId);
        Assert.Equal(baseline.NeedsHumanReview, delivered.NeedsHumanReview);
        Assert.Equal("case-record-synced", delivered.Audit.Last().Action);

        var note = "Synthetic supervisor confirmed jurisdiction after refreshing the changed case";
        using var stale = await client.PostAsJsonAsync($"/api/v1/map/cases/{id}/ward-override", new
        { municipalityId = "demo-metro", wardId = "demo-ward-01", note, expectedVersion = baseline.Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("Refresh", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        var refreshedCase = await client.GetFromJsonAsync<JsonElement>("/api/dispatch/incidents/" + id);
        Assert.Equal(delivered.Version, refreshedCase.GetProperty("version").GetInt32());
        var unchanged = (await store.GetAsync(id, default))!;
        Assert.Equal(JsonSerializer.Serialize(delivered), JsonSerializer.Serialize(unchanged));
        Assert.DoesNotContain(unchanged.Audit, entry => entry.Action == "geo.ward-overridden");

        using var refreshed = await client.PostAsJsonAsync($"/api/v1/map/cases/{id}/ward-override", new
        { municipalityId = "demo-metro", wardId = "demo-ward-01", note, expectedVersion = unchanged.Version });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var saved = (await store.GetAsync(id, default))!;
        Assert.Equal("demo-ward-01", saved.WardId);
        Assert.Equal(unchanged.Version + 1, saved.Version);
        Assert.Equal(delivered.NeedsHumanReview, saved.NeedsHumanReview);
        Assert.Equal(delivered.Status, saved.Status);
        Assert.Equal(delivered.Location, saved.Location);
        Assert.Equal(delivered.ZendeskTicketId, saved.ZendeskTicketId);
        Assert.Equal(delivered.Audit, saved.Audit.Take(delivered.Audit.Count));
        var audit = Assert.Single(saved.Audit, entry => entry.Action == "geo.ward-overridden");
        Assert.Equal("Demo supervisor (not signed in, unverified)", audit.Actor); Assert.Contains(note, audit.Detail);
        Assert.True((await store.VerifyAuditAsync(default)).Valid);
    }
    [Theory]
    [InlineData("face-recognition")] [InlineData("person-risk")] [InlineData("licence-plate-person")]
    public async Task RuntimeHazardWritesRejectProhibitedDetectorTypes(string type)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/map/hazards", Hazard(type))).StatusCode);
        Assert.Empty((await app.Services.GetRequiredService<GeoStore>().ReadAsync(default)).Hazards);
    }
    [Theory]
    [InlineData("faceEmbedding")] [InlineData("videoFrame")] [InlineData("voicePrint")]
    public async Task HazardSchemaRejectsExtraIdentificationOrImageryFields(string prohibited)
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var payload = JsonSerializer.SerializeToNode(Hazard("flooding"))!.AsObject(); payload[prohibited] = "synthetic-prohibited-payload";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/map/hazards", payload)).StatusCode);
    }
    [Fact]
    public async Task AllowedStructuredHazardPersistsWithoutImagesAndHasHumanAudit()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var response = await client.PostAsJsonAsync("/api/v1/map/hazards", Hazard("flooding"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var snapshot = await app.Services.GetRequiredService<GeoStore>().ReadAsync(default);
        Assert.Single(snapshot.Hazards); Assert.Equal("geo.hazard-reviewed", snapshot.Audit[^1].Action); Assert.Equal("Demo supervisor (not signed in, unverified)", snapshot.Audit[^1].Actor);
        Assert.DoesNotContain("videoFrame", JsonSerializer.Serialize(snapshot)); Assert.DoesNotContain("thumbnail", JsonSerializer.Serialize(snapshot));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/map/hazards", Hazard("flooding"))).StatusCode);
    }
    [Fact]
    public async Task SyntheticScenarioSeedsRealCasesQualifiedCrewPointsAndCanReplayWithoutDuplicatingCases()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/map/demo/seed", new { }); response.EnsureSuccessStatusCode();
        var seeded = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(120, seeded.GetProperty("created").GetInt32()); Assert.Equal(25, seeded.GetProperty("crewCount").GetInt32()); Assert.Equal(10, seeded.GetProperty("hazardCount").GetInt32());
        var cases = await app.Services.GetRequiredService<IncidentStore>().ListAsync(default); Assert.Equal(120, cases.Count); Assert.All(cases, value => Assert.True(value.Synthetic));
        var layers = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/layers"); Assert.Equal(168, layers.GetProperty("total").GetInt32());
        var replay = await client.PostAsJsonAsync("/api/v1/map/demo/seed", new { }); replay.EnsureSuccessStatusCode(); Assert.Equal(0, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetInt32());
        Assert.Equal(120, (await app.Services.GetRequiredService<IncidentStore>().ListAsync(default)).Count);
        var heat = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/heatmap?metric=cases&window=24h");
        Assert.Equal(95, heat.GetProperty("features").EnumerateArray().Sum(value => value.GetProperty("properties").GetProperty("count").GetInt32()));
        Assert.DoesNotContain("referenceNumber", heat.GetRawText()); Assert.DoesNotContain("crewId", heat.GetRawText());
        Assert.True((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
    }
    [Fact]
    public async Task DispatchSuggestionCannotApprovePendingCaseOrAcceptBlankHumanReason()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var created = await CreateLocated(client); var id = created.GetProperty("id").GetString();
        var pending = await client.PostAsJsonAsync($"/api/v1/map/cases/{id}/dispatch", new { crewId = "water-01", note = "Synthetic reviewed dispatch reason", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, pending.StatusCode);
        Assert.Null((await app.Services.GetRequiredService<IncidentStore>().GetAsync(id!, default))!.Approval);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/map/cases/{id}/dispatch", new { crewId = "water-01", note = "", expectedVersion = 1 })).StatusCode);
    }
    [Fact]
    public async Task NearestQualifiedDispatchUsesExistingApprovalHumanIdentityAndFreshVersion()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/map/demo/seed", new { })).EnsureSuccessStatusCode();
        var suggestions = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/cases/synthetic-map-0001/nearest-crews");
        Assert.False(suggestions.GetProperty("requiresCaseApproval").GetBoolean()); Assert.True(suggestions.GetProperty("humanApprovalRequired").GetBoolean());
        var first = suggestions.GetProperty("suggestions")[0]; Assert.Equal("eligible", first.GetProperty("status").GetString());
        Assert.NotEmpty(first.GetProperty("qualifications").EnumerateArray());
        var body = new { crewId = first.GetProperty("crewId").GetString(), note = "Synthetic supervisor confirms fresh qualification and safe access", expectedVersion = suggestions.GetProperty("caseVersion").GetInt32() };
        var assigned = await client.PostAsJsonAsync("/api/v1/map/cases/synthetic-map-0001/dispatch", body); assigned.EnsureSuccessStatusCode();
        var incident = (await assigned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident"); Assert.Equal("Assigned", incident.GetProperty("status").GetString());
        var audit = incident.GetProperty("audit").EnumerateArray().Last(value => value.GetProperty("action").GetString() == "assigned");
        Assert.Equal("Demo supervisor (not signed in, unverified)", audit.GetProperty("actor").GetString()); Assert.Contains("Human confirmed nearest-qualified", audit.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/map/cases/synthetic-map-0001/dispatch", body)).StatusCode);
    }
    [Theory]
    [InlineData("resident")] [InlineData("agent")] [InlineData("crew")] [InlineData("training-client")]
    public async Task RolesWithoutOperationsPermissionCannotReadTheMap(string role)
    {
        using var app = new TestApp(); using var client = await RbacTests.RoleClient(app, role, role == "crew" ? "water-01" : null);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/map/layers")).StatusCode);
    }
    [Fact]
    public async Task MapDispatchCannotBypassHighHazardSupervisorPolicy()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var initial = app.CreateClient(); var located = await CreateLocated(initial);
        var id = located.GetProperty("id").GetString()!; var store = app.Services.GetRequiredService<IncidentStore>();
        var changed = await store.ChangeAsync(id, located.GetProperty("version").GetInt32(), incident =>
        {
            incident.Priority = "Urgent"; incident.Audit.Add(new("synthetic.hazard-fixture", "Synthetic test supervisor", "Synthetic high hazard fixture.", DateTimeOffset.UtcNow)); return null;
        }, default);
        using var dispatcher = await RbacTests.RoleClient(app, "dispatcher");
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"/api/v1/map/cases/{id}/dispatch", new
        { crewId = "water-01", note = "Synthetic dispatcher attempts high hazard assignment", expectedVersion = changed.Incident!.Version })).StatusCode);
    }
    [Fact]
    public async Task AdminBoundaryRevisionUsesTrustedTenantAndOptimisticVersion()
    {
        using var app = new TestApp(); using var admin = await RbacTests.RoleClient(app, "admin");
        var boundaries = GeoScenario.Boundaries("foreign-body-tenant");
        var payload = new { expectedVersion = 0, municipalities = boundaries.Municipalities, wards = boundaries.Wards, reason = "Synthetic administrator reviewed fictional boundary import" };
        var saved = await admin.PutAsJsonAsync("/api/v1/map/geographies", payload); saved.EnsureSuccessStatusCode();
        Assert.Equal(1, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());
        var snapshot = await app.Services.GetRequiredService<GeoStore>().ReadAsync(default);
        Assert.All(snapshot.Municipalities, value => Assert.Equal("default", value.TenantId)); Assert.All(snapshot.Wards, value => Assert.Equal("default", value.TenantId));
        Assert.Equal("geo.boundaries-reviewed", snapshot.Audit[^1].Action);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/v1/map/geographies", payload)).StatusCode);
        using var dispatcher = await RbacTests.RoleClient(app, "dispatcher");
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PutAsJsonAsync("/api/v1/map/geographies", payload)).StatusCode);
    }
    [Fact]
    public async Task ConfiguredPolygonAtEnvelopeEdgesReturnsBoundsUsableByTheLayersApi()
    {
        using var app = new TestApp(); using var admin = await RbacTests.RoleClient(app, "admin");
        // A fictional rectangle exercises configured geography rather than the demo default;
        // these are the validation-envelope corners, not any actual municipal boundary.
        var municipality = new GeoMunicipality("synthetic-envelope-fixture", "SYNTHETIC envelope configuration test",
            new([[[16, -35], [33, -35], [33, -22], [16, -22], [16, -35]]]), false, "ignored-body-tenant");
        (await admin.PutAsJsonAsync("/api/v1/map/geographies", new
        { expectedVersion = 0, municipalities = new[] { municipality }, wards = Array.Empty<GeoWard>(), reason = "Synthetic administrator reviewed the envelope configuration fixture" })).EnsureSuccessStatusCode();
        var config = await admin.GetFromJsonAsync<JsonElement>("/api/v1/map/config");
        Assert.Equal(new[] { 16d, -35d, 33d, -22d }, config.GetProperty("bounds").EnumerateArray().Select(value => value.GetDouble()).ToArray());
        var bbox = string.Join(',', config.GetProperty("bounds").EnumerateArray().Select(value => value.GetRawText()));
        using var layers = await admin.GetAsync("/api/v1/map/layers?layers=municipalities&bbox=" + Uri.EscapeDataString(bbox));
        layers.EnsureSuccessStatusCode(); Assert.Equal(1, (await layers.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32());
    }
    [Fact]
    public async Task AuthorizedMapStreamUsesTenantBoundMetadataAndBoundedQueue()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        // Authenticate the cold tenant fixture within its own HTTP budget; each
        // expected event line still has a separate five-second deadline.
        client.Timeout = TimeSpan.FromSeconds(30);
        using var lifetime = new CancellationTokenSource();
        try
        {
            using var response = await client.GetAsync("/m/coast/api/v1/map/stream", HttpCompletionOption.ResponseHeadersRead, lifetime.Token); response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(lifetime.Token));
            async Task<string?> ReadLineAsync() => await reader.ReadLineAsync(lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("event: ready", await ReadLineAsync()); Assert.Equal("data: {}", await ReadLineAsync()); await ReadLineAsync();
            var events = app.Services.GetRequiredService<CaseEventStream>(); events.Publish(); using (TenantContext.Use("synthetic-coast")) events.Publish();
            Assert.Equal("event: refresh", await ReadLineAsync()); Assert.Equal("data: {}", await ReadLineAsync());
        }
        finally { lifetime.Cancel(); }
        var bounded = new CaseEventStream(); var subscription = bounded.Subscribe(); for (var index = 0; index < 1000; index++) bounded.Publish();
        var count = 0; while (subscription.Reader.TryRead(out var item)) { count++; Assert.Equal("refresh", item.Type); } Assert.Equal(16, count); bounded.Unsubscribe(subscription.Id);
    }
    private static object Hazard(string eventType) => new { id = "synthetic-reviewed-hazard", eventType, title = "Synthetic object situation exercise", location = new { longitude = 31.0, latitude = -29.9 },
        confidence = 0.9, exclusionRadiusMeters = 80, occurredAt = DateTimeOffset.UtcNow.AddMinutes(-2), cameraId = (string?)null, reason = "Synthetic supervisor reviewed structured event" };
    internal static async Task<JsonElement> CreateLocated(HttpClient client)
    {
        var created = await FoundationTests.Create(client, new
        {
            description = "Synthetic pipe leak by the fictional exercise hall", area = "Fictional Demo Metro site",
            location = GeoScenario.Position(0.7, 0.5), shareLocation = true, privacyConsentAccepted = true, privacyConsentVersion = PrivacyConsent.Version
        });
        return await client.GetFromJsonAsync<JsonElement>("/api/dispatch/incidents/" + created.GetProperty("id").GetString());
    }
}
