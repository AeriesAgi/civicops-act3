using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CivicOps.Tests;

public sealed class ChannelReviewGuardTests
{
    private const string Resident = "+27000000444";

    [Theory]
    [InlineData("message")][InlineData("provider")][InlineData("channel")][InlineData("from")]
    [InlineData("to")][InlineData("text")][InlineData("languageGuess")][InlineData("providerMessageId")]
    public void NullRequiredIntakeFieldsAreRejectedBeforeDereference(string field)
    {
        var message = Message();
        message = field switch
        {
            "message" => null!, "provider" => message with { Provider = null! }, "channel" => message with { Channel = null! },
            "from" => message with { From = null! }, "to" => message with { To = null! }, "text" => message with { Text = null! },
            "languageGuess" => message with { LanguageGuess = null! }, _ => message with { ProviderMessageId = null! }
        };
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => ChannelStore.Validate(message)).StatusCode);
    }

    [Fact]
    public void NullProviderMediaReferencesCannotCrashIntake() =>
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => ChannelStore.Validate(Message() with { MediaReferences = [null!] })).StatusCode);

    [Theory]
    [InlineData("reply")][InlineData("reply-variables")][InlineData("preview")][InlineData("preview-variables")]
    [InlineData("edit")][InlineData("edit-text")]
    public async Task NullReviewRequestsAreBadRequestsRatherThanUnhandledFailures(string kind)
    {
        using var app = App(); using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<ChannelStore>();
        Task Execute() => kind switch
        {
            "reply" => store.PrepareReplyAsync("synthetic-conversation", null!, "Synthetic reviewer", default),
            "reply-variables" => store.PrepareReplyAsync("synthetic-conversation", new("channel.notice", "en", null!, 1, 1,
                "Synthetic reviewed notice", "synthetic-reviewed-operation"), "Synthetic reviewer", default),
            "preview" => store.PreviewAsync(null!, default),
            "preview-variables" => store.PreviewAsync(new("case.received", "en", null!), default),
            "edit" => store.EditTemplateAsync("case.received", "en", null!, "Synthetic administrator", default),
            _ => store.EditTemplateAsync("case.received", "en", new(null!, 1, "Synthetic reviewed revision"), "Synthetic administrator", default)
        };
        Assert.Equal(400, (await Assert.ThrowsAsync<ChannelRequestException>(Execute)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task StopAllAndChannelStartPreserveOptOutForChannelsThatHaveNeverBeenOpened(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<ChannelStore>();
        await Prepare(store, Message(text: "STOPALL"));
        await Prepare(store, Message(text: "START"));
        Assert.False(await store.IsSuppressedAsync(Resident, "sms", default));
        foreach (var channel in new[] { "whatsapp", "voice", "web", "open311" })
            Assert.True(await store.IsSuppressedAsync(Resident, channel, default));
        foreach (var provider in new[] { "whatsapp", "sip" })
        {
            var opened = await Prepare(store, Message(provider, "HELP"));
            var conversation = (await store.GetAsync(opened.ConversationId, "Synthetic reviewer", default))!;
            Assert.True(conversation.Suppressed);
            var error = await Assert.ThrowsAsync<ChannelRequestException>(() => store.PrepareReplyAsync(conversation.Id,
                new("channel.notice", "en", [], 1, conversation.Version, "Synthetic human reviewed notice", Guid.NewGuid().ToString("N")),
                "Synthetic reviewer", default));
            Assert.Equal(409, error.StatusCode); Assert.Contains("suppressed", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(IncidentStatus.PendingApproval, false, false)]
    [InlineData(IncidentStatus.PendingApproval, true, false)]
    [InlineData(IncidentStatus.PendingApproval, false, true)]
    [InlineData(IncidentStatus.Approved, true, false)]
    [InlineData(IncidentStatus.Assigned, true, true)]
    [InlineData(IncidentStatus.InProgress, true, true)]
    [InlineData(IncidentStatus.Resolved, true, true)]
    [InlineData(IncidentStatus.Closed, true, true)]
    public async Task ResidentLocationCannotRelocateAHumanReviewedResponse(IncidentStatus status, bool approved, bool assigned)
    {
        using var app = App(); using var client = app.CreateClient();
        var service = app.Services.GetRequiredService<ChannelService>(); var incidents = app.Services.GetRequiredService<IncidentStore>();
        var originalPoint = new GeoPoint(31.02, -29.85); var suppliedPoint = new GeoPoint(28.04, -26.2);
        var original = Assert.Single(await Receive(service, Message("whatsapp") with { Location = originalPoint, LocationShared = true }));
        var originalCase = (await incidents.GetAsync(original.ReferenceNumber!, default))!;
        var changed = await incidents.ChangeAsync(originalCase.Id, originalCase.Version, incident =>
        {
            incident.Status = status; incident.Approval = approved ? new("Synthetic named supervisor", "Synthetic reviewed response", DateTimeOffset.UtcNow) : null;
            incident.AssignedCrewId = assigned ? "synthetic-crew" : null; incident.AssignedCrewName = assigned ? "Synthetic crew" : null;
            incident.NeedsHumanReview = !approved;
            incident.Audit.Add(new("synthetic.review", "Synthetic named supervisor", "Synthetic guard fixture establishes the reviewed response state.", DateTimeOffset.UtcNow));
            return null;
        }, default);
        Assert.Null(changed.Error); originalCase = changed.Incident!;
        var followup = Assert.Single(await Receive(service, Location(suppliedPoint)));
        var retained = (await incidents.GetAsync(original.ReferenceNumber!, default))!;
        if (status == IncidentStatus.PendingApproval && !approved && !assigned)
        {
            Assert.Equal(original.ReferenceNumber, followup.ReferenceNumber); Assert.Equal(suppliedPoint, retained.Location);
            Assert.Null(retained.Approval); Assert.Null(retained.AssignedCrewId); Assert.True(retained.NeedsHumanReview);
            Assert.Single(await incidents.ListAsync(default));
        }
        else
        {
            Assert.NotEqual(original.ReferenceNumber, followup.ReferenceNumber); Assert.Equal(originalPoint, retained.Location);
            Assert.Equal(originalCase.Version, retained.Version); Assert.Equal(originalCase.Status, retained.Status);
            Assert.Equal(originalCase.Approval, retained.Approval); Assert.Equal(originalCase.AssignedCrewId, retained.AssignedCrewId);
            Assert.Equal(originalCase.WardId, retained.WardId); Assert.Equal(originalCase.NeedsHumanReview, retained.NeedsHumanReview);
            var pending = (await incidents.GetAsync(followup.ReferenceNumber!, default))!;
            Assert.Equal(suppliedPoint, pending.Location); Assert.Equal(IncidentStatus.PendingApproval, pending.Status);
            Assert.True(pending.NeedsHumanReview); Assert.Null(pending.Approval); Assert.Null(pending.AssignedCrewId);
            Assert.Equal(2, (await incidents.ListAsync(default)).Count);
        }
    }

    [Fact]
    public async Task LocationPreparationDoesNotGrantAuthorityOverAnApprovalRecordedBeforeCompletion()
    {
        using var app = App(); using var client = app.CreateClient();
        var service = app.Services.GetRequiredService<ChannelService>(); var store = app.Services.GetRequiredService<ChannelStore>();
        var incidents = app.Services.GetRequiredService<IncidentStore>(); var coordinator = app.Services.GetRequiredService<IncidentCoordinator>();
        var point = new GeoPoint(31.02, -29.85);
        var original = Assert.Single(await Receive(service, Message("whatsapp") with { Location = point, LocationShared = true }));
        var followup = Location(new(28.04, -26.2)); var prepared = await Prepare(store, followup);
        Assert.Equal(original.ReferenceNumber, prepared.ExistingCaseReference);
        var approved = await coordinator.ApproveAsync(original.ReferenceNumber!, new("Synthetic named supervisor", "Synthetic approved response"),
            "Synthetic named supervisor", default);
        Assert.Null(approved.Error);
        var result = Assert.Single(await Receive(service, followup));
        Assert.NotEqual(original.ReferenceNumber, result.ReferenceNumber);
        var retained = (await incidents.GetAsync(original.ReferenceNumber!, default))!;
        Assert.Equal(point, retained.Location); Assert.Equal(approved.Incident!.Approval, retained.Approval);
    }

    [Fact]
    public async Task InterruptedSeparateLocationIntakeKeepsItsCaseIdentityAcrossLaterWorkflowChanges()
    {
        using var app = App(); using var client = app.CreateClient();
        var service = app.Services.GetRequiredService<ChannelService>(); var store = app.Services.GetRequiredService<ChannelStore>();
        var incidents = app.Services.GetRequiredService<IncidentStore>(); var coordinator = app.Services.GetRequiredService<IncidentCoordinator>();
        var first = Assert.Single(await Receive(service, Message("whatsapp")));
        var approved = await coordinator.ApproveAsync(first.ReferenceNumber!, new("Synthetic named supervisor", "Synthetic reviewed response"),
            "Synthetic named supervisor", default);
        Assert.Null(approved.Error);
        var followup = Location(new(28.04, -26.2)); var prepared = await Prepare(store, followup);
        Assert.NotNull(prepared.Request);
        var separate = await coordinator.CreateAsync(prepared.Request, "channel:" + prepared.MessageKey, default);
        Assert.True(separate.Created); Assert.Null(separate.Error);
        // Simulate a process interruption before CompleteAsync, then a subsequent reviewed workflow change.
        var reclassified = await coordinator.OverrideWorkOrderTypeAsync(first.ReferenceNumber!, new("ROADS.TRAFFIC_SIGNAL_FAULT", "Synthetic reassessment"),
            "Synthetic named supervisor", default);
        Assert.Null(reclassified.Error); Assert.Equal(IncidentStatus.PendingApproval, reclassified.Incident!.Status);
        var replay = Assert.Single(await Receive(service, followup));
        Assert.Equal(separate.Incident.ReferenceNumber, replay.ReferenceNumber);
        Assert.NotEqual(first.ReferenceNumber, replay.ReferenceNumber); Assert.Equal(2, (await incidents.ListAsync(default)).Count);
        var repeated = Assert.Single(await Receive(service, followup)); Assert.True(repeated.Replayed);
        Assert.Equal(replay.ReferenceNumber, repeated.ReferenceNumber); Assert.Equal(2, (await incidents.ListAsync(default)).Count);
    }

    [Fact]
    public async Task InterruptedLocationLinkKeepsItsReceiptAfterAHumanApprovesTheUpdatedDraft()
    {
        using var app = App(); using var client = app.CreateClient();
        var service = app.Services.GetRequiredService<ChannelService>(); var store = app.Services.GetRequiredService<ChannelStore>();
        var incidents = app.Services.GetRequiredService<IncidentStore>(); var coordinator = app.Services.GetRequiredService<IncidentCoordinator>();
        var first = Assert.Single(await Receive(service, Message("whatsapp")));
        var followup = Location(new(28.04, -26.2)); var prepared = await Prepare(store, followup);
        var linked = await incidents.ChangeAsync(first.ReferenceNumber!, null, incident =>
        {
            incident.Location = followup.Location; incident.LocationShared = true;
            incident.Audit.Add(new("channels.intake." + prepared.MessageKey, "Synthetic verified provider",
                "Synthetic fixture models interrupted journal completion after location linkage.", DateTimeOffset.UtcNow));
            return null;
        }, default);
        Assert.Null(linked.Error);
        var approved = await coordinator.ApproveAsync(first.ReferenceNumber!, new("Synthetic named supervisor", "Synthetic reviewed new point"),
            "Synthetic named supervisor", default);
        Assert.Null(approved.Error);
        var replay = Assert.Single(await Receive(service, followup));
        Assert.Equal(first.ReferenceNumber, replay.ReferenceNumber); Assert.Single(await incidents.ListAsync(default));
        var retained = (await incidents.GetAsync(first.ReferenceNumber!, default))!;
        Assert.Equal(approved.Incident!.Version, retained.Version); Assert.Equal(approved.Incident.Approval, retained.Approval);
        Assert.Equal(followup.Location, retained.Location); Assert.False(retained.NeedsHumanReview);
    }

    [Theory]
    [InlineData("en")][InlineData("af")][InlineData("zu")]
    public async Task CurrentEditedNoticesAndReceiptsRenderAllReviewedVariablesWithoutBreakingCaseLinkage(string language)
    {
        using var app = App(); using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<ChannelStore>(); var service = app.Services.GetRequiredService<ChannelService>();
        var notice = await store.EditTemplateAsync("channel.notice", language,
            new(ChannelCommands.Notice(language) + " Synthetic reviewed wording: {reference}; {update}; {request}.", 1, "Synthetic municipal notice review"),
            "Synthetic named administrator", default);
        var received = await store.EditTemplateAsync("case.received", language,
            new("CivicOps {reference}: {update} Please provide {request}.", 1, "Synthetic municipal receipt review"), "Synthetic named administrator", default);
        var result = Assert.Single(await Receive(service, Message() with { LanguageGuess = language }));
        Assert.NotNull(result.ReferenceNumber);
        var conversation = (await store.GetAsync(result.ConversationId, "Synthetic reviewer", default))!;
        var noticeDraft = Assert.Single(conversation.Messages, message => message.TemplateKey == "channel.notice");
        Assert.Equal(notice.Version, noticeDraft.TemplateVersion); Assert.Contains("Synthetic reviewed wording", noticeDraft.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{", noticeDraft.Text, StringComparison.Ordinal);
        var receipt = Assert.Single(conversation.Messages, message => message.TemplateKey == "case.received");
        Assert.Equal(received.Version, receipt.TemplateVersion); Assert.Contains(result.ReferenceNumber, receipt.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{", receipt.Text, StringComparison.Ordinal); Assert.Equal("draft", receipt.Direction);
        Assert.DoesNotContain(conversation.Messages, message => message.Direction == "outbound");
        Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }

    [Theory]
    [InlineData("channel.notice")][InlineData("case.received")]
    public async Task AutomaticDraftTemplateExpansionMustBeBoundedBeforeAnAdministratorCanSaveIt(string key)
    {
        using var app = App(); using var client = app.CreateClient(); var store = app.Services.GetRequiredService<ChannelStore>();
        var text = (key == "channel.notice" ? PrivacyConsent.Version + " " : "") + string.Concat(Enumerable.Repeat("{update}", 350));
        Assert.Equal(400, (await Assert.ThrowsAsync<ChannelRequestException>(() => store.EditTemplateAsync(key, "en",
            new(text, 1, "Synthetic oversized draft guard"), "Synthetic named administrator", default))).StatusCode);
        Assert.Equal(1, Assert.Single(await store.TemplatesAsync(default), template => template.Key == key && template.Language == "en").Version);
    }

    [Fact]
    public async Task HumanTemplateAndSendReasonsDoNotCopyDetectableResidentContactsIntoImmutableAudit()
    {
        using var app = App(); using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<ChannelStore>(); var service = app.Services.GetRequiredService<ChannelService>();
        var result = Assert.Single(await Receive(service, Message()));
        const string email = "review-fixture@example.invalid";
        var reason = "Synthetic reviewer records contact " + Resident + " and " + email + " before approving the purpose.";
        await store.EditTemplateAsync("case.received", "en", new("CivicOps received {reference}; awaiting human review.", 1, reason),
            "Synthetic named administrator", default);
        var conversation = (await store.GetAsync(result.ConversationId, "Synthetic reviewer", default))!;
        await store.PrepareReplyAsync(conversation.Id, new("case.received", "en", new() { ["reference"] = result.ReferenceNumber! },
            2, conversation.Version, reason, Guid.NewGuid().ToString("N")), "Synthetic named reviewer", default);
        var state = (await app.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(app.DataDirectory, "channels.json"), default))!;
        using var document = JsonDocument.Parse(state);
        foreach (var action in new[] { "channels.template.edited", "channels.send.approved" })
        {
            var entry = Assert.Single(document.RootElement.GetProperty("audit").EnumerateArray(), item => item.GetProperty("action").GetString() == action);
            var detail = entry.GetProperty("detail").GetString()!;
            Assert.DoesNotContain(Resident, detail, StringComparison.Ordinal); Assert.DoesNotContain(email, detail, StringComparison.Ordinal);
            Assert.Contains("PHONE REDACTED", detail, StringComparison.Ordinal); Assert.Contains("EMAIL REDACTED", detail, StringComparison.Ordinal);
            Assert.Contains("approving the purpose", detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProviderReadinessDistinguishesConfiguredIngressFromUnavailableOutboundTransports()
    {
        using var app = App(); using var client = app.CreateClient();
        var configuration = NativeConfiguration(app.DataDirectory); using var service = Service(app, configuration);
        var readiness = service.Readiness(); Assert.True(readiness.ExternalSendsEnabled);
        Assert.All(readiness.Channels, item => Assert.Equal("configured", item.Mode));
        foreach (var provider in new[] { "twilio-voice", "sip" })
            Assert.Contains("no outbound transport", Assert.Single(readiness.Channels, item => item.Provider == provider).Detail, StringComparison.Ordinal);
        foreach (var provider in new[] { "clickatell", "whatsapp", "elevenlabs" })
            Assert.Contains("credentials are incomplete", Assert.Single(readiness.Channels, item => item.Provider == provider).Detail, StringComparison.Ordinal);
        Assert.Contains("explicitly enabled", Assert.Single(readiness.Channels, item => item.Provider == "twilio-sms").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalTransportReadinessCannotBeTrueWhenOnlyInboundVoiceAdaptersAreConfigured()
    {
        using var app = App(); using var client = app.CreateClient();
        var configuration = NativeConfiguration(app.DataDirectory); configuration["CIVICOPS_TWILIO_AUTH_TOKEN"] = "";
        using var service = Service(app, configuration); var readiness = service.Readiness();
        Assert.False(readiness.ExternalSendsEnabled);
        Assert.Equal("configured", Assert.Single(readiness.Channels, item => item.Provider == "sip").Mode);
        Assert.Equal("disabled", Assert.Single(readiness.Channels, item => item.Provider == "twilio-sms").Mode);
    }

    [Theory]
    [InlineData("missing")][InlineData("invalid-json")][InlineData("null")][InlineData("invalid-base64")][InlineData("short-key")]
    public async Task CorruptOrMissingSyntheticKeysFailClosedWithoutReplacingResidentIdentity(string kind)
    {
        using var app = App(); using var client = app.CreateClient(); var store = app.Services.GetRequiredService<ChannelStore>();
        await Prepare(store, Message(text: "STOPALL"));
        var storage = app.Services.GetRequiredService<DurableStorage>(); var path = Path.Combine(app.DataDirectory, "synthetic-channel-key.json");
        var payload = kind switch { "invalid-json" => "synthetic-malformed-key", "null" => "null", "invalid-base64" => "\"synthetic-invalid-base64\"",
            "short-key" => JsonSerializer.Serialize(Convert.ToBase64String(new byte[16])), _ => "" };
        if (kind == "missing") await storage.DeleteAsync(path, default); else await storage.WriteAsync(path, payload, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.VerifyIntegrityAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.IsSuppressedAsync(Resident, "sms", default));
        Assert.Equal(kind == "missing" ? null : payload, await storage.ReadAsync(path, default));
    }

    [Fact]
    public async Task RetentionDiscoveryFailureWaitsForTheNextPassAndRecoversWithoutLoggingPayloads()
    {
        using var app = App(); using var client = app.CreateClient();
        var storage = app.Services.GetRequiredService<DurableStorage>(); var path = Path.Combine(app.DataDirectory, "tenants.json");
        var original = (await storage.ReadAsync(path, default))!;
        await storage.WriteAsync(path, "synthetic-invalid-directory-payload", default);
        var clock = new StepClock(); var configuration = Configuration(app.DataDirectory);
        using var tenants = new TenantStore(configuration, new SyntheticEnvironment(), app.Services.GetRequiredService<IncidentStore>(), clock, storage);
        var logger = new SafeLogger(); using var worker = new ChannelRetentionWorker(tenants, app.Services.GetRequiredService<ChannelStore>(), clock, logger);
        await worker.StartAsync(default); clock.Advance(await clock.NextDelayAsync());
        var next = await clock.NextDelayAsync(); Assert.False(worker.ExecuteTask!.IsCompleted);
        var warning = Assert.Single(logger.Entries); Assert.Null(warning.Error); Assert.DoesNotContain("payload", warning.Text, StringComparison.Ordinal);
        await storage.WriteAsync(path, original, default); clock.Advance(next);
        _ = await clock.NextDelayAsync(); Assert.False(worker.ExecuteTask.IsCompleted);
        Assert.Single(logger.Entries);
        var state = (await storage.ReadAsync(Path.Combine(app.DataDirectory, "channels.json"), default))!;
        Assert.Contains("channels.retention.purged", state, StringComparison.Ordinal);
        await worker.StopAsync(default);
    }

    private static TestApp App() => new() { EnableBackgroundDelivery = false };
    private static InboundMessage Message(string provider = "twilio-sms", string text = "Synthetic water leak outside the municipal guard fixture") =>
        new(ChannelOptions.Providers[provider], provider, Resident, "+27000000999", text, "en", DateTimeOffset.UtcNow,
            "synthetic-review-" + Guid.NewGuid().ToString("N"), Area: "Synthetic municipal fixture ward",
            PrivacyConsentAccepted: true, PrivacyConsentVersion: PrivacyConsent.Version);
    private static InboundMessage Location(GeoPoint point) => Message("whatsapp", "Resident shared a report location. Human review requires the report description.")
        with { Location = point, LocationShared = true };
    private static Task<PreparedChannelIntake> Prepare(ChannelStore store, InboundMessage message) =>
        store.PrepareAsync(message, IncidentStore.Hash(JsonSerializer.Serialize(message with { ReceivedAt = default })),
            Guid.NewGuid().ToString("N"), true, default);
    private static Task<IReadOnlyList<ChannelIntakeResult>> Receive(ChannelService service, InboundMessage message) =>
        service.ReceiveAsync([message], IncidentStore.Hash(JsonSerializer.Serialize(message)), message.Provider, Guid.NewGuid().ToString("N"), true, default);
    private static IConfiguration Configuration(string directory) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["CIVICOPS_DATA_DIR"] = directory, ["CIVICOPS_DEMO_MODE"] = "true", ["CIVICOPS_SECRET_PROVIDER"] = "env" }).Build();
    private static IConfiguration NativeConfiguration(string directory)
    {
        var configuration = Configuration(directory); configuration["CIVICOPS_DEMO_MODE"] = "false";
        configuration["CIVICOPS_CHANNEL_MODE"] = "configured"; configuration["CIVICOPS_CHANNEL_SEND_ENABLED"] = "true";
        configuration["CIVICOPS_CHANNEL_ENCRYPTION_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        configuration["CIVICOPS_CHANNEL_INGRESS_SECRET"] = "synthetic-relay-secret";
        configuration["CIVICOPS_CHANNEL_WEBHOOK_BASE_URL"] = "https://municipality.example.invalid";
        configuration["CIVICOPS_TWILIO_AUTH_TOKEN"] = "synthetic-twilio-secret";
        configuration["CIVICOPS_TWILIO_ACCOUNT_SID"] = "AC" + new string('a', 32);
        configuration["CIVICOPS_TWILIO_FROM_NUMBER"] = "+27000000999";
        configuration["CIVICOPS_CLICKATELL_CALLBACK_SECRET"] = "synthetic-clickatell-secret";
        configuration["CIVICOPS_CLICKATELL_FROM_NUMBER"] = "+27000000999";
        configuration["CIVICOPS_WHATSAPP_APP_SECRET"] = "synthetic-whatsapp-secret";
        configuration["CIVICOPS_WHATSAPP_PHONE_NUMBER_ID"] = "100000000001";
        configuration["ELEVENLABS_WEBHOOK_SECRET"] = "synthetic-elevenlabs-secret";
        configuration["CIVICOPS_SIP_CALLBACK_SECRET"] = "synthetic-sip-secret";
        return configuration;
    }
    private static ChannelService Service(TestApp app, IConfiguration configuration) => new(app.Services.GetRequiredService<ChannelStore>(),
        app.Services.GetRequiredService<IncidentCoordinator>(), app.Services.GetRequiredService<IncidentStore>(), new(configuration), configuration,
        app.Services.GetRequiredService<IHttpClientFactory>(), TimeProvider.System, app.Services.GetRequiredService<GeoStore>(), app.Services.GetRequiredService<SosStore>());
    private sealed class SafeLogger : ILogger<ChannelRetentionWorker>
    {
        public ConcurrentQueue<(string Text, Exception? Error)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((formatter(state, exception), exception));
    }
    private sealed class StepClock : TimeProvider
    {
        private readonly global::System.Threading.Channels.Channel<StepTimer> _delays = global::System.Threading.Channels.Channel.CreateUnbounded<StepTimer>();
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new StepTimer(callback, state); Assert.Equal(TimeSpan.FromHours(1), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period); Assert.True(_delays.Writer.TryWrite(timer)); return timer;
        }
        public Task<StepTimer> NextDelayAsync() => _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        public void Advance(StepTimer timer) { _now = _now.AddHours(1); timer.Fire(); }
    }
    private sealed class StepTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;
        public void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
