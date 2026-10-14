using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CivicOps.Tests;

public class OperationalHardeningTests
{
    [Fact]
    public void MetricsUseFirstAuditMilestonesExcludeUnfinishedAndExposeNullWithoutMeasurements()
    {
        var now = DateTimeOffset.UtcNow;
        AuditRecord Record(string id, string action, int seconds, string detail = "") => new(1, id, id, action, "test", detail,
            now.AddSeconds(seconds), "", "", "");
        var audit = new[]
        {
            Record("A", "received", 0), Record("A", "triaged", 2), Record("A", "agent-pipeline", 8), Record("A", "approved", 10),
            Record("A", "reapproved", 100), Record("A", "assigned", 20), Record("A", "resolved", 50),
            Record("A", "metrics-intake", 2, "{\"source\":\"chat\",\"duplicate\":false,\"aiRuns\":2,\"fallbackRuns\":3}"),
            Record("B", "received", 0), Record("B", "triaged", 4), Record("B", "approved", 30),
            Record("B", "metrics-intake", 4, "{\"source\":\"voice\",\"duplicate\":true,\"aiRuns\":0,\"fallbackRuns\":5}"),
            Record("C", "received", 0), Record("system", "training.completed", 1)
        };
        var metrics = AuditMetrics.Compute(audit, true);
        Assert.Equal(3, metrics.Reports); Assert.Equal(3, metrics.ReportToTriage.MedianSeconds);
        Assert.Equal(20, metrics.ReportToApproval.MedianSeconds); Assert.Equal(2, metrics.ReportToApproval.Samples);
        Assert.Equal(50, metrics.ReportToResolution.MedianSeconds); Assert.Equal(1, metrics.ReportToResolution.Samples);
        Assert.Equal(.5, metrics.WithoutPhoneQueueShare); Assert.Equal(.5, metrics.DuplicateRate);
        Assert.Equal(2, metrics.AiDecisions); Assert.Equal(8, metrics.FallbackDecisions);
        var empty = AuditMetrics.Compute([], false);
        Assert.Null(empty.ReportToTriage.MedianSeconds); Assert.Null(empty.DuplicateRate); Assert.Null(empty.WithoutPhoneQueueShare);
    }

