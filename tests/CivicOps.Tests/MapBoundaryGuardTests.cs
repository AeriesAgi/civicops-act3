using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class MapBoundaryGuardTests
{
    [Fact]
    public async Task ProductionWardStatisticsExcludeHistoricalSyntheticCasesAndKeepSharedReports()
    {
        string directory;
        using (var demo = new TestApp { KeepData = true, EnableBackgroundDelivery = false })
        {
            directory = demo.DataDirectory; using var client = demo.CreateClient();
            (await client.PostAsJsonAsync("/api/v1/map/demo/seed", new { })).EnsureSuccessStatusCode();
            await MapEndpointTests.CreateLocated(client);
            var wards = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/wards");
            Assert.True(Count(wards, "openCases") > 1);
            Assert.True(Count(wards, "slaBreaches") > 0);
        }
        using var production = new TestApp { DataDirectory = directory, EnableBackgroundDelivery = false };
        production.Settings["CIVICOPS_DEMO_MODE"] = "false";
        using var staff = production.CreateClient(); staff.DefaultRequestHeaders.Authorization = new("Bearer", "test-dispatch-token");
        foreach (var route in new[] { "/api/v1/map/wards", "/api/v1/map/layers?layers=wards" })
        {
            var wards = await staff.GetFromJsonAsync<JsonElement>(route);
            Assert.Equal(1, Count(wards, "openCases")); Assert.Equal(0, Count(wards, "slaBreaches"));
        }
        var points = await staff.GetFromJsonAsync<JsonElement>("/api/v1/map/layers?layers=cases");
        Assert.Equal(1, points.GetProperty("total").GetInt32());
        Assert.False(points.GetProperty("features")[0].GetProperty("properties").GetProperty("synthetic").GetBoolean());
        var heat = await staff.GetFromJsonAsync<JsonElement>("/api/v1/map/heatmap?metric=cases&window=7d");
        Assert.Equal(1, Count(heat, "count"));
    }

    [Fact]
    public async Task ExpiredReportCoordinatesDoNotRemainInWardStatisticsBeforePhysicalPurge()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var created = await MapEndpointTests.CreateLocated(client); var id = created.GetProperty("id").GetString()!;
        var store = app.Services.GetRequiredService<IncidentStore>(); await AgeReport(store, id);
        Assert.NotNull((await store.GetAsync(id, default))!.Location);
        foreach (var route in new[] { "/api/v1/map/wards", "/api/v1/map/layers?layers=wards" })
        {
            var wards = await client.GetFromJsonAsync<JsonElement>(route);
            Assert.Equal(0, Count(wards, "openCases")); Assert.Equal(0, Count(wards, "slaBreaches"));
        }
        var points = await client.GetFromJsonAsync<JsonElement>("/api/v1/map/layers?layers=cases");
        Assert.Equal(0, points.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AuditedManualJurisdictionWithoutCoordinatesRemainsInWardStatistics()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var created = await FoundationTests.Create(client); var id = created.GetProperty("id").GetString()!;
        var store = app.Services.GetRequiredService<IncidentStore>(); await AgeReport(store, id);
        var aged = await store.GetAsync(id, default);
        var response = await client.PostAsJsonAsync($"/api/v1/map/cases/{id}/ward-override", new
        {
            municipalityId = "demo-metro", wardId = "demo-ward-01",
            note = "Synthetic supervisor confirmed administrative service jurisdiction", expectedVersion = aged!.Version
        });
        response.EnsureSuccessStatusCode();
        var stored = await store.GetAsync(id, default); Assert.Null(stored!.Location);
        Assert.Contains(stored.Audit, value => value.Action == "geo.ward-overridden" && !string.IsNullOrWhiteSpace(value.Actor));
        foreach (var route in new[] { "/api/v1/map/wards", "/api/v1/map/layers?layers=wards" })
        {
            var wards = await client.GetFromJsonAsync<JsonElement>(route);
            Assert.Equal(1, Count(wards, "openCases")); Assert.Equal(1, Count(wards, "slaBreaches"));
        }
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ProductionReadinessRejectsTamperedGeoInEveryTenantAndRecoversAfterRestoration(string backend)
    {
        using var selected = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false }; app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        using var client = app.CreateClient();
        await app.Services.GetRequiredService<TenantStore>().SaveAsync(new("synthetic-coast", "Synthetic test municipality", "coast",
            ["Water and Sanitation"], new(), "Synthetic geo readiness test partition."), "Synthetic test supervisor", default);
        var geo = app.Services.GetRequiredService<GeoStore>(); var storage = app.Services.GetRequiredService<DurableStorage>();
        foreach (var tenant in new[] { "default", "synthetic-coast" })
        {
            using var lease = TenantContext.Use(tenant);
            await geo.AddHazardAsync(new("synthetic-readiness-hazard", "flooding", "Synthetic structured situation", new(31, -29.9),
                0.9, 50, DateTimeOffset.UtcNow.AddMinutes(-1), null, "Synthetic supervisor reviewed the situation"), "Synthetic test supervisor", default);
        }
        await AssertHealthyGeo(client);
        foreach (var tenant in new[] { "default", "synthetic-coast" })
        {
            var path = tenant == "default" ? Path.Combine(app.DataDirectory, "geo.json")
                : Path.Combine(app.DataDirectory, "tenants", tenant, "geo.json");
            var original = await storage.ReadAsync(path, default); Assert.NotNull(original);
            var tampered = JsonNode.Parse(original)!; tampered["hazards"]![0]!["confidence"] = 0.1;
            await storage.WriteAsync(path, tampered.ToJsonString(), default);
            using var response = await client.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.False(result.GetProperty("ready").GetBoolean());
            var checks = result.GetProperty("checks"); Assert.Equal("healthy", checks.GetProperty("storage").GetString());
            Assert.Equal("healthy", checks.GetProperty("migrations").GetString()); Assert.Equal("unhealthy", checks.GetProperty("geo").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            await storage.WriteAsync(path, original, default); await AssertHealthyGeo(client);
        }
    }

    private static int Count(JsonElement collection, string property) => collection.GetProperty("features").EnumerateArray()
        .Sum(feature => feature.GetProperty("properties").GetProperty(property).GetInt32());
    private static async Task AssertHealthyGeo(HttpClient client)
    {
        using var response = await client.GetAsync("/health/ready"); response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(result.GetProperty("ready").GetBoolean());
        Assert.Equal("healthy", result.GetProperty("checks").GetProperty("geo").GetString());
    }
    private static async Task AgeReport(IncidentStore store, string id)
    {
        var current = await store.GetAsync(id, default);
        var result = await store.ChangeAsync(id, current!.Version, incident =>
        {
            incident.CreatedAt = DateTimeOffset.UtcNow.AddDays(-31);
            incident.Audit.Add(new("synthetic.clock-adjusted", "Synthetic test supervisor", "Synthetic retention time fixture.", DateTimeOffset.UtcNow));
            return null;
        }, default);
        Assert.Null(result.Error);
    }
}
