using System.Text.Json;
using System.Text.Json.Serialization;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class TenantIsolationTests
{
    private const string Coast = "synthetic-coast";
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static Incident Report(string description = "Synthetic municipality pipe leak") => new()
    { Description = description, RawDescription = description, Area = "Fictitious ward", NormalizedArea = "fictitious ward" };
    private static TrainingCompletionRequest Completion() => new("synthetic-water-01-a", "synthetic-module", "WATER.BURST_MAIN",
        WorkOrderCatalog.Default.Find("WATER.BURST_MAIN")!.RequiredSkills.Select(skill => skill.Code).ToArray(),
        95, true, 120, 1, "synthetic-device", new string('a', 64), DateTimeOffset.UtcNow.AddMinutes(-1));

    [Fact]
    public async Task ContextLeaseRestoresAndParallelFlowsNeverShareMunicipality()
    {
        Assert.Equal(TenantStore.DefaultTenantId, TenantContext.CurrentId);
        using (TenantContext.Use(Coast))
        {
            Assert.Equal(Coast, TenantContext.CurrentId);
            await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
            {
                var id = "synthetic-" + index;
                using var lease = TenantContext.Use(id);
                await Task.Yield();
                Assert.Equal(id, TenantContext.CurrentId);
            }));
            Assert.Equal(Coast, TenantContext.CurrentId);
        }
        Assert.Equal(TenantStore.DefaultTenantId, TenantContext.CurrentId);
    }

    [Theory]
    [InlineData("../default")][InlineData("A")][InlineData("")][InlineData("default/other")]
    public void ContextRejectsFilesystemTraversalAndUnboundedIdentities(string id) =>
        Assert.Throws<InvalidOperationException>(() => TenantContext.Use(id));

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task IncidentReadWriteReplayDuplicateAuditRetentionAndOutboxAreIsolated(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<IncidentStore>();
        var outbox = app.Services.GetRequiredService<IntegrationOutbox>();
        var first = await store.CreateAsync(Report(), "shared-key", new(), Ct);
        var firstId = first.Incident.Id;
        using (TenantContext.Use(Coast))
        {
            Assert.Empty(await store.ListAsync(Ct)); Assert.Null(await store.GetAsync(firstId, Ct));
            Assert.Null((await store.ChangeAsync(firstId, null, incident => { incident.Area = "Spoof"; return null; }, Ct)).Incident);
            Assert.Empty(await store.AuditAsync(Ct));
            Assert.DoesNotContain(await outbox.ListAsync(Ct), item => item.IncidentId == firstId);
            var other = await store.CreateAsync(Report(), "shared-key", new(), Ct);
            Assert.True(other.Created); Assert.NotEqual(firstId, other.Incident.Id);
            Assert.Null(other.Incident.DuplicateOf); Assert.Equal(Coast, other.Incident.TenantId);
            await outbox.EnqueueAsync(other.Incident, "create", Ct);
            Assert.All(await outbox.ListAsync(Ct), item => Assert.Equal(Coast, item.TenantId));
            await store.RecordSystemEventAsync(new("synthetic.test", "Synthetic tester", "Coastal event", DateTimeOffset.UtcNow), Ct);
            Assert.All(await store.AuditAsync(Ct), record => Assert.Equal(Coast, record.TenantId));
            Assert.True((await store.VerifyAuditAsync(Ct)).Valid);
            Assert.Equal(0, await store.PurgeClosedAsync(DateTimeOffset.UtcNow.AddDays(1), Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => outbox.EnqueueAsync(first.Incident, "create", Ct));
        }
        Assert.DoesNotContain(await store.AuditAsync(Ct), record => record.Detail == "Coastal event");
        var spoof = await store.ChangeAsync(firstId, null, incident => { incident.TenantId = Coast; return null; }, Ct);
        Assert.Contains("cannot be changed", spoof.Error);
        Assert.Equal(TenantStore.DefaultTenantId, (await store.GetAsync(firstId, Ct))!.TenantId);
        Assert.True((await store.VerifyAuditAsync(Ct)).Valid);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task IncidentBodyTenantCannotOverrideTrustedBinding(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<IncidentStore>();
        var request = Report(); request.TenantId = Coast;
        var result = await store.CreateAsync(request, "synthetic-body-tenant", new(), Ct);
        Assert.Equal(TenantStore.DefaultTenantId, result.Incident.TenantId);
        using var lease = TenantContext.Use(Coast);
        Assert.Null(await store.GetAsync(result.Incident.Id, Ct));
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task WorkforceCrudCertificationsAvailabilityAndTrainingRecordsAreIsolated(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = app.CreateClient();
        var workforce = app.Services.GetRequiredService<WorkforceStore>();
        var training = app.Services.GetRequiredService<TrainingStore>();
        var crew = new Crew("synthetic-new-crew", "Synthetic municipal crew", "Water and Sanitation", "Synthetic repairs", []);
        await workforce.UpsertCrewAsync(crew, "Synthetic admin", Ct);
        var worker = new Worker("synthetic-local-worker", "Synthetic municipal worker", crew.Id, [], []);
        await workforce.UpsertWorkerAsync(worker, "Synthetic admin", Ct);
        var completion = await training.CompleteAsync(Completion(), "shared-training-key", "synthetic-client", Ct);
        Assert.True(completion.Created);
        using (TenantContext.Use(Coast))
        {
            Assert.Null(await workforce.GetWorkerAsync(worker.Id, Ct));
            Assert.DoesNotContain(await workforce.ListCrewsAsync(Ct), value => value.Id == crew.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => workforce.SetCrewAvailabilityAsync(crew.Id, "unavailable", "Synthetic admin", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => workforce.UpsertWorkerAsync(worker, "Synthetic admin", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => workforce.UpsertCrewAsync(crew with { MemberIds = [worker.Id] }, "Synthetic admin", Ct));
            Assert.Empty(await training.QualificationsAsync(Completion().WorkerId, Ct));
            Assert.Empty(await training.AuditAsync(Ct));
            var foreignGrant = await training.GrantAsync(new(Completion().WorkerId, "WATER.ISOLATION_SIM", completion.Record!.Id, true, "synthetic-signoff"),
                "synthetic-foreign-grant", "synthetic-client", Ct);
            Assert.NotNull(foreignGrant.Error);
            var other = await training.CompleteAsync(Completion(), "shared-training-key", "synthetic-client", Ct);
            Assert.True(other.Created); Assert.Equal(Coast, other.Record!.TenantId);
            Assert.NotEqual(completion.Record.Id, other.Record.Id);
            Assert.All(await workforce.ListWorkersAsync(Ct), value => Assert.Equal(Coast, value.TenantId));
            Assert.All(await workforce.ListCrewsAsync(Ct), value => Assert.Equal(Coast, value.TenantId));
            Assert.True((await training.VerifyAsync(Ct)).Valid); Assert.True((await workforce.VerifyAsync(Ct)).Valid);
            await training.RecoverAsync(Ct);
        }
        Assert.Single(await training.QualificationsAsync(Completion().WorkerId, Ct));
        Assert.Equal("available", (await workforce.ListCrewsAsync(Ct)).Single(value => value.Id == crew.Id).Availability);
        Assert.True((await workforce.VerifyAsync(Ct)).Valid);
    }

    [Fact]
    public void DispatcherAndTrainingStreamsPublishOnlyWithinCapturedMunicipality()
    {
        var events = new CaseEventStream();
        var inland = events.Subscribe(); var inlandTraining = events.Subscribe(true);
        (Guid Id, System.Threading.Channels.ChannelReader<CaseStreamEvent> Reader) coast;
        using (TenantContext.Use(Coast)) { coast = events.Subscribe(); events.Publish(); events.Publish("training.completed"); }
        Assert.False(inland.Reader.TryRead(out _)); Assert.False(inlandTraining.Reader.TryRead(out _));
        Assert.True(coast.Reader.TryRead(out var refresh)); Assert.Equal("refresh", refresh.Type);
        Assert.True(coast.Reader.TryRead(out var training)); Assert.Equal("training.completed", training.Type);
        events.Publish("certification.granted");
        Assert.True(inland.Reader.TryRead(out _)); Assert.True(inlandTraining.Reader.TryRead(out _));
        Assert.False(coast.Reader.TryRead(out _));
        events.Unsubscribe(inland.Id); events.Unsubscribe(inlandTraining.Id); events.Unsubscribe(coast.Id);
    }

    [Fact]
    public async Task LegacySnapshotMigratesToDefaultAndExtendsOriginalAuditHashes()
    {
        using var app = new TestApp();
        Directory.CreateDirectory(app.DataDirectory);
        var incident = Report(); incident.Audit.Add(new("received", "Synthetic legacy", "Legacy synthetic report", DateTimeOffset.UtcNow));
        var historical = AuditLog.Append([], incident, incident.Audit[0]);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        await File.WriteAllTextAsync(Path.Combine(app.DataDirectory, "incidents.json"), JsonSerializer.Serialize(new { incidents = new[] { incident }, audit = new[] { historical } }, json));
        using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<IncidentStore>();
        var read = await store.GetAsync(incident.Id, Ct);
        Assert.Equal(TenantStore.DefaultTenantId, read!.TenantId);
        Assert.Equal(historical.Hash, (await store.AuditAsync(Ct))[0].Hash);
        Assert.Contains(await store.AuditAsync(Ct), value => value.Action == "tenant.migrated");
        Assert.True((await store.VerifyAuditAsync(Ct)).Valid);
        using var lease = TenantContext.Use(Coast); Assert.Null(await store.GetAsync(incident.Id, Ct));
    }

    [Fact]
    public async Task WrongMunicipalitySnapshotFailsClosedOnEveryRead()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var directory = Path.Combine(app.DataDirectory, "synthetic-tampered-partition");
        Directory.CreateDirectory(directory);
        var incident = Report(); incident.TenantId = Coast;
        incident.Audit.Add(new("received", "Synthetic tester", "Tampered partition fixture", DateTimeOffset.UtcNow));
        var record = AuditLog.Append([], incident, incident.Audit[0]);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        await File.WriteAllTextAsync(Path.Combine(directory, "incidents.json"), JsonSerializer.Serialize(new { incidents = new[] { incident }, audit = new[] { record } }, json));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_DATA_DIR"] = directory }).Build();
        var store = new IncidentStore(configuration, app.Services.GetRequiredService<IWebHostEnvironment>());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync(Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(incident.Id, Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.AuditAsync(Ct));
    }

    [Fact]
    public void TenantBindingIsAnAuthenticatedAuditField()
    {
        var incident = Report(); incident.TenantId = TenantStore.DefaultTenantId;
        var record = AuditLog.Append([], incident, new("received", "Synthetic tester", "Synthetic fixture", DateTimeOffset.UtcNow));
        Assert.True(AuditLog.Verify([record]).Valid);
        Assert.False(AuditLog.Verify([record with { TenantId = Coast }]).Valid);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task DirectorySeedsTwoSyntheticMunicipalitiesAndValidatesVersionedEdits(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = app.CreateClient();
        var directory = app.Services.GetRequiredService<TenantStore>();
        var tenants = await directory.ListAsync(Ct);
        Assert.Equal(2, tenants.Count); Assert.All(tenants, tenant => Assert.True(tenant.Synthetic));
        Assert.Equal(Coast, (await directory.ResolveSlugAsync("coast", Ct))!.Id);
        var local = (await directory.GetAsync(TenantStore.DefaultTenantId, Ct))!;
        var saved = await directory.SaveAsync(local with { ServiceArea = "Updated fictitious boundary" }, "Synthetic admin", Ct);
        Assert.Equal(2, saved.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.SaveAsync(local, "Synthetic admin", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.SaveAsync(local with { Id = "../foreign" }, "Synthetic admin", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.SaveAsync(local with { Slug = "coast" }, "Synthetic admin", Ct));
    }
    [Fact]
    public async Task CopiedUnboundLegacySnapshotsCannotBeReboundToAnotherMunicipality()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tenant-legacy-copy-" + Guid.NewGuid().ToString("N"));
        var coastDirectory = Path.Combine(directory, "tenants", Coast); Directory.CreateDirectory(coastDirectory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["CIVICOPS_DATA_DIR"] = directory, ["CIVICOPS_DEMO_MODE"] = "true" }).Build();
            var environment = new SyntheticEnvironment(); var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            var incident = Report(); incident.Audit.Add(new("received", "Synthetic legacy", "Legacy synthetic fixture", DateTimeOffset.UtcNow));
            var historical = AuditLog.Append([], incident, incident.Audit[0]);
            await File.WriteAllTextAsync(Path.Combine(coastDirectory, "incidents.json"), JsonSerializer.Serialize(new { incidents = new[] { incident }, audit = new[] { historical } }, json));
            var workforceState = new { Workers = new List<Worker>(), Crews = new List<Crew>(), GrantReceipts = new Dictionary<string, string>() };
            var workforceAudit = new WorkforceAuditRecord(1, "workforce.seeded", "Synthetic legacy", "Synthetic fixture", DateTimeOffset.UtcNow,
                AuditLog.Genesis, IncidentStore.Hash(JsonSerializer.Serialize(workforceState)), "");
            workforceAudit = workforceAudit with { Hash = IncidentStore.Hash(JsonSerializer.Serialize(workforceAudit)) };
            await File.WriteAllTextAsync(Path.Combine(coastDirectory, "workforce.json"), JsonSerializer.Serialize(new
            { workers = workforceState.Workers, crews = workforceState.Crews, grantReceipts = workforceState.GrantReceipts, audit = new[] { workforceAudit } }, json));
            var completion = new TrainingCompletion("synthetic-legacy-completion", Completion(), "synthetic-legacy-client", DateTimeOffset.UtcNow, "synthetic-key", "synthetic-request");
            var trainingAudit = new TrainingAuditRecord(1, "training.completed", completion.Id, completion.Record.WorkerId, completion.Client,
                completion.RecordedAt, IncidentStore.Hash(JsonSerializer.Serialize(completion)), AuditLog.Genesis, "");
            trainingAudit = trainingAudit with { Hash = IncidentStore.Hash(JsonSerializer.Serialize(new
            { trainingAudit.Sequence, trainingAudit.Action, trainingAudit.RecordId, trainingAudit.WorkerId, trainingAudit.Actor, trainingAudit.At, trainingAudit.StateHash, trainingAudit.PreviousHash })) };
            await File.WriteAllTextAsync(Path.Combine(coastDirectory, "training.json"), JsonSerializer.Serialize(new { completions = new[] { completion }, grants = Array.Empty<TrainingCertificationGrant>(), audit = new[] { trainingAudit } }, json));
            await File.WriteAllTextAsync(Path.Combine(coastDirectory, "integration-outbox.json"), JsonSerializer.Serialize(new[] { new IntegrationOutboxItem { IncidentId = incident.Id, CaseReference = incident.ReferenceNumber } }, json));
            await File.WriteAllTextAsync(Path.Combine(coastDirectory, "elevenlabs-webhooks.json"), JsonSerializer.Serialize(new[]
            { new ElevenLabsReceipt("synthetic-event", "synthetic-body-hash", incident.ReferenceNumber, "post_call_transcription", null, DateTimeOffset.UtcNow) }, json));
            var incidents = new IncidentStore(configuration, environment); var workforce = new WorkforceStore(configuration, environment, WorkOrderCatalog.Default, TimeProvider.System);
            var training = new TrainingStore(configuration, environment, WorkOrderCatalog.Default, workforce, new TrainingAccess(configuration), incidents, new(), TimeProvider.System);
            var options = new ZendeskOptions { DataDirectory = directory, DemoMode = true };
            using var http = new HttpClient(); var zendesk = new ZendeskClient(http, options);
            var outbox = new IntegrationOutbox(zendesk, options, incidents, TimeProvider.System);
            var receipts = new ElevenLabsChannelState(configuration, environment);
            using var lease = TenantContext.Use(Coast);
            await Assert.ThrowsAsync<InvalidDataException>(() => incidents.ListAsync(Ct));
            await Assert.ThrowsAsync<InvalidDataException>(() => workforce.ListWorkersAsync(Ct));
            await Assert.ThrowsAsync<InvalidDataException>(() => training.AuditAsync(Ct));
            await Assert.ThrowsAsync<InvalidDataException>(() => outbox.ListAsync(Ct));
            await Assert.ThrowsAsync<InvalidDataException>(() => receipts.ReadAsync(Ct));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LoadedDirectoryRejectsDuplicateResidentHostsIgnoringCase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tenant-host-tamper-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_DATA_DIR"] = directory }).Build();
            var environment = new SyntheticEnvironment();
            var first = new Tenant("default", "Synthetic inland", "inland", ["Public Works"], new(), "Fictitious area", ResidentHost: "municipality.example.invalid");
            var second = first with { Id = Coast, Slug = "coast", ResidentHost = "MUNICIPALITY.example.invalid" };
            await File.WriteAllTextAsync(Path.Combine(directory, "tenants.json"), JsonSerializer.Serialize(new[] { first, second }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var store = new TenantStore(configuration, environment, new IncidentStore(configuration, environment), TimeProvider.System);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync(Ct));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ResolveHostAsync("municipality.example.invalid", Ct));
        }
        finally { Directory.Delete(directory, true); }
    }

}
