using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class ChannelKeyIntegrityTests
{
    private const string Resident = "+27000000555";
    private const string Municipality = "+27000000999";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] SyntheticFailures = ["missing", "replaced", "invalid-base64", "wrong-length", "null", "malformed-json"];
    private static readonly string[] OwnerFailures = ["replaced", "missing"];

    public static IEnumerable<object[]> SyntheticKeyFailures()
    {
        foreach (var backend in StoreBackends.Available())
            foreach (var failure in SyntheticFailures) yield return [backend[0], failure];
    }
    public static IEnumerable<object[]> OwnerKeyFailures()
    {
        foreach (var backend in StoreBackends.Available())
            foreach (var failure in OwnerFailures) yield return [backend[0], failure];
    }

    [Theory]
    [MemberData(nameof(SyntheticKeyFailures))]
    public async Task MissingReplacedOrMalformedSyntheticKeysBlockIntakeSuppressionReadsAndReadinessWithoutChangingTheJournal(string backend, string failure)
    {
        using var selected = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<ChannelStore>(); var storage = app.Services.GetRequiredService<DurableStorage>();
        var stopped = await Prepare(store, "STOPALL");
        Assert.True(await store.IsSuppressedAsync(Resident, "sms", default)); await Healthy(client);
        var statePath = Path.Combine(app.DataDirectory, "channels.json");
        var keyPath = Path.Combine(app.DataDirectory, "synthetic-channel-key.json");
        var originalState = await storage.ReadAsync(statePath, default); Assert.NotNull(originalState);
        var originalKey = await storage.ReadAsync(keyPath, default); Assert.NotNull(originalKey);
        var keyId = JsonNode.Parse(originalState)!["contactKeyId"]!.GetValue<string>(); Assert.Matches("^[A-F0-9]{64}$", keyId);
        Assert.DoesNotContain(Resident, originalState, StringComparison.Ordinal);
        if (failure == "missing") await storage.DeleteAsync(keyPath, default);
        else await storage.WriteAsync(keyPath, failure switch
        {
            "replaced" => JsonSerializer.Serialize(NewKey(), Json),
            "invalid-base64" => JsonSerializer.Serialize("synthetic-invalid-base64!", Json),
            "wrong-length" => JsonSerializer.Serialize(Convert.ToBase64String(RandomNumberGenerator.GetBytes(31)), Json),
            "null" => "null",
            _ => "{synthetic malformed key document"
        }, default);
        using var restarted = RestartedStore(app);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.VerifyIntegrityAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.IsSuppressedAsync(Resident, "sms", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.GetAsync(stopped.ConversationId, "Synthetic named supervisor", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(restarted, "Synthetic changed-key intake must remain blocked"));
        await UnhealthyChannels(client);
        Assert.Equal(originalState, await storage.ReadAsync(statePath, default));
        if (failure == "missing") Assert.Null(await storage.ReadAsync(keyPath, default));
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        await storage.WriteAsync(keyPath, originalKey, default); await Healthy(client);
        Assert.True(await restarted.IsSuppressedAsync(Resident, "voice", default));
        var resumed = await Prepare(restarted, "HELP"); Assert.Equal(stopped.ConversationId, resumed.ConversationId);
        Assert.True((await restarted.GetAsync(resumed.ConversationId, "Synthetic named supervisor", default))!.Suppressed);
    }

    [Theory]
    [MemberData(nameof(OwnerKeyFailures))]
    public async Task RestartingWithAChangedOrMissingOwnerKeyCannotCreateNewThreadHashesOrSilentlyForgetSuppression(string backend, string failure)
    {
        using var selected = StoreBackends.Use(backend);
        var directory = Path.Combine(Path.GetTempPath(), "civicops-channel-key-tests-" + Guid.NewGuid().ToString("N"));
        var originalKey = NewKey(); string conversationId; string originalState;
        using (var initial = App())
        {
            initial.DataDirectory = directory; initial.KeepData = true; ConfigureOwner(initial, originalKey);
            using var client = initial.CreateClient(); var store = initial.Services.GetRequiredService<ChannelStore>();
            conversationId = (await Prepare(store, "STOPALL", simulated: false)).ConversationId;
            originalState = (await initial.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(directory, "channels.json"), default))!;
            Assert.DoesNotContain(originalKey, originalState, StringComparison.Ordinal); await Healthy(client);
        }
        using var changed = App(); changed.DataDirectory = directory; ConfigureOwner(changed, failure == "replaced" ? NewKey() : null);
        using var changedClient = changed.CreateClient(); var changedStore = changed.Services.GetRequiredService<ChannelStore>();
        await Assert.ThrowsAsync<InvalidDataException>(() => changedStore.VerifyIntegrityAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => changedStore.IsSuppressedAsync(Resident, "voice", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => changedStore.GetAsync(conversationId, "Synthetic named supervisor", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(changedStore, "Synthetic replacement-key intake must remain blocked", simulated: false));
        await UnhealthyChannels(changedClient);
        Assert.Equal(originalState, await changed.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(directory, "channels.json"), default));
        Assert.Empty(await changed.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task KeyFingerprintsAreTenantBoundAndReadinessDetectsTamperingInEitherMunicipality(string backend)
    {
        using var selected = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        const string coast = "synthetic-channel-key-coast";
        await app.Services.GetRequiredService<TenantStore>().SaveAsync(new(coast, "Synthetic key municipality", "channel-key-coast",
            ["Water and Sanitation"], new(), "Synthetic channel key readiness partition."), "Synthetic supervisor", default);
        var store = app.Services.GetRequiredService<ChannelStore>(); var storage = app.Services.GetRequiredService<DurableStorage>();
        using (TenantContext.Use("default")) await Prepare(store, "STOPALL");
        using (TenantContext.Use(coast)) await Prepare(store, "STOPALL");
        var defaultPath = Path.Combine(app.DataDirectory, "channels.json");
        var coastPath = Path.Combine(app.DataDirectory, "tenants", coast, "channels.json");
        var primary = JsonNode.Parse((await storage.ReadAsync(defaultPath, default))!)!;
        var coastal = JsonNode.Parse((await storage.ReadAsync(coastPath, default))!)!;
        Assert.NotEqual(primary["contactKeyId"]!.GetValue<string>(), coastal["contactKeyId"]!.GetValue<string>());
        Assert.NotEqual(primary["conversations"]![0]!["numberHash"]!.GetValue<string>(), coastal["conversations"]![0]!["numberHash"]!.GetValue<string>());
        await Healthy(client);
        foreach (var path in new[] { defaultPath, coastPath })
        {
            var original = await storage.ReadAsync(path, default); Assert.NotNull(original);
            var tampered = JsonNode.Parse(original)!; tampered["contactKeyId"] = new string('0', 64);
            await storage.WriteAsync(path, tampered.ToJsonString(), default); await UnhealthyChannels(client);
            Assert.Equal(tampered.ToJsonString(), await storage.ReadAsync(path, default));
            await storage.WriteAsync(path, original, default); await Healthy(client);
        }
        using (TenantContext.Use(coast)) Assert.True(await store.IsSuppressedAsync(Resident, "sms", default));
        using (TenantContext.Use("default")) Assert.True(await store.IsSuppressedAsync(Resident, "sms", default));
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task HealthyIntegrityProbesAreReadOnlyAndPreserveTheOriginalSyntheticKeyAndThread(string backend)
    {
        using var selected = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<ChannelStore>(); var storage = app.Services.GetRequiredService<DurableStorage>();
        var prepared = await Prepare(store, "HELP"); var statePath = Path.Combine(app.DataDirectory, "channels.json");
        var keyPath = Path.Combine(app.DataDirectory, "synthetic-channel-key.json");
        var original = await storage.ReadAsync(statePath, default); var key = await storage.ReadAsync(keyPath, default);
        Assert.NotNull(original); Assert.NotNull(key);
        using var restarted = RestartedStore(app); await restarted.VerifyIntegrityAsync(default); await restarted.VerifyIntegrityAsync(default);
        await Healthy(client); await Healthy(client);
        Assert.Equal(original, await storage.ReadAsync(statePath, default)); Assert.Equal(key, await storage.ReadAsync(keyPath, default));
        var fresh = await Prepare(restarted, "HELP"); Assert.Equal(prepared.ConversationId, fresh.ConversationId);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task UnusedDemoChannelsRemainReadyWithoutGeneratingAContactKeyOrJournal(string backend)
    {
        using var selected = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        await app.Services.GetRequiredService<ChannelStore>().VerifyIntegrityAsync(default); await Healthy(client);
        var storage = app.Services.GetRequiredService<DurableStorage>();
        Assert.Null(await storage.ReadAsync(Path.Combine(app.DataDirectory, "synthetic-channel-key.json"), default));
        Assert.Null(await storage.ReadAsync(Path.Combine(app.DataDirectory, "channels.json"), default));
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task DisabledUnusedProductionChannelsRemainReadyWithoutAnOwnerKey(string backend)
    {
        using var selected = StoreBackends.Use(backend); using var app = App();
        app.Settings["CIVICOPS_DEMO_MODE"] = "false"; app.Settings["CIVICOPS_CHANNEL_MODE"] = "disabled";
        using var client = app.CreateClient(); await app.Services.GetRequiredService<ChannelStore>().VerifyIntegrityAsync(default);
        await Healthy(client); var storage = app.Services.GetRequiredService<DurableStorage>();
        Assert.Null(await storage.ReadAsync(Path.Combine(app.DataDirectory, "synthetic-channel-key.json"), default));
        Assert.Null(await storage.ReadAsync(Path.Combine(app.DataDirectory, "channels.json"), default));
    }

    private static TestApp App() => new() { EnableBackgroundDelivery = false };
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static void ConfigureOwner(TestApp app, string? key)
    {
        app.Settings["CIVICOPS_DEMO_MODE"] = "false"; app.Settings["CIVICOPS_CHANNEL_MODE"] = "configured";
        app.Settings["CIVICOPS_CHANNEL_ENCRYPTION_KEY"] = key;
        app.Settings["CIVICOPS_CHANNEL_INGRESS_SECRET"] = "synthetic-channel-key-relay";
        app.Settings["CIVICOPS_CHANNEL_WEBHOOK_BASE_URL"] = "https://municipality.example.invalid";
    }
    private static ChannelStore RestartedStore(TestApp app)
    {
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        return new(configuration, new SyntheticEnvironment(), app.Services.GetRequiredService<DurableStorage>(),
            app.Services.GetRequiredService<IncidentStore>(), TimeProvider.System, new CaseEventStream(), new ChannelOptions(configuration));
    }
    private static Task<PreparedChannelIntake> Prepare(ChannelStore store, string text, bool simulated = true)
    {
        var message = new InboundMessage("sms", "twilio-sms", Resident, Municipality, text, "en", DateTimeOffset.UtcNow,
            "synthetic-key-message-" + Guid.NewGuid().ToString("N"), PrivacyConsentAccepted: true, PrivacyConsentVersion: PrivacyConsent.Version);
        return store.PrepareAsync(message, IncidentStore.Hash(JsonSerializer.Serialize(message, Json)), Guid.NewGuid().ToString("N"), simulated, default);
    }
    private static async Task Healthy(HttpClient client)
    {
        using var response = await client.GetAsync("/health/ready"); response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(result.GetProperty("ready").GetBoolean());
        Assert.Equal("healthy", result.GetProperty("checks").GetProperty("channels").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
    private static async Task UnhealthyChannels(HttpClient client)
    {
        using var response = await client.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.False(result.GetProperty("ready").GetBoolean());
        Assert.Equal("healthy", result.GetProperty("checks").GetProperty("storage").GetString());
        Assert.Equal("healthy", result.GetProperty("checks").GetProperty("geo").GetString());
        Assert.Equal("healthy", result.GetProperty("checks").GetProperty("crew").GetString());
        Assert.Equal("unhealthy", result.GetProperty("checks").GetProperty("channels").GetString());
        Assert.DoesNotContain(Resident, result.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("contactKeyId", result.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }
}
