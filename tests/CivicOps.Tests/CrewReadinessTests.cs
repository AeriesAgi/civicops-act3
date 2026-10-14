using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class CrewReadinessTests
{
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task TamperedCrewShiftConsentStateMakesReadinessUnhealthyInEveryTenant(string backend)
    {
        using var selected = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var client = app.CreateClient();
        const string coast = "synthetic-crew-coast";
        await app.Services.GetRequiredService<TenantStore>().SaveAsync(new(coast, "Synthetic crew municipality", "crew-coast",
            ["Water and Sanitation"], new(), "Synthetic crew readiness partition."), "Synthetic supervisor", default);
        var crews = app.Services.GetRequiredService<CrewStore>();
        var storage = app.Services.GetRequiredService<DurableStorage>();
        foreach (var tenant in new[] { "default", coast })
        {
            using var lease = TenantContext.Use(tenant);
            await crews.SetPolicyAsync(new(60, 100, 30, 12, "Synthetic supervisor retention review"), "Synthetic supervisor", default);
        }
        await Healthy(client);
        foreach (var tenant in new[] { "default", coast })
        {
            var path = tenant == "default" ? Path.Combine(app.DataDirectory, "crew-app.json")
                : Path.Combine(app.DataDirectory, "tenants", tenant, "crew-app.json");
            var original = await storage.ReadAsync(path, default); Assert.NotNull(original);
            var tampered = JsonNode.Parse(original)!; tampered["policy"]!["retentionDays"] = 29;
            await storage.WriteAsync(path, tampered.ToJsonString(), default);
            using var response = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(result.GetProperty("ready").GetBoolean());
            Assert.Equal("healthy", result.GetProperty("checks").GetProperty("storage").GetString());
            Assert.Equal("unhealthy", result.GetProperty("checks").GetProperty("crew").GetString());
            Assert.DoesNotContain("retentionDays", result.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            await storage.WriteAsync(path, original, default);
            await Healthy(client);
        }
    }
    private static async Task Healthy(HttpClient client)
    {
        using var response = await client.GetAsync("/health/ready"); response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("ready").GetBoolean());
        Assert.Equal("healthy", result.GetProperty("checks").GetProperty("crew").GetString());
    }
}
