using Microsoft.Extensions.Configuration;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CivicOps.Tests;

public sealed class PostgresStorageTests
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ProjectedAuditRejectsSystemTailTruncationAndHashPreservingBodyRewrite(string backend)
    {
        using var selection = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        await FoundationTests.Create(client);
        var storage = app.Services.GetRequiredService<DurableStorage>(); var store = app.Services.GetRequiredService<IncidentStore>();
        await store.RecordSystemEventAsync(new("synthetic-review-checkpoint", "Synthetic audit reviewer", "Synthetic immutable system checkpoint.", DateTimeOffset.UtcNow), CancellationToken.None);
        var path = Path.Combine(app.DataDirectory, "incidents.json");
        var original = (await storage.ReadAsync(path, CancellationToken.None))!;
        var snapshot = JsonNode.Parse(original)!;
        var audit = snapshot["audit"]!.AsArray();
        Assert.True(audit.Count > 1);
        audit.RemoveAt(audit.Count - 1);
        // This removes only a system event, so ordinary case-state/chain verification cannot detect the omitted tail.
        var records = audit.Deserialize<List<AuditRecord>>(SnapshotJson)!;
        var incidents = snapshot["incidents"]!.Deserialize<List<Incident>>(SnapshotJson)!;
        Assert.True(AuditLog.VerifySnapshot(records, incidents).Valid);
        if (backend == "file") { Assert.False(storage.IsPostgres); return; }
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.WriteAsync(path, snapshot.ToJsonString(), CancellationToken.None));
        Assert.Equal(original, await storage.ReadAsync(path, CancellationToken.None));
        snapshot = JsonNode.Parse(original)!;
        snapshot["audit"]![0]!["actor"] = "Synthetic rewritten actor with unchanged stored hash";
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.WriteAsync(path, snapshot.ToJsonString(), CancellationToken.None));
        Assert.Equal(original, await storage.ReadAsync(path, CancellationToken.None));
        Assert.True((await store.VerifyAuditAsync(CancellationToken.None)).Valid);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task IncidentMutationCommitsAuditContactAndDeliveryIntentTogether(string backend)
    {
        using var selection = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<IncidentStore>(); var outbox = app.Services.GetRequiredService<IntegrationOutbox>();
        var created = await store.CreateAsync(new Incident { Description = "Synthetic storage contract pipe leak", Area = "Fictitious ward", NormalizedArea = "fictitious ward" }, "synthetic-transaction", new(), CancellationToken.None);
        if (backend == "file") await outbox.EnqueueAsync(created.Incident, "create", CancellationToken.None);
        Assert.Single(await outbox.ListAsync(CancellationToken.None));
        Assert.True((await store.VerifyAuditAsync(CancellationToken.None)).Valid);
        Assert.Single(await store.ListAsync(CancellationToken.None));
        if (backend == "postgres") Assert.False(File.Exists(Path.Combine(app.DataDirectory, "incidents.json")));
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task FailedPostgresUnitOfWorkRollsBackEveryDurableMutation(string backend)
    {
        using var selection = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var storage = app.Services.GetRequiredService<DurableStorage>(); var store = app.Services.GetRequiredService<IncidentStore>();
        if (backend == "file") { Assert.False(storage.IsPostgres); Assert.True(storage.MigrationsReady); return; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.RunAsync(async () =>
        {
            await store.CreateAsync(new Incident { Description = "Synthetic rolled back pipe leak", Area = "Fictitious ward", NormalizedArea = "fictitious ward" }, "synthetic-rollback", new(), CancellationToken.None);
            throw new InvalidOperationException("Synthetic transaction failure.");
        }, CancellationToken.None));
        Assert.Empty(await store.ListAsync(CancellationToken.None));
        Assert.Empty(await store.AuditAsync(CancellationToken.None));
        Assert.Empty(await app.Services.GetRequiredService<IntegrationOutbox>().ListAsync(CancellationToken.None));
        Assert.True(await storage.HealthCheckAsync(CancellationToken.None));
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task IndependentInstancesPreserveIdempotencyAndOptimisticConcurrency(string backend)
    {
        using var selection = StoreBackends.Use(backend);
        var directory = Path.Combine(Path.GetTempPath(), "civicops-multi-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new TestApp { DataDirectory = directory, KeepData = true, EnableBackgroundDelivery = false };
            using var firstClient = first.CreateClient();
            if (backend == "file")
            {
                using var refused = new TestApp { DataDirectory = directory, KeepData = true };
                Assert.Throws<InvalidOperationException>(() => refused.CreateClient()); return;
            }
            using var second = new TestApp { DataDirectory = directory, KeepData = true, EnableBackgroundDelivery = false }; using var secondClient = second.CreateClient();
            var stores = new[] { first.Services.GetRequiredService<IncidentStore>(), second.Services.GetRequiredService<IncidentStore>() };
            var created = await Task.WhenAll(stores.Select(store => store.CreateAsync(new Incident { Description = "Synthetic same-key pipe leak", Area = "Fictitious ward", NormalizedArea = "fictitious ward" }, "synthetic-shared-key", new(), CancellationToken.None)));
            Assert.Single(created, result => result.Created); Assert.Equal(created[0].Incident.Id, created[1].Incident.Id);
            var version = created[0].Incident.Version;
            var changed = await Task.WhenAll(stores.Select(store => store.ChangeAsync(created[0].Incident.Id, version, incident => { incident.Area = "Updated fictitious ward"; return null; }, CancellationToken.None)));
            Assert.Single(changed, result => result.Conflict); Assert.Single(changed, result => result.Error is null);
            Assert.Equal(version + 1, (await stores[0].GetAsync(created[0].Incident.Id, CancellationToken.None))!.Version);
            Assert.True((await stores[1].VerifyAuditAsync(CancellationToken.None)).Valid);
            Assert.Single(await first.Services.GetRequiredService<IntegrationOutbox>().ListAsync(CancellationToken.None));
        }
        finally
        {
            if (backend == "postgres") StoreBackends.DeletePartition(StoreBackends.TestConnection(directory));
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task FileImportPreservesAllAuditHashesAndRejectsNonemptyTarget(string backend)
    {
        var source = new TestApp { KeepData = true, EnableBackgroundDelivery = false };
        var sourceDirectory = source.DataDirectory; string id; string[] hashes;
        try
        {
            using (source)
            using (var client = source.CreateClient())
            {
                var incident = await FoundationTests.Create(client); id = incident.GetProperty("id").GetString()!;
                hashes = (await source.Services.GetRequiredService<IncidentStore>().AuditAsync(CancellationToken.None)).Select(record => record.Hash).ToArray();
            }
            using var selection = StoreBackends.Use(backend);
            var targetDirectory = Path.Combine(Path.GetTempPath(), "civicops-import-" + Guid.NewGuid().ToString("N"));
            var settings = new Dictionary<string, string?> { ["CIVICOPS_STORAGE"] = backend, ["CIVICOPS_DATA_DIR"] = targetDirectory, ["CIVICOPS_DEMO_MODE"] = "true" };
            if (backend == "postgres") settings["CIVICOPS_DATABASE_URL"] = StoreBackends.TestConnection(targetDirectory);
            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var environment = new SyntheticEnvironment(); await using var storage = new DurableStorage(config, environment);
            try
            {
                if (backend == "file") { await Assert.ThrowsAsync<InvalidOperationException>(() => StorageCommand.ImportAsync(storage, sourceDirectory, config, environment, CancellationToken.None)); return; }
                await storage.InitializeAsync();
                Assert.True(await StorageCommand.ImportAsync(storage, sourceDirectory, config, environment, CancellationToken.None) > 0);
                using var imported = new IncidentStore(config, environment, persistence: storage);
                Assert.NotNull(await imported.GetAsync(id, CancellationToken.None)); Assert.True((await imported.VerifyAuditAsync(CancellationToken.None)).Valid);
                Assert.Equal(hashes, (await imported.AuditAsync(CancellationToken.None)).Take(hashes.Length).Select(record => record.Hash));
                await Assert.ThrowsAsync<InvalidOperationException>(() => StorageCommand.ImportAsync(storage, sourceDirectory, config, environment, CancellationToken.None));
            }
            finally { if (backend == "postgres") StoreBackends.DeletePartition(StoreBackends.TestConnection(targetDirectory)); }
        }
        finally { if (Directory.Exists(sourceDirectory)) Directory.Delete(sourceDirectory, true); }
    }
}