    [Fact]
    public async Task MetricsEndpointUsesActualTriageMilestoneBeforeRemainingPipeline()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var created = await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic water leak for metrics.", area = "Synthetic metric area" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var metrics = await client.GetFromJsonAsync<OperationalMetrics>("/api/metrics");
        Assert.Equal(1, metrics!.Reports); Assert.Equal(1, metrics.ReportToTriage.Samples);
        Assert.True(metrics.ReportToTriage.MedianSeconds >= 0); Assert.Null(metrics.ReportToApproval.MedianSeconds);
        Assert.Equal(1, metrics.WithoutPhoneQueueShare); Assert.Equal(0, metrics.AiDecisions); Assert.Equal(5, metrics.FallbackDecisions);
        var audit = await app.Services.GetRequiredService<IncidentStore>().AuditAsync(default);
        Assert.True(audit.Single(x => x.Action == "triaged").At <= audit.Single(x => x.Action == "agent-pipeline").At);
    }

    [Fact]
    public async Task ReadinessAndMetricsAreDispatcherOnlyAndNeverExposeConfiguredValues()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        app.Settings["CIVICOPS_ELEVENLABS_TOOL_SECRET"] = "synthetic-readiness-sentinel";
        using var client = app.CreateClient();
        foreach (var route in new[] { "/api/metrics", "/api/integrations/readiness" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-dispatch-token");
        var response = await client.GetStringAsync("/api/integrations/readiness");
        Assert.Contains("CIVICOPS_ELEVENLABS_TOOL_SECRET", response); Assert.DoesNotContain("synthetic-readiness-sentinel", response);
        Assert.DoesNotContain("test-dispatch-token", response); Assert.Contains("providerChecksPerformed", response);
    }

    [Fact]
    public async Task RetentionPurgesOnlyOldClosingMilestonesPreservesAuditAndSurvivesRestart()
    {
        using var app = new TestApp { KeepData = true }; using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<IncidentStore>(); var classifier = new DeterministicClassifier();
        var now = DateTimeOffset.UtcNow;
        async Task<Incident> Seed(IncidentStatus status, int closingDays)
        {
            var incident = new Incident { Description = "Synthetic water leak for retention testing.", Area = "Synthetic area " + closingDays, NormalizedArea = "Synthetic area " + closingDays,
                Status = status, CreatedAt = now.AddDays(-100), UpdatedAt = now.AddDays(-closingDays) };
            if (status == IncidentStatus.Closed && closingDays != 40) incident.Audit.Add(new("closed", "test", "Closed synthetic case.", now.AddDays(-closingDays)));
            return (await store.CreateAsync(incident, closingDays == 31 ? "retention-expired-key" : null, classifier, default)).Incident;
        }
        var expired = await Seed(IncidentStatus.Closed, 31); var recent = await Seed(IncidentStatus.Closed, 1);
        var open = await Seed(IncidentStatus.PendingApproval, 100);
        var legacy = await Seed(IncidentStatus.Closed, 40);
        Assert.Equal(1, await store.PurgeClosedAsync(now.AddDays(-30), default, new HashSet<string> { expired.Id }));
        Assert.Null(await store.GetAsync(legacy.Id, default));
        Assert.NotNull(await store.GetAsync(expired.Id, default));
        Assert.Equal(1, await store.PurgeClosedAsync(now.AddDays(-30), default));
        Assert.Null(await store.GetAsync(expired.Id, default)); Assert.NotNull(await store.GetAsync(recent.Id, default));
        Assert.NotNull(await store.GetAsync(open.Id, default)); Assert.True((await store.VerifyAuditAsync(default)).Valid);
        Assert.Contains(await store.AuditAsync(default), record => record.Action == "retention-purged");
        var directory = app.DataDirectory; app.Dispose();
        using var restarted = new TestApp { DataDirectory = directory }; using var resumed = restarted.CreateClient();
        var recovered = restarted.Services.GetRequiredService<IncidentStore>();
        Assert.Equal(2, (await recovered.ListAsync(default)).Count); Assert.True((await recovered.VerifyAuditAsync(default)).Valid);
        Assert.Equal(0, await recovered.PurgeClosedAsync(now.AddDays(-30), default));
        var retry = await recovered.CreateAsync(new Incident { Description = "Synthetic water leak for retention testing." }, "retention-expired-key", classifier, default);
        Assert.False(retry.Created); Assert.Contains("retention", retry.Error);
        Assert.Equal(2, (await recovered.ListAsync(default)).Count);
    }

    [Fact]
    public void ConcurrentProcessDataDirectoryLeaseFailsUntilReleased()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lease-" + Guid.NewGuid().ToString("N"));
        var options = new CivicOpsOptions { DataDirectory = directory };
        try
        {
            using (var first = new DataDirectoryLease(options)) Assert.Throws<InvalidOperationException>(() => new DataDirectoryLease(options));
            using var released = new DataDirectoryLease(options);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("A:invalid")]
    public void MalformedDispatcherIdentityConfigurationFailsClosed(string identities)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["CIVICOPS_DISPATCHERS"] = identities }).Build();
        Assert.Throws<InvalidOperationException>(() => new DispatcherAccess(configuration));
    }

    [Fact]
    public async Task OpenApiContainsActualRoutesScopesAndRequiredPathParameters()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi.json"));
        Assert.Equal("3.1.0", doc.RootElement.GetProperty("openapi").GetString());
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/incidents/{id}/crew-eligibility").GetProperty("get").GetProperty("parameters")[0].GetProperty("required").GetBoolean());
        Assert.True(paths.GetProperty("/api/v1/training/completions").GetProperty("post").GetProperty("security")[0].TryGetProperty("trainingBearer", out _));
        Assert.True(paths.GetProperty("/api/integrations/readiness").GetProperty("get").GetProperty("security")[0].TryGetProperty("dispatcherBearer", out _));
        Assert.True(doc.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject().Count() >= 10);
        void CheckReferences(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (value.TryGetProperty("$ref", out var reference))
                {
                    var target = doc.RootElement;
                    foreach (var part in reference.GetString()![2..].Split('/')) target = target.GetProperty(part.Replace("~1", "/").Replace("~0", "~"));
                    Assert.Equal(JsonValueKind.Object, target.ValueKind);
                }
                foreach (var child in value.EnumerateObject()) CheckReferences(child.Value);
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) CheckReferences(child);
        }
        CheckReferences(doc.RootElement);
    }
}
