using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class ChannelPipelineTests
{
    private const string Resident = "+27000000111";
    private const string Municipality = "+27000000999";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("UNSUBSCRIBE")][InlineData("HOU OP")][InlineData("STAAK")][InlineData("TEKEN UIT")]
    [InlineData("YIMA")][InlineData("MISA")][InlineData("YEKA")][InlineData("UNGANGITHUMELI")]
    public void ReviewedOptOutTermsCannotBeMisroutedAsServiceReports(string text) =>
        Assert.Equal("STOP", ChannelCommands.Canonical("  " + text.ToLowerInvariant() + "  "));

    [Theory]
    [InlineData("en")][InlineData("af")][InlineData("zu")]
    public void EveryLanguageHasVersionedNoticeAndFourBoundedServiceTemplates(string language)
    {
        var templates = ChannelTemplates.Defaults().Where(item => item.Language == language).ToArray();
        Assert.Equal(5, templates.Length);
        Assert.Equal(5, templates.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(templates, item => { Assert.Equal(1, item.Version); ChannelTemplates.Validate(item.Text); });
        Assert.Contains(PrivacyConsent.Version, Assert.Single(templates, item => item.Key == "channel.notice").Text);
        foreach (var template in templates.Where(item => item.Key != "channel.notice"))
        {
            var rendered = ChannelTemplates.Render(template, Variables("CIV-20261009-00000011"));
            Assert.Contains("CIV-20261009-00000011", rendered);
            Assert.DoesNotContain("{reference}", rendered);
            Assert.DoesNotContain("{update}", rendered);
            Assert.DoesNotContain("{request}", rendered);
        }
        Assert.Contains("pending", templates[0].ReviewState, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{person}")][InlineData("{faceEmbedding}")][InlineData("{riskScore}")][InlineData("{{reference}}")]
    public void TemplateEditorRejectsUndocumentedVariablesAndMalformedBraces(string variable)
    {
        var error = Assert.Throws<ChannelRequestException>(() => ChannelTemplates.Validate("Synthetic report update " + variable));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void TemplateRenderingRejectsMissingVariablesAndRedactsDetectablePersonalData()
    {
        var template = Assert.Single(ChannelTemplates.Defaults(), item => item.Key == "case.update" && item.Language == "en");
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => ChannelTemplates.Render(template, new Dictionary<string, string>())).StatusCode);
        var rendered = ChannelTemplates.Render(template, new Dictionary<string, string>
        { ["reference"] = "CIV-20261009-00000011", ["update"] = "Synthetic email fixture@example.invalid and phone 0820000000" });
        Assert.Contains("EMAIL REDACTED", rendered); Assert.Contains("PHONE REDACTED", rendered);
        Assert.DoesNotContain("fixture@example.invalid", rendered); Assert.DoesNotContain("0820000000", rendered);
    }

    [Theory]
    [InlineData("faceEmbedding")][InlineData("voicePrint")][InlineData("personRiskScore")]
    public void NormalizedInboundContractRejectsUnreviewedIdentificationFields(string field)
    {
        var message = Message(); var value = JsonSerializer.Serialize(message, Json);
        value = value[..^1] + ",\"" + field + "\":[]} ";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<InboundMessage>(value, Json));
    }

    [Theory]
    [InlineData("https://media.example.invalid/frame.png")][InlineData("../synthetic-frame")][InlineData("synthetic/frame")]
    public void MediaContractAcceptsOpaqueProviderReferencesOnly(string reference) =>
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => ChannelStore.Validate(Message() with { MediaReferences = [reference] })).StatusCode);

    [Theory]
    [InlineData(false, true, 31.02, -29.85)][InlineData(true, false, 31.02, -29.85)]
    [InlineData(true, true, 0, 0)][InlineData(true, true, 181, -29.85)]
    public void CoordinatesRequireExplicitReportSharingAndTheSupportedGeographicEnvelope(bool shared, bool point, double longitude, double latitude)
    {
        var message = Message() with { LocationShared = shared, Location = point ? new(longitude, latitude) : null };
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => ChannelStore.Validate(message)).StatusCode);
    }

    [Theory]
    [InlineData("live")][InlineData("automatic")]
    public void UnreviewedChannelModesFailClosed(string mode) =>
        Assert.Throws<InvalidOperationException>(() => new ChannelOptions(Configuration("", ("CIVICOPS_CHANNEL_MODE", mode))));

    [Theory]
    [InlineData("0")][InlineData("31")][InlineData("365")]
    public void ChannelRetentionCannotExceedTheApprovedBound(string days) =>
        Assert.Throws<InvalidOperationException>(() => new ChannelOptions(Configuration("", ("CIVICOPS_CHANNEL_RETENTION_DAYS", days))));

    [Fact]
    public void ExplicitDemoCannotEnableNativeIngressOrExternalSendsEvenWithFilledConfiguration()
    {
        var options = new ChannelOptions(Configuration("", ("CIVICOPS_CHANNEL_MODE", "configured"),
            ("CIVICOPS_CHANNEL_SEND_ENABLED", "true"), ("CIVICOPS_CHANNEL_INGRESS_SECRET", "synthetic-relay-secret"),
            ("CIVICOPS_CHANNEL_ENCRYPTION_KEY", Key()), ("CIVICOPS_CHANNEL_WEBHOOK_BASE_URL", "https://municipality.example.invalid")));
        Assert.Equal("simulated", options.Mode); Assert.False(options.CanReceiveNative); Assert.False(options.SendsEnabled);
        Assert.Null(options.EncryptionKey); Assert.Null(options.IngressSecret);
    }

    [Theory]
    [InlineData("http://municipality.example.invalid")][InlineData("https://municipality.example.invalid?tenant=default")]
    [InlineData("https://fixture:fixture@municipality.example.invalid")][InlineData("https://municipality.example.invalid/#default")]
    public void NativeWebhookConfigurationRejectsUntrustedUrlForms(string url)
    {
        var options = NativeOptions(url); Assert.False(options.CanReceiveNative);
        Assert.Equal(503, Assert.Throws<ChannelRequestException>(() => options.PublicUrl("whatsapp", "")).StatusCode);
    }

    [Fact]
    public void ConfiguredNativeWebhookCannotBindToAnotherMunicipalityPath()
    {
        var options = NativeOptions("https://municipality.example.invalid/m/synthetic-coast");
        Assert.True(options.CanReceiveNative);
        Assert.Equal("https://municipality.example.invalid/m/synthetic-coast/api/v1/channels/webhooks/whatsapp", options.PublicUrl("whatsapp", "/m/synthetic-coast"));
        Assert.Equal(403, Assert.Throws<ChannelRequestException>(() => options.PublicUrl("whatsapp", "")).StatusCode);
    }

    [Fact]
    public void ContactEncryptionUsesRandomNoncesAndCryptographicallyBindsTheMunicipality()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var first = ChannelContactCrypto.Protect(key, Resident, "default");
        var second = ChannelContactCrypto.Protect(key, Resident, "default");
        Assert.NotEqual(first, second); Assert.Equal(Resident, ChannelContactCrypto.Unprotect(key, first, "default"));
        Assert.ThrowsAny<CryptographicException>(() => ChannelContactCrypto.Unprotect(key, first, "synthetic-coast"));
        Assert.NotEqual(ChannelContactCrypto.Hash(key, Resident, "default"), ChannelContactCrypto.Hash(key, Resident, "synthetic-coast"));
        var tampered = Convert.FromBase64String(first); tampered[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => ChannelContactCrypto.Unprotect(key, Convert.ToBase64String(tampered), "default"));
    }

    [Theory]
    [InlineData("twilio-sms")][InlineData("clickatell")][InlineData("whatsapp")]
    [InlineData("twilio-voice")][InlineData("sip")][InlineData("elevenlabs")]
    public async Task EveryReviewedProviderUsesTheSameTriageDuplicateAuditAndHumanCasePipeline(string provider)
    {
        using var app = App(); using var client = app.CreateClient(); var clock = new ChannelClock(); using var store = Store(app, clock);
        var message = Message(provider, at: clock.Now);
        var receipt = await Deliver(app, store, message);
        Assert.NotNull(receipt.ReferenceNumber); Assert.True(receipt.Simulated);
        var incident = await app.Services.GetRequiredService<IncidentStore>().GetAsync(receipt.ReferenceNumber!, default);
        Assert.NotNull(incident); Assert.Equal(message.Channel, incident.Source);
        Assert.Equal(IncidentStatus.PendingApproval, incident.Status); Assert.True(incident.NeedsHumanReview);
        Assert.Null(incident.Approval); Assert.Null(incident.AssignedCrewId);
        Assert.Contains(incident.AgentRuns, item => item.AgentName == "TriageAgent");
        Assert.Contains(incident.AgentRuns, item => item.AgentName == "DuplicateAgent");
        Assert.Contains(incident.Audit, item => item.Action == "agent-pipeline");
        var conversation = await store.GetAsync(receipt.ConversationId, "Synthetic test supervisor", default);
        Assert.NotNull(conversation); Assert.Contains(incident.ReferenceNumber, conversation.CaseReferences);
        Assert.Contains(conversation.Messages, item => item.Direction == "draft" && item.TemplateKey == "case.received");
        Assert.DoesNotContain(conversation.Messages, item => item.Direction == "outbound");
        var metric = Assert.Single(await store.MetricsAsync(default), item => item.Channel == message.Channel);
        Assert.Equal(1, metric.Inbound); Assert.Equal(1, metric.CasesCreated); Assert.Equal(0, metric.Outbound);
        Assert.True((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ProcessingConsentRequiresTheNoticeToBeHumanSentBeforeYesCanCreateThePendingCase(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var clock = new ChannelClock(); using var store = Store(app, clock);
        var first = await Prepare(store, Message(consent: false, at: clock.Now));
        Assert.Null(first.Request); Assert.NotNull(first.Result); Assert.Null(first.Result.ReferenceNumber);
        var conversation = await store.GetAsync(first.ConversationId, "Synthetic call-taker", default);
        Assert.NotNull(conversation); Assert.False(conversation.ConsentAccepted); Assert.True(conversation.HumanQueue);
        Assert.Contains(conversation.Messages, item => item.TemplateKey == "channel.notice" && item.Direction == "draft");
        var premature = await Prepare(store, Message(text: "YES", consent: false, at: clock.Now));
        Assert.Null(premature.Request); Assert.Contains("human", premature.Result!.Reply!, StringComparison.OrdinalIgnoreCase);
        conversation = await store.GetAsync(first.ConversationId, "Synthetic call-taker", default);
        var notice = await store.PrepareReplyAsync(first.ConversationId, Reply(conversation!, "channel.notice"), "Synthetic named call-taker", default);
        Assert.NotNull(notice.Message); await store.CompleteReplyAsync(notice, "simulated", "Synthetic named call-taker", default);
        var accepted = await Prepare(store, Message(text: "YES", consent: false, at: clock.Now));
        Assert.NotNull(accepted.Request); Assert.True(accepted.Request.PrivacyConsentAccepted);
        var created = await app.Services.GetRequiredService<IncidentCoordinator>().CreateAsync(accepted.Request, "synthetic-consent-" + accepted.MessageKey, default);
        Assert.Null(created.Error); Assert.True(created.Created);
        var result = await store.CompleteAsync(accepted, created.Incident, true, default);
        Assert.NotNull(result.ReferenceNumber); Assert.Equal(IncidentStatus.PendingApproval, created.Incident.Status);
    }

    [Theory]
    [InlineData("STOP")][InlineData("UNSUBSCRIBE")][InlineData("HOU OP")][InlineData("YIMA")]
    public async Task ChannelOptOutBlocksAHumanApprovedOutboundReply(string command)
    {
        using var app = App(); using var client = app.CreateClient(); var clock = new ChannelClock(); using var store = Store(app, clock);
        var linked = await Deliver(app, store, Message(at: clock.Now));
        await Prepare(store, Message(text: command, at: clock.Now));
        var conversation = await store.GetAsync(linked.ConversationId, "Synthetic supervisor", default);
        Assert.NotNull(conversation); Assert.True(conversation.Suppressed);
        var error = await Assert.ThrowsAsync<ChannelRequestException>(() => store.PrepareReplyAsync(conversation.Id,
            Reply(conversation, reference: linked.ReferenceNumber), "Synthetic named dispatcher", default));
        Assert.Equal(409, error.StatusCode); Assert.Contains("suppressed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Assert.Single(await store.MetricsAsync(default), item => item.Channel == "sms").Outbound);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task StopAllCrossesChannelsAndStartReactivatesOnlyTheChosenChannel(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var clock = new ChannelClock(); using var store = Store(app, clock);
        var sms = await Prepare(store, Message(text: "HELP", at: clock.Now));
        var whatsapp = await Prepare(store, Message("whatsapp", "HELP", at: clock.Now));
        var voice = await Prepare(store, Message("sip", "HELP", at: clock.Now));
        await Prepare(store, Message(text: "STOPALL", at: clock.Now));
        foreach (var id in new[] { sms.ConversationId, whatsapp.ConversationId, voice.ConversationId })
            Assert.True((await store.GetAsync(id, "Synthetic call-taker", default))!.Suppressed);
        await Prepare(store, Message("whatsapp", "START", at: clock.Now));
        Assert.False((await store.GetAsync(whatsapp.ConversationId, "Synthetic call-taker", default))!.Suppressed);
        Assert.True((await store.GetAsync(sms.ConversationId, "Synthetic call-taker", default))!.Suppressed);
        Assert.True((await store.GetAsync(voice.ConversationId, "Synthetic call-taker", default))!.Suppressed);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ChannelOptOutSurvivesAutomaticConversationRetentionAndFreshStoreRestart(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var clock = new ChannelClock(); using var store = Store(app, clock);
        await Prepare(store, Message(text: "STOP", at: clock.Now));
        clock.Now = clock.Now.AddDays(31); Assert.Equal(1, (await store.PurgeAsync("Synthetic retention worker", default)).Conversations);
        using var restarted = Store(app, clock); Assert.True(await restarted.IsSuppressedAsync(Resident, "sms", default));
        var recreated = await Prepare(restarted, Message(text: "HELP", at: clock.Now));
        var recreatedConversation = (await restarted.GetAsync(recreated.ConversationId, "Synthetic call-taker", default))!;
        Assert.True(recreatedConversation.Suppressed);
        Assert.Equal(409, (await Assert.ThrowsAsync<ChannelRequestException>(() => restarted.PrepareReplyAsync(recreated.ConversationId,
            Reply(recreatedConversation, "channel.notice"),
            "Synthetic named call-taker", default))).StatusCode);
        await Prepare(restarted, Message(text: "START", at: clock.Now));
        Assert.False(await restarted.IsSuppressedAsync(Resident, "sms", default));
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task NumberEncryptionAndConversationStateSurviveRestartAndRejectTamperingAndPartitionCopy(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var clock = new ChannelClock(); using var store = Store(app, clock);
        var prepared = await Prepare(store, Message(text: "HELP", at: clock.Now));
        var storage = app.Services.GetRequiredService<DurableStorage>(); var statePath = Path.Combine(app.DataDirectory, "channels.json");
        var original = (await storage.ReadAsync(statePath, default))!;
        Assert.DoesNotContain(Resident, original, StringComparison.Ordinal); Assert.DoesNotContain(Municipality, original, StringComparison.Ordinal);
        using var restarted = Store(app, clock);
        var conversation = await restarted.GetAsync(prepared.ConversationId, "Synthetic supervisor", default);
        Assert.NotNull(conversation); Assert.Equal("Resident ••••0111", conversation.ResidentLabel);
        await storage.WriteAsync(Path.Combine(app.DataDirectory, "tenants", "synthetic-coast", "channels.json"), original, default);
        using (TenantContext.Use("synthetic-coast"))
            await Assert.ThrowsAsync<InvalidDataException>(() => restarted.ListAsync("Synthetic coast supervisor", default));
        var corrupt = JsonNode.Parse(original)!; corrupt["conversations"]![0]!["suppressed"] = true;
        await storage.WriteAsync(statePath, corrupt.ToJsonString(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.GetAsync(prepared.ConversationId, "Synthetic supervisor", default));
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task SameNumberThreadsWithinAChannelButNeverSharesConversationOrSuppressionAcrossMunicipalities(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var clock = new ChannelClock(); using var store = Store(app, clock);
        var sms = await Prepare(store, Message(text: "HELP", at: clock.Now));
        var alternateProvider = await Prepare(store, Message("clickatell", "HELP", at: clock.Now));
        var whatsapp = await Prepare(store, Message("whatsapp", "HELP", at: clock.Now));
        Assert.Equal(sms.ConversationId, alternateProvider.ConversationId); Assert.NotEqual(sms.ConversationId, whatsapp.ConversationId);
        await Prepare(store, Message(text: "STOPALL", at: clock.Now));
        using (TenantContext.Use("synthetic-coast"))
        {
            var coast = await Prepare(store, Message(text: "HELP", at: clock.Now)); Assert.NotEqual(sms.ConversationId, coast.ConversationId);
            Assert.False((await store.GetAsync(coast.ConversationId, "Synthetic coast supervisor", default))!.Suppressed);
            Assert.Null(await store.GetAsync(sms.ConversationId, "Synthetic coast supervisor", default));
        }
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task IdenticalProviderMessageRetriesAreIdempotentButReusedNonceAndChangedContentFailClosed(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var clock = new ChannelClock(); using var store = Store(app, clock); var message = Message(text: "HELP", at: clock.Now);
        var first = await store.PrepareAsync(message, "synthetic-body-hash", "synthetic-first-nonce", true, default);
        Assert.Equal(401, (await Assert.ThrowsAsync<ChannelRequestException>(() => store.PrepareAsync(message,
            "synthetic-body-hash", "synthetic-first-nonce", true, default))).StatusCode);
        var retry = await store.PrepareAsync(message, "synthetic-body-hash", "synthetic-second-nonce", true, default);
        Assert.True(retry.Replayed); Assert.True(retry.Result!.Replayed); Assert.Equal(first.ConversationId, retry.ConversationId);
        Assert.Equal(409, (await Assert.ThrowsAsync<ChannelRequestException>(() => store.PrepareAsync(message with { Text = "Different fixture" },
            "synthetic-changed-body", "synthetic-third-nonce", true, default))).StatusCode);
        Assert.Equal(1, Assert.Single(await store.MetricsAsync(default), item => item.Channel == "sms").Inbound);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ExpiredPendingCaseReceiptsPurgeDescriptionAndVolunteeredCoordinatesWithoutRecreatingWork(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var clock = new ChannelClock(); using var store = Store(app, clock);
        var message = Message(text: "Synthetic unreconciled receipt sentinel", at: clock.Now) with { Location = new(31.02345, -29.85432), LocationShared = true };
        var prepared = await Prepare(store, message); Assert.NotNull(prepared.Request); Assert.NotNull(prepared.Request.Location);
        clock.Now = clock.Now.AddDays(31); await store.PurgeAsync("Synthetic retention worker", default);
        var state = (await app.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(app.DataDirectory, "channels.json"), default))!;
        Assert.DoesNotContain("unreconciled receipt sentinel", state, StringComparison.Ordinal);
        Assert.DoesNotContain("31.02345", state, StringComparison.Ordinal); Assert.DoesNotContain("29.85432", state, StringComparison.Ordinal);
        var receipt = JsonNode.Parse(state)!["receipts"]![prepared.MessageKey]!;
        Assert.Null(receipt["request"]); Assert.NotNull(receipt["result"]);
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ExpiredPendingSendReceiptsPurgeRenderedContentWhileKeepingRetriesNonSending(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var clock = new ChannelClock(); using var store = Store(app, clock);
        var linked = await Deliver(app, store, Message(at: clock.Now));
        var conversation = (await store.GetAsync(linked.ConversationId, "Synthetic named dispatcher", default))!;
        var request = Reply(conversation, "case.update", linked.ReferenceNumber) with
        { Variables = new Dictionary<string, string> { ["reference"] = linked.ReferenceNumber!, ["update"] = "Synthetic unreconciled outbound sentinel" } };
        var pending = await store.PrepareReplyAsync(conversation.Id, request, "Synthetic named dispatcher", default);
        Assert.NotNull(pending.Message); Assert.Contains("unreconciled outbound sentinel", pending.Message.Text, StringComparison.Ordinal);
        clock.Now = clock.Now.AddDays(31); Assert.Equal(1, (await store.PurgeAsync("Synthetic retention worker", default)).Conversations);
        var state = (await app.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(app.DataDirectory, "channels.json"), default))!;
        Assert.DoesNotContain("unreconciled outbound sentinel", state, StringComparison.Ordinal);
        var replay = await store.PrepareReplyAsync(conversation.Id, request, "Synthetic named dispatcher", default);
        Assert.Null(replay.Message); Assert.True(replay.Result.Replayed);
        Assert.DoesNotContain("unreconciled outbound sentinel", replay.Result.Text, StringComparison.Ordinal);
        Assert.Equal(0, Assert.Single(await store.MetricsAsync(default), item => item.Channel == "sms").Outbound);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task FreshHelpMessagesCannotExtendAnUnconsentedReportsIndependentRetentionClock(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var clock = new ChannelClock(); using var store = Store(app, clock);
        var first = await Prepare(store, Message(text: "Synthetic ageing pending report sentinel", consent: false, at: clock.Now));
        Assert.Null(first.Request);
        var conversation = (await store.GetAsync(first.ConversationId, "Synthetic named call-taker", default))!;
        var notice = await store.PrepareReplyAsync(conversation.Id, Reply(conversation, "channel.notice"), "Synthetic named call-taker", default);
        await store.CompleteReplyAsync(notice, "simulated", "Synthetic named call-taker", default);
        clock.Now = clock.Now.AddDays(31);
        var help = await Prepare(store, Message(text: "HELP", consent: false, at: clock.Now)); Assert.Equal(first.ConversationId, help.ConversationId);
        Assert.Equal(0, (await store.PurgeAsync("Synthetic retention worker", default)).Conversations);
        var state = (await app.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(app.DataDirectory, "channels.json"), default))!;
        Assert.DoesNotContain("ageing pending report sentinel", state, StringComparison.Ordinal);
        var accepted = await Prepare(store, Message(text: "YES", consent: false, at: clock.Now));
        Assert.Null(accepted.Request); Assert.Null(accepted.Result!.ReferenceNumber);
        Assert.Contains("Describe the municipal problem", accepted.Result.Reply!, StringComparison.Ordinal);
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }

    [Fact]
    public async Task TemplateRevisionAndConversationVersionsFenceStaleHumanSendsAndOperationIdReuse()
    {
        using var app = App(); using var client = app.CreateClient(); var clock = new ChannelClock(); using var store = Store(app, clock);
        var linked = await Deliver(app, store, Message(at: clock.Now));
        var conversation = (await store.GetAsync(linked.ConversationId, "Synthetic named dispatcher", default))!;
        var stale = Reply(conversation, reference: linked.ReferenceNumber);
        var edited = await store.EditTemplateAsync("case.received", "en", new("CivicOps recorded {reference}; a human reviews the synthetic report.", 1,
            "Synthetic municipal notice revision"), "Synthetic named administrator", default);
        Assert.Equal(2, edited.Version);
        Assert.Equal(409, (await Assert.ThrowsAsync<ChannelRequestException>(() => store.PrepareReplyAsync(conversation.Id, stale, "Synthetic named dispatcher", default))).StatusCode);
        var request = stale with { ExpectedTemplateVersion = 2 };
        var pending = await store.PrepareReplyAsync(conversation.Id, request, "Synthetic named dispatcher", default);
        Assert.NotNull(pending.Message); Assert.Equal(2, pending.Message.TemplateVersion);
        var sent = await store.CompleteReplyAsync(pending, "simulated", "Synthetic named dispatcher", default); Assert.Equal("simulated", sent.Mode);
        var replay = await store.PrepareReplyAsync(conversation.Id, request, "Synthetic named dispatcher", default);
        Assert.Null(replay.Message); Assert.True(replay.Result.Replayed); Assert.Equal(sent.MessageId, replay.Result.MessageId);
        Assert.Equal(409, (await Assert.ThrowsAsync<ChannelRequestException>(() => store.PrepareReplyAsync(conversation.Id,
            request with { Reason = "Different synthetic action with the same operation" }, "Synthetic named dispatcher", default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ChannelRequestException>(() => store.PrepareReplyAsync(conversation.Id,
            request with { OperationId = "synthetic-fresh-operation" }, "Synthetic named dispatcher", default))).StatusCode);
        var timeline = (await store.GetAsync(conversation.Id, "Synthetic supervisor", default))!;
        Assert.Contains(timeline.Messages, item => item.Direction == "outbound" && item.Actor == "Synthetic named dispatcher" && item.TemplateVersion == 2 && item.Simulated);
        Assert.Equal(1, Assert.Single(await store.MetricsAsync(default), item => item.Channel == "sms").Outbound);
    }

    [Fact]
    public async Task ResolvedTemplateCannotFalselyTellAResidentTheirPendingCaseIsResolved()
    {
        using var app = App(); using var client = app.CreateClient(); var clock = new ChannelClock(); using var store = Store(app, clock);
        var linked = await Deliver(app, store, Message(at: clock.Now));
        var conversation = (await store.GetAsync(linked.ConversationId, "Synthetic dispatcher", default))!;
        var error = await Assert.ThrowsAsync<ChannelRequestException>(() => store.PrepareReplyAsync(conversation.Id,
            Reply(conversation, "case.resolved", linked.ReferenceNumber), "Synthetic named dispatcher", default));
        Assert.Equal(409, error.StatusCode); Assert.Contains("not recorded as resolved", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain((await store.GetAsync(conversation.Id, "Synthetic dispatcher", default))!.Messages, item => item.Direction == "outbound");
    }

    [Theory]
    [InlineData("SOS")][InlineData("CALLBACK")][InlineData("HUMAN")]
    public async Task SafetyAndCallbackCommandsQueueHumanReviewWithoutCreatingOrDispatchingACase(string command)
    {
        using var app = App(); using var client = app.CreateClient(); var clock = new ChannelClock(); using var store = Store(app, clock);
        var prepared = await Prepare(store, Message("sip", command, at: clock.Now));
        Assert.Null(prepared.Request); Assert.Null(prepared.Result!.ReferenceNumber);
        var conversation = (await store.GetAsync(prepared.ConversationId, "Synthetic call-taker", default))!;
        Assert.True(conversation.HumanQueue); Assert.Equal(command == "CALLBACK", conversation.CallbackRequested);
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        Assert.DoesNotContain(conversation.Messages, item => item.Direction == "outbound");
        if (command == "SOS")
        {
            Assert.Contains("10111", prepared.Result.Reply!); Assert.Contains("10177", prepared.Result.Reply!); Assert.Contains("112", prepared.Result.Reply!);
            Assert.Contains("does not alert responders", prepared.Result.Reply!);
        }
    }

    [Fact]
    public async Task SmsAndWhatsAppPanicPhrasesRecordOneIdempotentAlertWhileVoiceAndHelpDoNot()
    {
        using var app = App();
        var service = app.Services.GetRequiredService<ChannelService>();
        var sos = app.Services.GetRequiredService<SosStore>();
        async Task<ChannelIntakeResult> Receive(string provider, string text)
        {
            var message = Message(provider, text);
            var results = await service.ReceiveAsync([message], IncidentStore.Hash(JsonSerializer.Serialize(message, Json)), provider, Guid.NewGuid().ToString("N"), true, default);
            return Assert.Single(results);
        }
        var sms = Message("twilio-sms", "SOS");
        var first = Assert.Single(await service.ReceiveAsync([sms], IncidentStore.Hash(JsonSerializer.Serialize(sms, Json)), sms.Provider, Guid.NewGuid().ToString("N"), true, default));
        Assert.Equal("SOS", first.Command); Assert.Contains("SOS recorded for the municipal control room", first.Reply, StringComparison.Ordinal);
        Assert.Contains("does not alert responders", first.Reply, StringComparison.Ordinal); Assert.Contains("does not call 10111, 10177 or 112", first.Reply, StringComparison.Ordinal);
        Assert.Equal("sms", Assert.Single((await sos.QueueAsync(default)).Alerts).Channel);
        var replay = Assert.Single(await service.ReceiveAsync([sms], IncidentStore.Hash(JsonSerializer.Serialize(sms, Json)), sms.Provider, Guid.NewGuid().ToString("N"), true, default));
        Assert.True(replay.Replayed); Assert.Single((await sos.QueueAsync(default)).Alerts);
        Assert.Equal("SOS", (await Receive("whatsapp", "NGICELA USIZO")).Command);
        Assert.Equal("SOS", (await Receive("twilio-sms", "HULP MY")).Command);
        Assert.Equal("SOS", (await Receive("whatsapp", "SIZA")).Command);
        Assert.Equal("SOS", (await Receive("twilio-sms", "HELP ME")).Command);
        Assert.Equal("HELP", (await Receive("twilio-sms", "HULP")).Command);
        Assert.Equal("HELP", (await Receive("whatsapp", "USIZO")).Command);
        var voice = await Receive("sip", "SOS");
        Assert.Contains("does not alert responders", voice.Reply, StringComparison.Ordinal);
        Assert.Contains("No control-room SOS was created from this voice menu.", voice.Reply, StringComparison.Ordinal);
        Assert.Contains("10111", voice.Reply, StringComparison.Ordinal); Assert.Contains("10177", voice.Reply, StringComparison.Ordinal); Assert.Contains("112", voice.Reply, StringComparison.Ordinal);
        var queue = await sos.QueueAsync(default);
        Assert.Equal(5, queue.Alerts.Count);
        Assert.All(queue.Alerts, alert => Assert.True(alert.Channel is "sms" or "whatsapp"));
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }

    [Theory]
    [InlineData("twilio-voice")][InlineData("sip")][InlineData("elevenlabs")]
    public async Task HumanVoiceSendRequiresAnExplicitResidentCallbackRequestForEveryVoiceProvider(string provider)
    {
        using var app = App(); using var client = app.CreateClient(); var clock = new ChannelClock(); using var store = Store(app, clock);
        var linked = await Deliver(app, store, Message(provider, at: clock.Now));
        var conversation = (await store.GetAsync(linked.ConversationId, "Synthetic named call-taker", default))!;
        var blocked = await Assert.ThrowsAsync<ChannelRequestException>(() => store.PrepareReplyAsync(conversation.Id,
            Reply(conversation, reference: linked.ReferenceNumber), "Synthetic named call-taker", default));
        Assert.Equal(409, blocked.StatusCode); Assert.Contains("callback request", blocked.Message, StringComparison.Ordinal);
        var requested = await Prepare(store, Message(provider, "CALLBACK", at: clock.Now)); Assert.Null(requested.Request);
        conversation = (await store.GetAsync(linked.ConversationId, "Synthetic named call-taker", default))!;
        Assert.True(conversation.CallbackRequested); Assert.True(conversation.HumanQueue);
        var permitted = await store.PrepareReplyAsync(conversation.Id, Reply(conversation, reference: linked.ReferenceNumber), "Synthetic named call-taker", default);
        Assert.NotNull(permitted.Message);
        var simulated = await store.CompleteReplyAsync(permitted, "simulated", "Synthetic named call-taker", default);
        Assert.Equal("simulated", simulated.Mode); Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }

    [Fact]
    public async Task StatusCommandCannotLookUpAnotherResidentsUnlinkedCase()
    {
        using var app = App(); using var client = app.CreateClient(); var clock = new ChannelClock(); using var store = Store(app, clock);
        var linked = await Deliver(app, store, Message(at: clock.Now));
        var own = await Prepare(store, Message(text: "STATUS " + linked.ReferenceNumber, at: clock.Now));
        Assert.Equal(linked.ReferenceNumber, own.Result!.ReferenceNumber);
        Assert.DoesNotContain("municipal demo library", own.Result.Reply!, StringComparison.Ordinal);
        var unrelated = await Prepare(store, Message(text: "STATUS " + linked.ReferenceNumber, at: clock.Now) with { From = "+27000000222" });
        Assert.Null(unrelated.Result!.ReferenceNumber); Assert.DoesNotContain(linked.ReferenceNumber!, unrelated.Result.Reply!, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ExistingWebCasesJoinTheInboxWithoutInventingAVerifiedResidentOrAddressableContact(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/incidents", new IncidentRequest("Synthetic legacy web case outside the demo hall", "Synthetic fixture ward"));
        response.EnsureSuccessStatusCode(); var body = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident");
        var incident = await app.Services.GetRequiredService<IncidentStore>().GetAsync(body.GetProperty("referenceNumber").GetString()!, default);
        Assert.NotNull(incident); var clock = new ChannelClock(); using var store = Store(app, clock);
        await store.ImportLegacyAsync(incident, default); await store.ImportLegacyAsync(incident, default);
        var conversation = Assert.Single(await store.ListAsync("Synthetic dispatcher", default), item => item.CaseReferences.Contains(incident.ReferenceNumber));
        Assert.Equal("web", conversation.Channel); Assert.Equal("Unverified web report", conversation.ResidentLabel);
        Assert.Equal(1, Assert.Single(await store.MetricsAsync(default), item => item.Channel == "web").CasesCreated);
        var error = await Assert.ThrowsAsync<ChannelRequestException>(() => store.PrepareReplyAsync(conversation.Id,
            Reply(conversation, "channel.notice"), "Synthetic named dispatcher", default));
        Assert.Equal(409, error.StatusCode); Assert.Contains("No addressable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InboxReadAccessIsAuditedWithoutAddingResidentTextOrPhoneToAuditDetail()
    {
        using var app = App(); using var client = app.CreateClient(); var clock = new ChannelClock(); using var store = Store(app, clock);
        var receipt = await Prepare(store, Message(text: "Synthetic access-text sentinel", consent: false, at: clock.Now));
        await store.ListAsync("Synthetic named auditor", default); await store.GetAsync(receipt.ConversationId, "Synthetic named auditor", default);
        var state = JsonNode.Parse((await app.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(app.DataDirectory, "channels.json"), default))!)!;
        var access = state["audit"]!.AsArray().Where(item => item!["action"]!.GetValue<string>() == "channels.access").ToArray();
        Assert.Equal(2, access.Length);
        Assert.All(access, item => { var audit = item!.ToJsonString(); Assert.Contains("Synthetic named auditor", audit); Assert.DoesNotContain(Resident, audit); Assert.DoesNotContain("access-text sentinel", audit); });
    }

    [Theory]
    [InlineData("twilio-sms")][InlineData("clickatell")][InlineData("whatsapp")]
    [InlineData("twilio-voice")][InlineData("sip")][InlineData("elevenlabs")]
    public async Task SignedSyntheticWebhookCreatesAnAuditedCaseAndKeepsThePublicReferenceProjectionPrivate(string provider)
    {
        using var app = App(); using var client = await RbacTests.RoleClient(app, "supervisor");
        var message = Message(provider) with { Location = GeoScenario.Position(0.7, 0.5), LocationShared = true };
        using var response = await Simulate(client, message); response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<ChannelIntakeResult>(); Assert.NotNull(receipt);
        Assert.True(receipt.Simulated); Assert.NotNull(receipt.ReferenceNumber);
        var incident = await app.Services.GetRequiredService<IncidentStore>().GetAsync(receipt.ReferenceNumber, default);
        Assert.NotNull(incident); Assert.NotNull(incident.Location); Assert.True(incident.LocationShared);
        Assert.NotNull(incident.WardId); Assert.Equal("default", incident.TenantId);
        Assert.Equal(IncidentStatus.PendingApproval, incident.Status); Assert.Null(incident.Approval); Assert.Null(incident.AssignedCrewId);
        Assert.Contains(incident.Audit, item => item.Action.StartsWith("channels.intake.", StringComparison.Ordinal));
        if (message.Channel == "voice") Assert.Equal(message.Text, incident.TranscriptSummary);
        var privateTimeline = await client.GetStringAsync("/api/v1/channels/conversations/" + receipt.ConversationId);
        Assert.DoesNotContain(Resident, privateTimeline, StringComparison.Ordinal); Assert.DoesNotContain(Municipality, privateTimeline, StringComparison.Ordinal);
        var lookup = await client.GetStringAsync("/api/incidents/" + receipt.ReferenceNumber);
        Assert.DoesNotContain(message.Text, lookup, StringComparison.Ordinal); Assert.DoesNotContain("longitude", lookup, StringComparison.Ordinal);
        Assert.DoesNotContain("transcriptSummary", lookup, StringComparison.OrdinalIgnoreCase);
        Assert.True((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
    }

    [Fact]
    public async Task VolunteeredWhatsAppLocationCreatesANewHumanReviewCaseWithoutChangingAnApprovedResponseDestination()
    {
        using var app = App(); using var client = await RbacTests.RoleClient(app, "supervisor");
        using var report = await Simulate(client, Message("whatsapp")); report.EnsureSuccessStatusCode();
        var receipt = await report.Content.ReadFromJsonAsync<ChannelIntakeResult>(); Assert.NotNull(receipt); Assert.NotNull(receipt.ReferenceNumber);
        var coordinator = app.Services.GetRequiredService<IncidentCoordinator>();
        var approved = await coordinator.ApproveAsync(receipt.ReferenceNumber, new("Ignored request actor", "Synthetic original review"), "Synthetic first reviewer", default);
        Assert.Null(approved.Error); Assert.False(approved.Incident!.NeedsHumanReview); Assert.Null(approved.Incident.Location);
        var store = app.Services.GetRequiredService<IncidentStore>();
        var original = await store.GetAsync(receipt.ReferenceNumber, default); Assert.NotNull(original);
        var originalSnapshot = JsonSerializer.Serialize(original, Json);
        var point = GeoScenario.Position(0.7, 0.5);
        using var shared = await Simulate(client, Message("whatsapp",
            "Resident shared a report location. Human review requires the report description.", consent: false) with { Location = point, LocationShared = true });
        shared.EnsureSuccessStatusCode(); var linked = await shared.Content.ReadFromJsonAsync<ChannelIntakeResult>(); Assert.NotNull(linked);
        Assert.NotNull(linked.ReferenceNumber); Assert.NotEqual(receipt.ReferenceNumber, linked.ReferenceNumber);
        Assert.Equal(receipt.ConversationId, linked.ConversationId);
        var cases = await store.ListAsync(default); Assert.Equal(2, cases.Count);
        Assert.Equal(originalSnapshot, JsonSerializer.Serialize(await store.GetAsync(receipt.ReferenceNumber, default), Json));
        var incident = Assert.Single(cases, item => item.ReferenceNumber == linked.ReferenceNumber);
        Assert.Equal(point, incident.Location); Assert.True(incident.LocationShared); Assert.NotNull(incident.WardId);
        Assert.True(incident.NeedsHumanReview); Assert.Equal(IncidentStatus.PendingApproval, incident.Status);
        Assert.Null(incident.Approval); Assert.Null(incident.AssignedCrewId);
        Assert.Contains(incident.Audit, item => item.Action.StartsWith("channels.intake.", StringComparison.Ordinal));
        var timeline = await client.GetFromJsonAsync<ChannelConversationView>("/api/v1/channels/conversations/" + receipt.ConversationId);
        Assert.NotNull(timeline); Assert.Contains(receipt.ReferenceNumber, timeline.CaseReferences); Assert.Contains(linked.ReferenceNumber, timeline.CaseReferences);
        var reviewed = await coordinator.ApproveAsync(incident.Id, new("Ignored request actor", "Synthetic volunteered point reviewed"), "Synthetic second reviewer", default);
        Assert.Null(reviewed.Error); Assert.False(reviewed.Incident!.NeedsHumanReview);
        Assert.Equal("Synthetic second reviewer", reviewed.Incident.Approval!.ApprovedBy);
        Assert.Contains(reviewed.Incident.Audit, item => item.Action == "approved" && item.Actor == "Synthetic second reviewer");
        Assert.Equal(originalSnapshot, JsonSerializer.Serialize(await store.GetAsync(receipt.ReferenceNumber, default), Json));
        Assert.True((await store.VerifyAuditAsync(default)).Valid);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task RecordedCurrentIvrNoticeAllowsExplicitYesAndDoesNotClaimVerifiedProviderPlayback(string backend)
    {
        using var selection = StoreBackends.Use(backend); using var app = App(); using var client = app.CreateClient();
        var clock = new ChannelClock(); using var store = Store(app, clock);
        var first = await Prepare(store, Message("twilio-voice", consent: false, at: clock.Now));
        Assert.Null(first.Request); Assert.Null(first.Result!.ReferenceNumber);
        var premature = await Prepare(store, Message("twilio-voice", "YES", consent: false, at: clock.Now));
        Assert.Null(premature.Request); Assert.False((await store.GetAsync(first.ConversationId, "Synthetic call-taker", default))!.ConsentAccepted);
        await store.RecordIvrNoticeAsync(first.ConversationId, default); await store.RecordIvrNoticeAsync(first.ConversationId, default);
        var accepted = await Deliver(app, store, Message("twilio-voice", "YES", consent: false, at: clock.Now));
        Assert.NotNull(accepted.ReferenceNumber);
        var incident = await app.Services.GetRequiredService<IncidentStore>().GetAsync(accepted.ReferenceNumber, default);
        Assert.NotNull(incident); Assert.Equal(IncidentStatus.PendingApproval, incident.Status); Assert.True(incident.NeedsHumanReview);
        Assert.Null(incident.Approval); Assert.Null(incident.AssignedCrewId);
        var state = JsonNode.Parse((await app.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(app.DataDirectory, "channels.json"), default))!)!;
        var notice = Assert.Single(state["audit"]!.AsArray(), item => item!["action"]!.GetValue<string>() == "channels.notice.provided");
        Assert.Contains("provider playback unverified", notice!["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain((await store.GetAsync(first.ConversationId, "Synthetic call-taker", default))!.Messages, item => item.Direction == "outbound");
    }

    [Fact]
    public async Task AnIvrNoticeDoesNotGrantProcessingConsentToAnotherChannelForTheSameNumber()
    {
        using var app = App(); using var client = app.CreateClient(); var clock = new ChannelClock(); using var store = Store(app, clock);
        var voice = await Prepare(store, Message("twilio-voice", consent: false, at: clock.Now));
        await store.RecordIvrNoticeAsync(voice.ConversationId, default);
        var whatsapp = await Prepare(store, Message("whatsapp", "YES", consent: false, at: clock.Now));
        Assert.NotEqual(voice.ConversationId, whatsapp.ConversationId); Assert.Null(whatsapp.Request); Assert.Null(whatsapp.Result!.ReferenceNumber);
        Assert.False((await store.GetAsync(whatsapp.ConversationId, "Synthetic call-taker", default))!.ConsentAccepted);
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }

    [Fact]
    public async Task StopAllFromSmsBlocksTheExistingElevenLabsCallbackBeforeAnyProviderReceiptOrCallAudit()
    {
        using var app = App(); app.Settings["ELEVENLABS_AGENT_PHONE_NUMBER_ID"] = "synthetic-phone";
        using var client = await RbacTests.RoleClient(app, "supervisor");
        using var web = await client.PostAsJsonAsync("/api/incidents", new IncidentRequest("Synthetic legacy callback consent fixture near the municipal hall",
            "Synthetic fixture ward", ConsentToContact: true, CallbackConsent: true, ContactPhone: Resident,
            PrivacyConsentAccepted: true, PrivacyConsentVersion: PrivacyConsent.Version));
        web.EnsureSuccessStatusCode(); var reference = (await web.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident").GetProperty("referenceNumber").GetString()!;
        using var stopped = await Simulate(client, Message(text: "STOPALL")); stopped.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "synthetic-suppressed-callback");
        using var callback = await client.PostAsync("/api/incidents/" + reference + "/callback", null);
        Assert.Equal(HttpStatusCode.Conflict, callback.StatusCode); Assert.Contains("opted out", await callback.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var incident = await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default); Assert.NotNull(incident);
        Assert.DoesNotContain(incident.Audit, item => item.Action.StartsWith("elevenlabs-callback", StringComparison.Ordinal));
        Assert.Equal(0, Assert.Single(await app.Services.GetRequiredService<ChannelStore>().MetricsAsync(default), item => item.Channel == "sms").Outbound);
    }

    [Fact]
    public async Task SyntheticWebhookRejectsInvalidAndReplayedEnvelopesButFreshSignedProviderRetriesDoNotCreateCasesTwice()
    {
        using var app = App(); using var client = await RbacTests.RoleClient(app, "supervisor");
        var message = Message(); const string nonce = "synthetic-fixed-nonce-0001";
        using var invalid = await Simulate(client, message, nonce: nonce, invalidSignature: true);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        using var valid = await Simulate(client, message, nonce: nonce); valid.EnsureSuccessStatusCode();
        var original = await valid.Content.ReadFromJsonAsync<ChannelIntakeResult>(); Assert.NotNull(original);
        using var replay = await Simulate(client, message, nonce: nonce); Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using var retry = await Simulate(client, message); retry.EnsureSuccessStatusCode();
        var retried = await retry.Content.ReadFromJsonAsync<ChannelIntakeResult>(); Assert.NotNull(retried);
        Assert.True(retried.Replayed); Assert.Equal(original.ReferenceNumber, retried.ReferenceNumber);
        Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        using var conflict = await Simulate(client, message with { Text = "Synthetic changed content with same provider receipt" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task SyntheticSignedEnvelopeCannotBeReusedByAnotherAuthenticatedMunicipality()
    {
        using var app = App(); using var coast = await RbacTests.RoleClient(app, "supervisor", tenant: "synthetic-coast");
        var message = Message(text: "HELP");
        using var otherTenant = await Simulate(coast, message, tenant: "default");
        Assert.Equal(HttpStatusCode.Unauthorized, otherTenant.StatusCode);
        using var coastSigned = await Simulate(coast, message, tenant: "synthetic-coast"); coastSigned.EnsureSuccessStatusCode();
        var receipt = await coastSigned.Content.ReadFromJsonAsync<ChannelIntakeResult>(); Assert.NotNull(receipt);
        using var primary = await RbacTests.RoleClient(app, "supervisor");
        Assert.Equal(HttpStatusCode.NotFound, (await primary.GetAsync("/api/v1/channels/conversations/" + receipt.ConversationId)).StatusCode);
    }

    [Theory]
    [InlineData("resident")][InlineData("agent")][InlineData("crew")][InlineData("training-client")]
    public async Task ResidentAndNonOperationalRolesCannotReadThePrivateChannelInbox(string role)
    {
        using var app = App(); using var client = await RbacTests.RoleClient(app, role, crew: role == "crew" ? "water-01" : null);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/channels/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/channels/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/channels/metrics")).StatusCode);
    }

    [Fact]
    public async Task HumanReplyUsesTheServerAuthenticatedActorAndCookieWritesRequireCsrf()
    {
        using var app = App(); using var client = await RbacTests.RoleClient(app, "dispatcher");
        using var inbound = await Simulate(client, Message()); inbound.EnsureSuccessStatusCode();
        var receipt = await inbound.Content.ReadFromJsonAsync<ChannelIntakeResult>(); Assert.NotNull(receipt);
        var conversation = await client.GetFromJsonAsync<ChannelConversationView>("/api/v1/channels/conversations/" + receipt.ConversationId);
        Assert.NotNull(conversation); var reply = Reply(conversation, reference: receipt.ReferenceNumber);
        client.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        using var missingCsrf = await client.PostAsJsonAsync("/api/v1/channels/conversations/" + conversation.Id + "/reply", reply);
        Assert.Contains(missingCsrf.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden });
        var identity = await client.GetFromJsonAsync<JsonElement>("/auth/status");
        client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", identity.GetProperty("csrfToken").GetString());
        using var approved = await client.PostAsJsonAsync("/api/v1/channels/conversations/" + conversation.Id + "/reply", reply);
        approved.EnsureSuccessStatusCode(); var result = await approved.Content.ReadFromJsonAsync<ChannelReplyResult>();
        Assert.NotNull(result); Assert.Equal("simulated", result.Mode);
        var timeline = await client.GetFromJsonAsync<ChannelConversationView>("/api/v1/channels/conversations/" + conversation.Id);
        Assert.NotNull(timeline);
        Assert.Equal(identity.GetProperty("name").GetString(), Assert.Single(timeline.Messages, item => item.Direction == "outbound").Actor);
        using var auditor = await RbacTests.RoleClient(app, "auditor");
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync("/api/v1/channels/conversations/" + conversation.Id + "/reply", reply)).StatusCode);
    }

    [Fact]
    public async Task AdministratorTemplateChangesRequireTheCurrentVersionAndCannotDiscardTheProcessingNoticeVersion()
    {
        using var app = App(); using var admin = await RbacTests.RoleClient(app, "admin");
        const string endpoint = "/api/admin/channel-templates/case.received/en";
        var edit = new ChannelTemplateEdit("CivicOps recorded {reference}; this report awaits a municipal human reviewer.", 1, "Synthetic municipal approval records this revision");
        using var saved = await admin.PutAsJsonAsync(endpoint, edit); saved.EnsureSuccessStatusCode();
        var version = await saved.Content.ReadFromJsonAsync<ChannelTemplate>(); Assert.NotNull(version); Assert.Equal(2, version.Version);
        using var stale = await admin.PutAsJsonAsync(endpoint, edit); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var preview = await admin.PostAsJsonAsync("/api/admin/channel-templates/preview", new ChannelTemplatePreview("case.received", "en", Variables("CIV-20261009-00000011")));
        preview.EnsureSuccessStatusCode(); var rendered = await preview.Content.ReadFromJsonAsync<ChannelTemplatePreviewResult>();
        Assert.NotNull(rendered); Assert.Equal(2, rendered.Version); Assert.Contains("CIV-20261009-00000011", rendered.Text);
        using var removedNotice = await admin.PutAsJsonAsync("/api/admin/channel-templates/channel.notice/en",
            new ChannelTemplateEdit("Synthetic notice whose processing version has been omitted.", 1, "Synthetic municipal notice edit"));
        Assert.Equal(HttpStatusCode.BadRequest, removedNotice.StatusCode);
        using var dispatcher = await RbacTests.RoleClient(app, "dispatcher");
        Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PutAsJsonAsync(endpoint, edit with { ExpectedVersion = 2 })).StatusCode);
    }

    [Fact]
    public async Task ExplicitDemoReadinessAndNativeEndpointsRemainHonestAndCannotCallConfiguredProviders()
    {
        using var app = App(); app.Settings["CIVICOPS_CHANNEL_MODE"] = "configured"; app.Settings["CIVICOPS_CHANNEL_SEND_ENABLED"] = "true";
        using var supervisor = await RbacTests.RoleClient(app, "supervisor");
        var readiness = await supervisor.GetFromJsonAsync<ChannelReadiness>("/api/v1/channels/readiness"); Assert.NotNull(readiness);
        Assert.True(readiness.DemoMode); Assert.False(readiness.ExternalSendsEnabled); Assert.Equal(6, readiness.Channels.Count);
        Assert.All(readiness.Channels, item => Assert.Equal("simulated", item.Mode));
        using var webhook = app.CreateClient();
        foreach (var provider in ChannelOptions.Providers.Keys)
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await webhook.PostAsJsonAsync("/api/v1/channels/webhooks/" + provider, new { synthetic = true })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await webhook.GetAsync("/api/v1/channels/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=synthetic&hub.challenge=123")).StatusCode);
    }

    private static TestApp App() => new() { EnableBackgroundDelivery = false };
    private static IConfiguration Configuration(string directory, params (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["CIVICOPS_DATA_DIR"] = directory, ["CIVICOPS_DEMO_MODE"] = "true", ["CIVICOPS_SECRET_PROVIDER"] = "env" };
        foreach (var setting in settings) values[setting.Key] = setting.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    private static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static ChannelOptions NativeOptions(string url) => new(Configuration("", ("CIVICOPS_DEMO_MODE", "false"),
        ("CIVICOPS_CHANNEL_MODE", "configured"), ("CIVICOPS_CHANNEL_INGRESS_SECRET", "synthetic-relay-secret"),
        ("CIVICOPS_CHANNEL_ENCRYPTION_KEY", Key()), ("CIVICOPS_CHANNEL_WEBHOOK_BASE_URL", url)));
    private static ChannelStore Store(TestApp app, TimeProvider clock)
    {
        var configuration = Configuration(app.DataDirectory);
        return new(configuration, new SyntheticEnvironment(), app.Services.GetRequiredService<DurableStorage>(),
            app.Services.GetRequiredService<IncidentStore>(), clock, new CaseEventStream(), new ChannelOptions(configuration));
    }
    private static InboundMessage Message(string provider = "twilio-sms", string text = "Synthetic water leak outside the municipal demo library", bool consent = true, DateTimeOffset? at = null) =>
        new(ChannelOptions.Providers[provider], provider, Resident, Municipality, text, "en", at ?? DateTimeOffset.UtcNow,
            "synthetic-message-" + Guid.NewGuid().ToString("N"), Area: "Synthetic municipal fixture ward",
            PrivacyConsentAccepted: consent ? true : null, PrivacyConsentVersion: consent ? PrivacyConsent.Version : null);
    private static Task<PreparedChannelIntake> Prepare(ChannelStore store, InboundMessage message) =>
        store.PrepareAsync(message, IncidentStore.Hash(JsonSerializer.Serialize(message, Json)), "synthetic-nonce-" + Guid.NewGuid().ToString("N"), true, default);
    private static async Task<ChannelIntakeResult> Deliver(TestApp app, ChannelStore store, InboundMessage message)
    {
        var prepared = await Prepare(store, message); if (prepared.Result is not null) return prepared.Result;
        Assert.NotNull(prepared.Request);
        var created = await app.Services.GetRequiredService<IncidentCoordinator>().CreateAsync(prepared.Request, "synthetic-channel-" + prepared.MessageKey, default);
        Assert.Null(created.Error); return await store.CompleteAsync(prepared, created.Incident, true, default);
    }
    private static Dictionary<string, string> Variables(string reference) => new()
    { ["reference"] = reference, ["update"] = "Synthetic dispatcher review is pending; no arrival time is promised.", ["request"] = "the service issue from a safe distance" };
    private static ChannelReplyRequest Reply(ChannelConversationView conversation, string key = "case.received", string? reference = null) =>
        new(key, "en", key == "channel.notice" ? [] : Variables(reference ?? (conversation.CaseReferences.Count > 0 ? conversation.CaseReferences[0] : "CIV-20261009-00000011")),
            1, conversation.Version, "Synthetic named municipal reviewer approves this reply", "synthetic-operation-" + Guid.NewGuid().ToString("N"));
    private static async Task<HttpResponseMessage> Simulate(HttpClient client, InboundMessage message, string tenant = "default", string? nonce = null, bool invalidSignature = false)
    {
        var body = JsonSerializer.Serialize(message, Json); var now = DateTimeOffset.UtcNow; nonce ??= Guid.NewGuid().ToString("N");
        var envelope = new ChannelWebhookRequest(message.Provider, body, "application/json", tenant, new Dictionary<string, string>());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/simulated/" + message.Provider)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-CivicOps-Timestamp", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-CivicOps-Nonce", nonce);
        request.Headers.Add("X-CivicOps-Signature", invalidSignature ? "sha256=" + new string('0', 64)
            : ChannelSignature.SignIngress(envelope, ChannelOptions.SyntheticSigningKey, now, nonce));
        return await client.SendAsync(request);
    }
    private sealed class ChannelClock : TimeProvider
    { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
