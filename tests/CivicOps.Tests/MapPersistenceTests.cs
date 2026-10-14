using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class MapPersistenceTests
{
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task GeoStateSurvivesFreshStoreAndRejectsTamperingAndPartitionCopy(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = new TestApp(); using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<GeoStore>(); var storage = app.Services.GetRequiredService<DurableStorage>();
        await store.AddHazardAsync(Request(), "Synthetic test supervisor", default);
        var config = Configuration(app.DataDirectory, demo: true);
        using var restarted = new GeoStore(config, new SyntheticEnvironment(), storage, TimeProvider.System, new CaseEventStream());
        var loaded = await restarted.ReadAsync(default); Assert.Single(loaded.Hazards); Assert.Equal("flooding", loaded.Hazards[0].EventType);
        var path = Path.Combine(app.DataDirectory, "geo.json"); var original = await storage.ReadAsync(path, default);
        await storage.WriteAsync(Path.Combine(app.DataDirectory, "tenants", "synthetic-coast", "geo.json"), original!, default);
        using (TenantContext.Use("synthetic-coast")) await Assert.ThrowsAsync<InvalidDataException>(() => restarted.ReadAsync(default));
        var tampered = JsonNode.Parse(original!)!; tampered["hazards"]![0]!["confidence"] = 0.1;
        await storage.WriteAsync(path, tampered.ToJsonString(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.ReadAsync(default));
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ExpiredHazardsHideImmediatelyPurgePhysicallyAndCannotBlockNewWrites(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = new TestApp(); using var client = app.CreateClient();
        var now = DateTimeOffset.UtcNow; var clock = new MapClock(now); var storage = app.Services.GetRequiredService<DurableStorage>();
        using var store = new GeoStore(Configuration(app.DataDirectory, true), new SyntheticEnvironment(), storage, clock, new CaseEventStream());
        await store.AddHazardAsync(Request(now), "Synthetic test supervisor", default);
        clock.Now = now.AddDays(8);
        Assert.Empty((await store.ReadAsync(default)).Hazards);
        var before = JsonNode.Parse((await storage.ReadAsync(Path.Combine(app.DataDirectory, "geo.json"), default))!)!;
        Assert.Single(before["hazards"]!.AsArray());
        var newRequest = Request(clock.Now) with { Id = "synthetic-new-hazard" };
        var afterWrite = await store.AddHazardAsync(newRequest, "Synthetic test supervisor", default);
        Assert.Single(afterWrite.Hazards); Assert.Equal("synthetic-new-hazard", afterWrite.Hazards[0].Id);
        clock.Now = clock.Now.AddDays(8); Assert.Equal(1, await store.PurgeExpiredAsync(default));
        Assert.Empty((await store.ReadAsync(default)).Hazards);
        var physical = JsonNode.Parse((await storage.ReadAsync(Path.Combine(app.DataDirectory, "geo.json"), default))!)!;
        Assert.Empty(physical["hazards"]!.AsArray()); Assert.Equal("geo.retention-purged", physical["audit"]!.AsArray()[^1]!["action"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.AddHazardAsync(Request(now), "Synthetic test supervisor", default));
    }
    [Fact]
    public async Task ProductionReadSuppressesHistoricalSyntheticPointsAndCannotSeedNewOnes()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var storage = app.Services.GetRequiredService<DurableStorage>();
        var demo = app.Services.GetRequiredService<GeoStore>();
        await demo.SeedScenarioAsync(await app.Services.GetRequiredService<WorkforceStore>().ListCrewsAsync(default), "Synthetic test supervisor", default);
        using var production = new GeoStore(Configuration(app.DataDirectory, false), new SyntheticEnvironment(), storage, TimeProvider.System, new CaseEventStream());
        var result = await production.ReadAsync(default); Assert.Empty(result.CrewPositions); Assert.Empty(result.Hazards); Assert.Empty(result.Assets);
        await Assert.ThrowsAsync<InvalidOperationException>(() => production.SeedScenarioAsync([], "Synthetic test supervisor", default));
        Assert.Equal(16, await production.PurgeExpiredAsync(default));
    }
    [Fact]
    public async Task DefaultReportCoordinateRetentionPreservesCaseAndAuditedWorkflow()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var located = await MapEndpointTests.CreateLocated(client); var id = located.GetProperty("id").GetString()!;
        var store = app.Services.GetRequiredService<IncidentStore>();
        var before = await store.GetAsync(id, default);
        await store.ChangeAsync(id, before!.Version, incident =>
        {
            incident.CreatedAt = DateTimeOffset.UtcNow.AddDays(-31);
            incident.Audit.Add(new("synthetic.clock-adjusted", "Synthetic test supervisor", "Synthetic retention time fixture.", DateTimeOffset.UtcNow)); return null;
        }, default);
        var expired = await store.GetAsync(id, default); Assert.NotNull(expired!.Location);
        var projection = JsonSerializer.SerializeToElement(IncidentView.Dispatcher(expired)); Assert.Equal(JsonValueKind.Null, projection.GetProperty("location").ValueKind);
        Assert.Equal(1, await store.PurgeLocationFieldsAsync(DateTimeOffset.UtcNow - GeoStore.ReportLocationRetention, default));
        var after = await store.GetAsync(id, default); Assert.NotNull(after); Assert.Null(after.Location); Assert.False(after.LocationShared);
        Assert.Null(after.WardId); Assert.Null(after.MunicipalityId); Assert.Equal(before.Status, after.Status); Assert.Equal(before.Description, after.Description);
        Assert.Equal("geo.location-purged", after.Audit[^1].Action); Assert.DoesNotContain("longitude", after.Audit[^1].Detail);
        Assert.True((await store.VerifyAuditAsync(default)).Valid);
    }
    [Fact]
    public void WardContainmentRejectsConcaveBoundaryExcursionsAndMunicipalHoles()
    {
        GeoPolygon concave = new([[[30, -30], [31, -30], [31, -29], [30.7, -29], [30.7, -29.7], [30.3, -29.7], [30.3, -29], [30, -29], [30, -30]]]);
        GeoPolygon shortcut = new([[[30.2, -29.2], [30.8, -29.2], [30.8, -29.8], [30.2, -29.8], [30.2, -29.2]]]);
        Assert.True(GeoGeofencing.IsValidPolygon(concave)); Assert.True(GeoGeofencing.IsValidPolygon(shortcut));
        Assert.All(shortcut.Coordinates[0], point => Assert.True(GeoGeofencing.Contains(concave, new(point[0], point[1]))));
        Assert.False(GeoGeofencing.ContainsPolygon(concave, shortcut));
        GeoPolygon holeParent = new([[[30, -30], [31, -30], [31, -29], [30, -29], [30, -30]],
            [[30.4, -29.6], [30.6, -29.6], [30.6, -29.4], [30.4, -29.4], [30.4, -29.6]]]);
        GeoPolygon coversHole = new([[[30.1, -29.9], [30.9, -29.9], [30.9, -29.1], [30.1, -29.1], [30.1, -29.9]]]);
        Assert.False(GeoGeofencing.ContainsPolygon(holeParent, coversHole));
    }
    [Fact]
    public void EventStreamHasFiniteSubscriberCapacityAndReleasesSlots()
    {
        var stream = new CaseEventStream(); var subscriptions = Enumerable.Range(0, CaseEventStream.MaximumSubscribers).Select(_ => stream.Subscribe()).ToArray();
        Assert.False(stream.TrySubscribe(out _)); stream.Unsubscribe(subscriptions[0].Id); Assert.True(stream.TrySubscribe(out var replacement));
        foreach (var subscription in subscriptions) stream.Unsubscribe(subscription.Id); stream.Unsubscribe(replacement.Id);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task MapsRejectTamperedCaseCoordinatesWhileTheLegacyQueueRemainsInspectable(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var located = await MapEndpointTests.CreateLocated(client); var id = located.GetProperty("id").GetString();
        var storage = app.Services.GetRequiredService<DurableStorage>(); var path = Path.Combine(app.DataDirectory, "incidents.json");
        var snapshot = JsonNode.Parse((await storage.ReadAsync(path, default))!)!;
        snapshot["incidents"]![0]!["location"]!["longitude"] = 32.7; await storage.WriteAsync(path, snapshot.ToJsonString(), default);
        foreach (var route in new[] { "/api/v1/map/layers?layers=cases", "/api/v1/map/wards", "/api/v1/map/heatmap?metric=cases", $"/api/v1/map/cases/{id}/nearest-crews" })
        {
            using var response = await client.GetAsync(route); Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("32.7", body); Assert.DoesNotContain("longitude", body);
        }
        (await client.GetAsync("/api/incidents")).EnsureSuccessStatusCode();
        Assert.False((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
    }
    private static MapHazardRequest Request(DateTimeOffset? at = null) => new("synthetic-hazard", "flooding", "Synthetic structured situation", new(31, -29.9), 0.9, 50,
        (at ?? DateTimeOffset.UtcNow).AddMinutes(-1), null, "Synthetic test supervisor reviewed this situation");
    private static IConfiguration Configuration(string dataDirectory, bool demo) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["CIVICOPS_DATA_DIR"] = dataDirectory, ["CIVICOPS_DEMO_MODE"] = demo ? "true" : "false" }).Build();
    private sealed class MapClock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
}
