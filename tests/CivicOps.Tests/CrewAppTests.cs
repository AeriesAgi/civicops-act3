using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CivicOps.Tests;

public sealed class CrewAppTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)] [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)] [InlineData(true, true, false)] [InlineData(true, true, true)]
    public async Task LocationRequiresActiveShiftExplicitConsentAndVisibleIndicator(bool onShift, bool consented, bool visible)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var clock = new CrewClock(DateTimeOffset.UtcNow); using var store = Store(app, clock); var actor = Actor();
        if (onShift) await store.StartAsync(actor, new("synthetic-start-operation"), default);
        if (consented) await store.ConsentAsync(actor, new("synthetic-consent-operation", CrewStore.NoticeVersion, true), default);
        var me = await store.MeAsync(actor, default);
        var result = await store.LocationsAsync(actor, Ping(me, clock.Now.AddSeconds(1)) with { VisibleIndicator = visible }, default);
        Assert.Equal(onShift && consented && visible ? 1 : -1, result.Accepted);
        Assert.Equal(onShift && consented && visible ? 1 : 0, (await store.HistoryAsync(actor, 1, 1000, false, default)).Total);
        var audit = await app.Services.GetRequiredService<IncidentStore>().AuditAsync(default);
        Assert.Contains(audit, value => value.Action == (onShift && consented && visible ? "crew.location-accepted" : "crew.location-rejected"));
        Assert.DoesNotContain(audit, value => value.Detail.Contains("30.975", StringComparison.Ordinal) || value.Detail.Contains("-29.91", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("shift")] [InlineData("consent")] [InlineData("other-device")]
    public async Task StaleShiftConsentAndOtherDeviceCannotUploadLocations(string mismatch)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var clock = new CrewClock(DateTimeOffset.UtcNow);
        using var store = Store(app, clock); var actor = Actor(); var me = await Ready(store, actor); var ping = Ping(me, clock.Now.AddSeconds(1));
        if (mismatch == "shift") ping = ping with { ShiftId = "shift-obsolete" };
        if (mismatch == "consent") ping = ping with { ConsentId = "consent-obsolete" };
        if (mismatch == "other-device") actor = actor with { OwnerId = "other-device-owner" };
        Assert.Equal(-1, (await store.LocationsAsync(actor, ping, default)).Accepted);
        Assert.Empty(await store.PositionsAsync(default));
    }
    [Theory]
    [InlineData("end")] [InlineData("withdraw")] [InlineData("max-shift")]
    public async Task EndWithdrawAndMaximumShiftImmediatelyHidePositionsAndRejectBufferedRetries(string stop)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var clock = new CrewClock(DateTimeOffset.UtcNow);
        using var store = Store(app, clock); var actor = Actor(); var me = await Ready(store, actor); var ping = Ping(me, clock.Now.AddSeconds(1));
        Assert.Equal(1, (await store.LocationsAsync(actor, ping, default)).Accepted); Assert.Single(await store.PositionsAsync(default));
        if (stop == "end") await store.EndAsync(actor, new("synthetic-end-operation", me.Shift!.Id), default);
        if (stop == "withdraw") await store.WithdrawAsync(actor, new("synthetic-withdraw-operation"), default);
        if (stop == "max-shift") clock.Now = clock.Now.AddHours(12);
        Assert.Empty(await store.PositionsAsync(default)); Assert.Equal(-1, (await store.LocationsAsync(actor, ping, default)).Accepted);
        if (stop == "withdraw") Assert.Null((await store.ConsentAsync(actor, new("synthetic-consent-operation", CrewStore.NoticeVersion, true), default)).Consent);
        if (stop == "end") Assert.Null((await store.StartAsync(actor, new("synthetic-start-operation"), default)).Shift);
    }
    [Theory]
    [InlineData("longitude")] [InlineData("latitude")] [InlineData("accuracy")] [InlineData("before-shift")]
    [InlineData("future")] [InlineData("empty")] [InlineData("too-many")] [InlineData("midnight")]
    public async Task LocationBatchesRejectInvalidBoundsTimingAccuracyAndShape(string invalid)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var clock = new CrewClock(new DateTimeOffset(2026, 10, 9, 23, 58, 0, TimeSpan.Zero)); using var store = Store(app, clock); var actor = Actor();
        var me = await Ready(store, actor); var point = Ping(me, clock.Now.AddSeconds(1)).Points[0];
        point = invalid switch { "longitude" => point with { Longitude = 0 }, "latitude" => point with { Latitude = -40 }, "accuracy" => point with { AccuracyMeters = 500 },
            "before-shift" => point with { CapturedAt = clock.Now.AddSeconds(-1) }, "future" => point with { CapturedAt = clock.Now.AddMinutes(10) }, _ => point };
        var ping = Ping(me, clock.Now.AddSeconds(1)) with { Points = [point] };
        if (invalid == "empty") ping = ping with { Points = [] };
        if (invalid == "too-many") ping = ping with { Points = Enumerable.Repeat(point, 101).ToList() };
        if (invalid == "midnight") { clock.Now = clock.Now.AddMinutes(3); ping = ping with { Points = [point, point with { CapturedAt = clock.Now }] }; }
        var error = await Assert.ThrowsAsync<CrewRequestException>(() => store.LocationsAsync(actor, ping, default)); Assert.Equal(400, error.Status);
        Assert.Empty(await store.PositionsAsync(default));
    }
    [Theory]
    [InlineData(14, 100, 30, 12)] [InlineData(601, 100, 30, 12)] [InlineData(60, 4, 30, 12)] [InlineData(60, 1001, 30, 12)]
    [InlineData(60, 100, 0, 12)] [InlineData(60, 100, 31, 12)] [InlineData(60, 100, 30, 0)] [InlineData(60, 100, 30, 13)]
    public void TenantPolicyCannotEnableUnlimitedOrExcessiveTracking(int interval, int accuracy, int retention, int hours) =>
        Assert.Throws<CrewRequestException>(() => CrewStore.ValidatePolicy(new(interval, accuracy, retention, hours)));
    [Fact]
    public async Task LocationReplayIsIdempotentConflictingKeyFailsAndTenantCadenceIsEnforced()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var clock = new CrewClock(DateTimeOffset.UtcNow);
        using var store = Store(app, clock); var actor = Actor(); var me = await Ready(store, actor); var ping = Ping(me, clock.Now.AddSeconds(1));
        Assert.False((await store.LocationsAsync(actor, ping, default)).Replayed); Assert.True((await store.LocationsAsync(actor, ping, default)).Replayed);
        var changed = ping with { Points = [ping.Points[0] with { Longitude = 31 }] };
        Assert.Equal(409, (await Assert.ThrowsAsync<CrewRequestException>(() => store.LocationsAsync(actor, changed, default))).Status);
        clock.Now = clock.Now.AddSeconds(30); var fast = Ping(me, clock.Now) with { OperationId = "synthetic-fast-location" };
        Assert.Equal(409, (await Assert.ThrowsAsync<CrewRequestException>(() => store.LocationsAsync(actor, fast, default))).Status);
        clock.Now = clock.Now.AddSeconds(31); var next = Ping(me, clock.Now) with { OperationId = "synthetic-next-location" };
        Assert.Equal(1, (await store.LocationsAsync(actor, next, default)).Accepted); Assert.Equal(2, (await store.HistoryAsync(actor, 1, 1, false, default)).Total);
        Assert.Equal(2, (await store.HistoryAsync(actor, 1, 1, false, default)).NextPage); Assert.Equal(2, (await store.HistoryAsync(actor, 1, 1, true, default)).Points.Count);
    }
    [Fact]
    public async Task ReusedOperationAcrossStartAndWithdrawalCannotReplayAnotherAction()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var clock = new CrewClock(DateTimeOffset.UtcNow);
        using var store = Store(app, clock); var actor = Actor(); await Ready(store, actor);
        var error = await Assert.ThrowsAsync<CrewRequestException>(() => store.WithdrawAsync(actor, new("synthetic-start-operation"), default)); Assert.Equal(409, error.Status);
        Assert.Null((await store.WithdrawAsync(actor, new("synthetic-unique-withdrawal"), default)).Consent);
        Assert.Null((await store.ConsentAsync(actor, new("synthetic-consent-operation", CrewStore.NoticeVersion, true), default)).Consent);
    }
    [Fact]
    public async Task LocationOperationCannotBeReusedInAnAdjacentUtcDay()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var clock = new CrewClock(new DateTimeOffset(2026, 10, 9, 23, 58, 0, TimeSpan.Zero)); using var store = Store(app, clock); var actor = Actor(); var me = await Ready(store, actor);
        clock.Now = clock.Now.AddMinutes(1); await store.LocationsAsync(actor, Ping(me, clock.Now), default);
        clock.Now = clock.Now.AddMinutes(1); var error = await Assert.ThrowsAsync<CrewRequestException>(() => store.LocationsAsync(actor, Ping(me, clock.Now), default));
        Assert.Equal(409, error.Status); Assert.Equal(1, (await store.HistoryAsync(actor, 1, 1000, false, default)).Total);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task RawHistoryPurgesDurablyAndRetainsOnlyAnonymousMunicipalityDayCounts(string backend)
    {
        using var selected = StoreBackends.Use(backend); using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var clock = new CrewClock(DateTimeOffset.UtcNow); using var store = Store(app, clock); var actor = Actor(); var me = await Ready(store, actor);
        await store.LocationsAsync(actor, Ping(me, clock.Now.AddSeconds(1)), default); clock.Now = clock.Now.AddDays(31);
        Assert.Empty((await store.HistoryAsync(actor, 1, 1000, false, default)).Points); Assert.Empty(await store.PositionsAsync(default));
        Assert.Equal(1, await store.PurgeAsync(default)); Assert.Equal(0, await store.PurgeAsync(default));
        Assert.Empty(await app.Services.GetRequiredService<DurableStorage>().ListPathsAsync(Path.Combine(app.DataDirectory, "crew-locations"), ".json", default));
        var aggregates = await store.AggregateStatsAsync(default); Assert.Equal(1, aggregates.Values.Single());
        Assert.All(aggregates.Keys, value => Assert.Matches("^[0-9]{8}$", value));
        Assert.DoesNotContain(actor.WorkerId, JsonSerializer.Serialize(aggregates), StringComparison.Ordinal);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task AutomaticRetentionContinuesToLaterMunicipalitiesWhenTheFirstTenantIsCorrupt(string backend)
    {
        using var selected = StoreBackends.Use(backend); using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var clock = new CrewClock(DateTimeOffset.UtcNow); using var store = Store(app, clock); var actor = Actor();
        var storage = app.Services.GetRequiredService<DurableStorage>(); var incidents = app.Services.GetRequiredService<IncidentStore>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["CIVICOPS_DATA_DIR"] = app.DataDirectory, ["CIVICOPS_DEMO_MODE"] = "true" }).Build();
        using var jobs = new CrewJobService(configuration, new SyntheticEnvironment(), storage, incidents, store, app.Services.GetRequiredService<WorkOrderCatalog>(), clock);
        var tenants = app.Services.GetRequiredService<TenantStore>(); Assert.Equal("default", (await tenants.ListAsync(default))[0].Id);
        await Ready(store, actor);
        const string coast = "synthetic-coast"; string jobId;
        using (TenantContext.Use(coast))
        {
            var me = await Ready(store, actor); await store.LocationsAsync(actor, Ping(me, clock.Now.AddSeconds(1)), default);
            var created = await incidents.CreateAsync(new Incident { Description = "Synthetic coast water repair", Area = "Synthetic coast area", Synthetic = true },
                "synthetic-coast-retention-job", app.Services.GetRequiredService<DeterministicClassifier>(), default);
            Assert.Null(created.Error); jobId = created.Incident.Id;
            var assigned = await incidents.ChangeAsync(jobId, null, candidate => { candidate.AssignedCrewId = actor.CrewId; return null; }, default);
            Assert.Null(assigned.Error);
            await jobs.PhotoAsync(actor, jobId, new("synthetic-retention-photo", "image/png", Convert.ToBase64String(CrewPhotoTests.Png()), false, assigned.Incident!.Version), default);
        }
        var path = Path.Combine(app.DataDirectory, "crew-app.json");
        var original = (await storage.ReadAsync(path, default))!; var corrupt = JsonNode.Parse(original)!; corrupt["policy"]!["retentionDays"] = 29;
        await storage.WriteAsync(path, corrupt.ToJsonString(), default); clock.Now = clock.Now.AddDays(31);
        using var retention = new CrewRetentionService(store, jobs, tenants, NullLogger<CrewRetentionService>.Instance);
        await retention.PurgeOnceAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.PolicyAsync(default));
        using (TenantContext.Use(coast))
        {
            var root = Path.Combine(app.DataDirectory, "tenants", coast);
            Assert.Empty(await storage.ListPathsAsync(Path.Combine(root, "crew-locations"), ".json", default));
            Assert.Empty(await storage.ListPathsAsync(Path.Combine(root, "crew-photos"), ".json", default));
            Assert.Empty((await incidents.GetVerifiedAsync(jobId, default))!.CrewWork!.Photos);
            Assert.Equal(1, (await store.AggregateStatsAsync(default)).Values.Single());
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retention.PurgeOnceAsync(cancelled.Token));
    }
    [Fact]
    public async Task MetadataOnlyRetentionCleanupPersistsWithoutAnySharedLocation()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var clock = new CrewClock(DateTimeOffset.UtcNow);
        using var store = Store(app, clock); var actor = Actor(); var me = await Ready(store, actor);
        await store.EndAsync(actor, new("synthetic-end-operation", me.Shift!.Id), default);
        await store.WithdrawAsync(actor, new("synthetic-withdraw-operation"), default);
        await store.PairAsync(new(actor.WorkerId, "Synthetic retained pairing", "Synthetic supervisor approval"), "Synthetic supervisor", default);
        clock.Now = clock.Now.AddDays(31); Assert.Equal(0, await store.PurgeAsync(default));
        var physical = JsonNode.Parse((await app.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(app.DataDirectory, "crew-app.json"), default))!)!;
        foreach (var name in new[] { "shifts", "consents", "pairings" }) Assert.Empty(physical[name]!.AsArray());
        Assert.Empty(physical["receipts"]!.AsObject()); Assert.Equal("crew.retention-purged", physical["audit"]!.AsArray()[^1]!["action"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("expired-device")] [InlineData("worker-reassigned")]
    public async Task MapPositionDisappearsAfterDeviceExpiryOrWorkforceBindingChange(string change)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var clock = new CrewClock(DateTimeOffset.UtcNow);
        using var store = Store(app, clock); var pair = await store.PairAsync(new("synthetic-water-01-a", "Synthetic Android", "Synthetic supervisor approval"), "Synthetic supervisor", default);
        var issued = (await store.ExchangeAsync(new(pair.PairingId, pair.Code, pair.TenantId), default))!;
        var principal = (await store.AuthenticateAsync(issued.Token, default))!; var actor = await store.ActorAsync(principal, default); var me = await Ready(store, actor);
        Assert.True(me.Shift!.ExpiresAt <= issued.ExpiresAt); await store.LocationsAsync(actor, Ping(me, clock.Now.AddSeconds(1)), default); Assert.Single(await store.PositionsAsync(default));
        if (change == "expired-device") { clock.Now = clock.Now.AddHours(9); Assert.Null(await store.AuthenticateAsync(issued.Token, default)); }
        else
        {
            var workforce = app.Services.GetRequiredService<WorkforceStore>(); var worker = (await workforce.GetWorkerAsync(actor.WorkerId, default))!;
            await workforce.UpsertWorkerAsync(worker with { CrewId = "roads-01" }, "Synthetic supervisor transfer", default);
            Assert.Equal(403, (await Assert.ThrowsAsync<CrewRequestException>(() => store.ActorAsync(principal, default))).Status);
        }
        Assert.Empty(await store.PositionsAsync(default));
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task CrewStateAndRawHistoryRejectTamperingAndCrossTenantCopies(string backend)
    {
        using var selected = StoreBackends.Use(backend); using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var clock = new CrewClock(DateTimeOffset.UtcNow); using var store = Store(app, clock); var actor = Actor(); var me = await Ready(store, actor);
        await store.LocationsAsync(actor, Ping(me, clock.Now.AddSeconds(1)), default); var storage = app.Services.GetRequiredService<DurableStorage>();
        var path = Path.Combine(app.DataDirectory, "crew-app.json"); var original = await storage.ReadAsync(path, default);
        using (TenantContext.Use("synthetic-coast"))
        {
            await storage.WriteAsync(Path.Combine(app.DataDirectory, "tenants", "synthetic-coast", "crew-app.json"), original!, default);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.PolicyAsync(default));
        }
        var tampered = JsonNode.Parse(original!)!; tampered["policy"]!["retentionDays"] = 1; await storage.WriteAsync(path, tampered.ToJsonString(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.MeAsync(actor, default)); await storage.WriteAsync(path, original!, default);
        var historyPath = (await storage.ListPathsAsync(Path.Combine(app.DataDirectory, "crew-locations"), ".json", default)).Single();
        tampered = JsonNode.Parse((await storage.ReadAsync(historyPath, default))!)!; tampered["points"]![0]!["longitude"] = 32;
        await storage.WriteAsync(historyPath, tampered.ToJsonString(), default); await Assert.ThrowsAsync<InvalidDataException>(() => store.HistoryAsync(actor, 1, 1000, false, default));
    }
    [Fact]
    public async Task PairingIsOneUseExpiringHashedRevocableAndStopsCurrentShift()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var supervisor = app.CreateClient();
        var issued = await Pair(supervisor); using var device = app.CreateClient(); device.DefaultRequestHeaders.Authorization = new("Bearer", issued.Token);
        var me = await device.GetFromJsonAsync<CrewMe>("/api/v1/crew/me"); Assert.Equal("synthetic-water-01-a", me!.WorkerId);
        (await device.PostAsJsonAsync("/api/v1/crew/shifts/start", new CrewOperationRequest("synthetic-start-device"))).EnsureSuccessStatusCode();
        var stored = await app.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(app.DataDirectory, "crew-app.json"), default);
        Assert.DoesNotContain(issued.Token, stored, StringComparison.Ordinal);
        (await supervisor.PostAsJsonAsync("/api/v1/crew/devices/" + issued.DeviceId + "/revoke", new CrewRevokeRequest("Synthetic lost device revocation"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await device.GetAsync("/api/v1/crew/me")).StatusCode);
        var devices = await supervisor.GetStringAsync("/api/v1/crew/devices"); Assert.DoesNotContain("tokenHash", devices, StringComparison.Ordinal); Assert.Contains("revokedAt", devices, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("wrong-code")] [InlineData("expired")] [InlineData("consumed")]
    public async Task PairingRejectsWrongExpiredOrConsumedCode(string failure)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var clock = new CrewClock(DateTimeOffset.UtcNow);
        using var store = Store(app, clock); var pair = await store.PairAsync(new("synthetic-water-01-a", "Synthetic Android", "Synthetic supervisor approval"), "Synthetic supervisor", default);
        var request = new CrewPairingExchangeRequest(pair.PairingId, pair.Code, pair.TenantId);
        if (failure == "wrong-code") request = request with { Code = "0000000000" };
        if (failure == "expired") clock.Now = clock.Now.AddMinutes(11);
        if (failure == "consumed") Assert.NotNull(await store.ExchangeAsync(request, default));
        Assert.Null(await store.ExchangeAsync(request, default));
        Assert.Contains(await app.Services.GetRequiredService<IncidentStore>().AuditAsync(default), value => value.Action == "crew.pairing-rejected");
    }
    [Theory]
    [InlineData("/api/incidents")] [InlineData("/api/audit")] [InlineData("/api/dispatcher")] [InlineData("/api/admin/workers")]
    [InlineData("/api/v1/map/layers")] [InlineData("/api/v1/crew/devices")] [InlineData("/api/v1/crew/policy")]
    public async Task DeviceTokensCannotAccessOtherStaffOrSupervisorRoutes(string route)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var supervisor = app.CreateClient(); var token = await Pair(supervisor);
        using var device = app.CreateClient(); device.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostAsJsonAsync("/api/v1/crew/pairing", new CrewPairingRequest("synthetic-water-01-b", "Synthetic illicit device", "Synthetic forbidden action"))).StatusCode);
    }
    [Theory]
    [InlineData("faceEmbedding")] [InlineData("voicePrint")] [InlineData("workerPriorityScore")] [InlineData("continuousTracking")]
    public async Task ClosedLocationContractRejectsBiometricPersonScoringAndCovertTrackingFields(string field)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var crew = await RbacTests.RoleClient(app, "crew", "water-01");
        var body = JsonSerializer.SerializeToNode(new CrewLocationRequest("synthetic-guard-location", "shift-unavailable", "consent-unavailable", true, [new(31, -29.9, 20, DateTimeOffset.UtcNow)]), JsonSerializerOptions.Web)!.AsObject(); body[field] = "synthetic-prohibited-field";
        Assert.Equal(HttpStatusCode.BadRequest, (await crew.PostAsJsonAsync("/api/v1/crew/locations", body)).StatusCode);
        Assert.Empty(await app.Services.GetRequiredService<CrewStore>().PositionsAsync(default));
    }
    [Fact]
    public async Task ActualHttpOffShiftAndNoConsentRejectWith403AndAuditWithoutStoringCoordinates()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var supervisor = app.CreateClient(); var issued = await Pair(supervisor);
        using var device = app.CreateClient(); device.DefaultRequestHeaders.Authorization = new("Bearer", issued.Token);
        var me = await device.GetFromJsonAsync<CrewMe>("/api/v1/crew/me"); var ping = Ping(me!, DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostAsJsonAsync("/api/v1/crew/locations", ping)).StatusCode);
        (await device.PostAsJsonAsync("/api/v1/crew/shifts/start", new CrewOperationRequest("synthetic-start-operation"))).EnsureSuccessStatusCode();
        me = await device.GetFromJsonAsync<CrewMe>("/api/v1/crew/me"); Assert.Equal(HttpStatusCode.Forbidden, (await device.PostAsJsonAsync("/api/v1/crew/locations", Ping(me!, DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(2, (await app.Services.GetRequiredService<IncidentStore>().AuditAsync(default)).Count(value => value.Action == "crew.location-rejected"));
        Assert.Equal(0, (await device.GetFromJsonAsync<CrewLocationHistory>("/api/v1/crew/me/locations"))!.Total);
    }
    [Fact]
    public async Task CookieCrewSessionUsesCsrfAndExplicitDemoWorkerBinding()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var crew = await RbacTests.RoleClient(app, "crew", "water-01");
        Assert.Equal("synthetic-water-01-a", (await crew.GetFromJsonAsync<CrewMe>("/api/v1/crew/me"))!.WorkerId);
        crew.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await crew.PostAsJsonAsync("/api/v1/crew/shifts/start", new CrewOperationRequest("synthetic-no-csrf-start"))).StatusCode);
    }
    [Fact]
    public async Task JobsStatusNotesAndPhotoAreAssignedVersionedIdempotentAndCompletionRequiresDispatcherReview()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var supervisor = app.CreateClient(); var id = await Assigned(app, supervisor);
        var issued = await Pair(supervisor); using var device = app.CreateClient(); device.DefaultRequestHeaders.Authorization = new("Bearer", issued.Token);
        var job = await device.GetFromJsonAsync<CrewJob>("/api/v1/crew/me/jobs/" + id); Assert.NotEmpty(job!.Ppe); Assert.NotEmpty(job.Steps);
        var request = new CrewJobStatusRequest("synthetic-en-route-key", "en-route", "", job.Version);
        var result = await Post<CrewJobResult>(device, "/api/v1/crew/me/jobs/" + id + "/status", request); Assert.Equal(job.Version + 1, result.Job.Version);
        Assert.True((await Post<CrewJobResult>(device, "/api/v1/crew/me/jobs/" + id + "/status", request)).Replayed);
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync("/api/v1/crew/me/jobs/" + id + "/status", request with { Reason = "Conflicting operation payload" })).StatusCode);
        var note = new CrewJobNoteRequest("synthetic-note-key", "Synthetic valve isolated and photographed", result.Job.Version);
        result = await Post<CrewJobResult>(device, "/api/v1/crew/me/jobs/" + id + "/notes", note); Assert.Single(result.Job.Notes);
        Assert.True((await Post<CrewJobResult>(device, "/api/v1/crew/me/jobs/" + id + "/notes", note)).Replayed);
        var photo = new CrewJobPhotoRequest("synthetic-photo-key", "image/png", Convert.ToBase64String(CrewPhotoTests.Png()), true, result.Job.Version);
        result = await Post<CrewJobResult>(device, "/api/v1/crew/me/jobs/" + id + "/photos", photo); Assert.Single(result.Job.Photos); Assert.True(result.Job.Photos[0].MetadataStripped);
        Assert.True((await Post<CrewJobResult>(device, "/api/v1/crew/me/jobs/" + id + "/photos", photo)).Replayed);
        var reviewed = await supervisor.GetFromJsonAsync<CrewJob>("/api/v1/crew/jobs/" + id); Assert.Single(reviewed!.Notes);
        var bytes = await supervisor.GetByteArrayAsync("/api/v1/crew/jobs/" + id + "/photos/" + result.Job.Photos[0].Id); Assert.DoesNotContain("synthetic-gps-metadata", System.Text.Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync("/api/v1/crew/jobs/" + id)).StatusCode);
        var before = await app.Services.GetRequiredService<IncidentStore>().GetAsync(id, default);
        result = await Post<CrewJobResult>(device, "/api/v1/crew/me/jobs/" + id + "/status", new CrewJobStatusRequest("synthetic-complete-key", "complete", "", result.Job.Version));
        Assert.Equal("complete", result.Job.CrewStatus); Assert.Equal("InProgress", result.Job.Status);
        var after = await app.Services.GetRequiredService<IncidentStore>().GetAsync(id, default); Assert.Equal(before!.CommsDrafts.Count, after!.CommsDrafts.Count);
        Assert.True((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
    }
    [Fact]
    public async Task WorkerJobsAndHistoryDoNotCrossCrewsWorkersOrMunicipalities()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var supervisor = app.CreateClient(); var id = await Assigned(app, supervisor);
        var issued = await Pair(supervisor, "synthetic-roads-01-a"); using var device = app.CreateClient(); device.DefaultRequestHeaders.Authorization = new("Bearer", issued.Token);
        Assert.Empty((await device.GetFromJsonAsync<CrewJobs>("/api/v1/crew/me/jobs"))!.Jobs);
        Assert.Equal(HttpStatusCode.NotFound, (await device.GetAsync("/api/v1/crew/me/jobs/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await device.PostAsJsonAsync("/api/v1/crew/me/jobs/" + id + "/notes", new CrewJobNoteRequest("synthetic-foreign-note", "Synthetic forbidden foreign note", 1))).StatusCode);
        await app.Services.GetRequiredService<TenantStore>().SaveAsync(new("synthetic-coast", "Synthetic coast municipality", "coast", ["Water and Sanitation"], new(), "Synthetic tenant test"), "Synthetic supervisor", default);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync("/m/coast/api/v1/crew/me")).StatusCode);
        using var coast = await RbacTests.RoleClient(app, "supervisor", tenant: "synthetic-coast");
        Assert.Equal(HttpStatusCode.NotFound, (await coast.GetAsync("/api/v1/crew/jobs/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await coast.GetAsync("/api/v1/crew/me/locations")).StatusCode);
    }
    [Fact]
    public async Task SupervisorMapShowsConsentGatedPingsWhileDispatcherAndAuditorDoNotSeeThem()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var supervisor = app.CreateClient(); var issued = await Pair(supervisor);
        using var device = app.CreateClient(); device.DefaultRequestHeaders.Authorization = new("Bearer", issued.Token);
        await Post<CrewMe>(device, "/api/v1/crew/shifts/start", new CrewOperationRequest("synthetic-start-operation"));
        var me = await Post<CrewMe>(device, "/api/v1/crew/consent", new CrewConsentRequest("synthetic-consent-operation", CrewStore.NoticeVersion, true));
        await Post<CrewLocationResult>(device, "/api/v1/crew/locations", Ping(me, DateTimeOffset.UtcNow));
        var map = await supervisor.GetFromJsonAsync<JsonElement>("/api/v1/map/layers?layers=crews"); Assert.Equal(1, map.GetProperty("total").GetInt32());
        var props = map.GetProperty("features")[0].GetProperty("properties"); Assert.True(props.GetProperty("onShift").GetBoolean()); Assert.True(props.GetProperty("sharing").GetBoolean());
        Assert.DoesNotContain("workerId", map.GetRawText(), StringComparison.Ordinal);
        foreach (var role in new[] { "dispatcher", "auditor" })
        { using var client = await RbacTests.RoleClient(app, role); Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/api/v1/map/layers?layers=crews")).GetProperty("total").GetInt32()); }
        using var withdrawal = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/crew/consent") { Content = JsonContent.Create(new CrewOperationRequest("synthetic-withdraw-operation")) };
        (await device.SendAsync(withdrawal)).EnsureSuccessStatusCode(); Assert.Equal(0, (await supervisor.GetFromJsonAsync<JsonElement>("/api/v1/map/layers?layers=crews")).GetProperty("total").GetInt32());
    }
    [Fact]
    public async Task SosStubIsHonestIdempotentAndCannotBypassLocationPrivacyGate()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var crew = await RbacTests.RoleClient(app, "crew", "water-01");
        var request = new CrewSosRequest("synthetic-sos-operation", "Synthetic worker safety exercise", null, false);
        var first = await Post<CrewSosResult>(crew, "/api/v1/crew/sos", request); var replay = await Post<CrewSosResult>(crew, "/api/v1/crew/sos", request);
        Assert.Equal(first.Id, replay.Id); Assert.Equal("AwaitingLocation", first.Status); Assert.True(first.Simulated); Assert.True(first.HumanDispatchRequired);
        Assert.Contains("no responders", first.Message, StringComparison.OrdinalIgnoreCase); Assert.Contains("10111", first.Message, StringComparison.Ordinal);
        Assert.Contains("10177", first.Message, StringComparison.Ordinal); Assert.Contains("112", first.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await crew.PostAsJsonAsync("/api/v1/crew/sos", request with { Location = new(31, -29.9), ShareLocation = true })).StatusCode);
    }
    [Fact]
    public async Task CrewDeviceCanRaiseItsOwnSosButCannotDispatchTrackOffShiftOrCancelAnotherDevice()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var supervisor = app.CreateClient();
        var issued = await Pair(supervisor); using var device = app.CreateClient(); device.DefaultRequestHeaders.Authorization = new("Bearer", issued.Token);
        var otherIssued = await Pair(supervisor, "synthetic-roads-01-a"); using var other = app.CreateClient(); other.DefaultRequestHeaders.Authorization = new("Bearer", otherIssued.Token);
        const string notice = "CivicOps SOS alerts your municipality's control room. In a life-threatening emergency always call 10111 (police), 10177 (ambulance/fire) or 112 from a cell phone.";
        var deniedPoint = await device.PostAsJsonAsync("/api/v1/sos", new { operationId = "synthetic-device-coords", channel = "crew-android", note = "Synthetic worker safety exercise", contact = (string?)null, addressOrLandmark = "Synthetic training gate", silent = true, location = new { longitude = 30.985, latitude = -29.915 }, accuracyMeters = 12.0, batteryPercent = (int?)null });
        Assert.Equal(HttpStatusCode.Forbidden, deniedPoint.StatusCode); Assert.Contains("visible sharing indicator", await deniedPoint.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var created = await Post<JsonElement>(device, "/api/v1/sos", new { operationId = "synthetic-device-sos-01", channel = "crew-android", note = "Synthetic worker safety exercise", contact = (string?)null, addressOrLandmark = "Synthetic training gate", silent = true, location = (object?)null, accuracyMeters = (double?)null, batteryPercent = (int?)null });
        var alert = created.GetProperty("alert"); var id = alert.GetProperty("id").GetString()!;
        Assert.Equal("Triggered", alert.GetProperty("status").GetString()); Assert.True(alert.GetProperty("silent").GetBoolean()); Assert.Equal(notice, created.GetProperty("emergencyNotice").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await device.GetAsync("/api/v1/sos/queue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostAsJsonAsync("/api/v1/sos/" + id + "/acknowledge", new { operationId = "synthetic-device-ack-01", note = "Synthetic device must not acknowledge." })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync("/api/v1/sos/" + id + "/cancel", new { operationId = "synthetic-device-foreign-cancel", note = "Synthetic other device." })).StatusCode);
        var offShift = await device.PostAsJsonAsync("/api/v1/sos/" + id + "/location", new { operationId = "synthetic-device-off-shift", location = new { longitude = 30.985, latitude = -29.915 }, accuracyMeters = 12.0, addressOrLandmark = (string?)null, visibleIndicator = true, consentId = "synthetic-missing-consent" });
        Assert.Equal(HttpStatusCode.Forbidden, offShift.StatusCode);
        var landmark = await Post<JsonElement>(device, "/api/v1/sos/" + id + "/location", new { operationId = "synthetic-device-landmark", location = (object?)null, accuracyMeters = (double?)null, addressOrLandmark = "Synthetic landmark only", visibleIndicator = false, consentId = (string?)null });
        Assert.Equal("Synthetic landmark only", landmark.GetProperty("alert").GetProperty("addressOrLandmark").GetString());
        await Post<CrewMe>(device, "/api/v1/crew/shifts/start", new CrewOperationRequest("synthetic-sos-start"));
        var me = await Post<CrewMe>(device, "/api/v1/crew/consent", new CrewConsentRequest("synthetic-sos-consent", CrewStore.NoticeVersion, true));
        var hidden = await device.PostAsJsonAsync("/api/v1/sos/" + id + "/location", new { operationId = "synthetic-device-hidden-point", location = new { longitude = 30.985, latitude = -29.915 }, accuracyMeters = 12.0, addressOrLandmark = (string?)null, visibleIndicator = false, consentId = me.Consent!.Id });
        Assert.Equal(HttpStatusCode.Forbidden, hidden.StatusCode);
        var shared = await Post<JsonElement>(device, "/api/v1/sos/" + id + "/location", new { operationId = "synthetic-device-visible-point", location = new { longitude = 30.985, latitude = -29.915 }, accuracyMeters = 12.0, addressOrLandmark = (string?)null, visibleIndicator = true, consentId = me.Consent.Id });
        Assert.Equal(-29.915, shared.GetProperty("alert").GetProperty("location").GetProperty("latitude").GetDouble());
        var cancelCreated = await Post<JsonElement>(device, "/api/v1/sos", new { operationId = "synthetic-device-cancel-sos", channel = "crew-android", note = "Synthetic cancellation exercise", contact = (string?)null, addressOrLandmark = "Synthetic cancel gate", silent = false, location = (object?)null, accuracyMeters = (double?)null, batteryPercent = (int?)null });
        var cancelId = cancelCreated.GetProperty("alert").GetProperty("id").GetString()!;
        var cancelled = await Post<JsonElement>(device, "/api/v1/sos/" + cancelId + "/cancel", new { operationId = "synthetic-device-owner-cancel", note = "Synthetic owner cancelled during the window." });
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
    }
    internal static async Task<CrewTokenIssued> Pair(HttpClient supervisor, string worker = "synthetic-water-01-a")
    {
        var pairing = await Post<CrewPairingIssued>(supervisor, "/api/v1/crew/pairing", new CrewPairingRequest(worker, "Synthetic Android field device", "Synthetic supervisor pairing approval"));
        var issued = await Post<CrewTokenIssued>(supervisor, "/api/v1/crew/pairing/exchange", new CrewPairingExchangeRequest(pairing.PairingId, pairing.Code, pairing.TenantId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await supervisor.PostAsJsonAsync("/api/v1/crew/pairing/exchange", new CrewPairingExchangeRequest(pairing.PairingId, pairing.Code, pairing.TenantId))).StatusCode); return issued;
    }
    internal static async Task<T> Post<T>(HttpClient client, string path, object request)
    { var response = await client.PostAsJsonAsync(path, request); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); return (await response.Content.ReadFromJsonAsync<T>())!; }
    internal static async Task<string> Assigned(TestApp app, HttpClient client)
    {
        var created = await FoundationTests.Create(client, new { description = "Synthetic water pipe leak assigned to water crew", area = "Synthetic crew area" });
        var id = created.GetProperty("id").GetString()!; var result = await app.Services.GetRequiredService<IncidentStore>().ChangeAsync(id, null, incident =>
        {
            incident.AssignedCrewId = "water-01"; incident.Status = IncidentStatus.Assigned; incident.NeedsHumanReview = false;
            incident.Approval = new("Synthetic supervisor", "Synthetic crew preparation", DateTimeOffset.UtcNow);
            incident.Audit.Add(new("synthetic.assignment-prepared", "Synthetic supervisor", "Synthetic test prepared a human-reviewed assignment", DateTimeOffset.UtcNow)); return null;
        }, default); Assert.Null(result.Error); return id;
    }
    private static CrewStore Store(TestApp app, TimeProvider clock) => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["CIVICOPS_DATA_DIR"] = app.DataDirectory, ["CIVICOPS_DEMO_MODE"] = "true" }).Build(), new SyntheticEnvironment(), app.Services.GetRequiredService<DurableStorage>(), clock,
        app.Services.GetRequiredService<WorkforceStore>(), app.Services.GetRequiredService<IncidentStore>(), new CaseEventStream());
    private static CrewActor Actor() => new("synthetic-water-01-a", "water-01", "synthetic-owner", "Synthetic named field worker", null);
    private static async Task<CrewMe> Ready(CrewStore store, CrewActor actor)
    { await store.StartAsync(actor, new("synthetic-start-operation"), default); return await store.ConsentAsync(actor, new("synthetic-consent-operation", CrewStore.NoticeVersion, true), default); }
    private static CrewLocationRequest Ping(CrewMe me, DateTimeOffset at) => new("synthetic-location-operation", me.Shift?.Id ?? "shift-unavailable", me.Consent?.Id ?? "consent-unavailable", true, [new(30.975, -29.91, 20, at)]);
    private sealed class CrewClock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
}
