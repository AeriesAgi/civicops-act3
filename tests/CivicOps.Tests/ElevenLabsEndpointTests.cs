using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class ElevenLabsEndpointTests
{
    private const string Route = "/api/channels/elevenlabs/tools/";
    private static object Report(string turn = "turn-1", string conversation = "synthetic-conversation") => new
    {
        conversation_id = conversation, turn_id = turn, description = "Synthetic pothole reported beside the park",
        area = "Synthetic ward", language = "af", channel = "chat", contact_preference = "none",
        consent_to_contact = false, callback_consent = false,
        privacy_consent_accepted = true, privacy_consent_version = PrivacyConsent.Version
    };
    private static async Task<string> Create(HttpClient client, string turn = "turn-1", string conversation = "synthetic-conversation")
    {
        var response = await client.PostAsJsonAsync(Route + "report_incident", Report(turn, conversation));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("referenceNumber").GetString()!;
    }
    [Fact]
    public async Task SignedInResidentUsesTenantBoundSimulatedIntakeWithoutProviderToolAccess()
    {
        using var app = new TestApp(); using var resident = await RbacTests.RoleClient(app, "resident", tenant: "synthetic-coast");
        Assert.Equal(HttpStatusCode.Forbidden, (await resident.PostAsJsonAsync(Route + "report_incident", Report())).StatusCode);
        var response = await resident.PostAsJsonAsync("/api/channels/elevenlabs/simulated-chat", Report());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reference = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("referenceNumber").GetString()!;
        var replay = await resident.PostAsJsonAsync("/api/channels/elevenlabs/simulated-chat", Report());
        Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await resident.GetAsync("/api/incidents/" + reference)).StatusCode);
        using var otherMunicipality = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await otherMunicipality.GetAsync("/api/incidents/" + reference)).StatusCode);
        using (TenantContext.Use("synthetic-coast"))
            Assert.Equal("synthetic-coast", (await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default))!.TenantId);
        resident.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await resident.PostAsJsonAsync("/api/channels/elevenlabs/simulated-chat", Report("second"))).StatusCode);
    }
    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.NotFound)]
    public async Task ResidentChatFacadeRequiresSimulationAndNeverCallsAProvider(bool simulated, HttpStatusCode expected)
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        // These synthetic configuration values only select live mode; the disabled facade must return before any client call.
        app.Settings["ELEVENLABS_API_KEY"] = simulated ? "" : "synthetic-unusable-key";
        using var client = app.CreateClient();
        Assert.Equal(expected, (await client.PostAsJsonAsync("/api/channels/elevenlabs/simulated-chat", Report())).StatusCode);
    }
    [Fact]
    public async Task SimulatedToolCreatesSamePipelineAndIsIdempotent()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var reference = await Create(client);
        var replay = await client.PostAsJsonAsync(Route + "report_incident", Report());
        var body = await replay.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("replayed").GetBoolean()); Assert.Equal(reference, body.GetProperty("referenceNumber").GetString());
        var incident = Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        Assert.Equal("synthetic-conversation", incident.ConversationId); Assert.Equal("af", incident.ResidentLanguage);
        Assert.Equal("chat", incident.Source); Assert.Equal("PendingApproval", incident.Status.ToString()); Assert.Null(incident.AssignedCrewId);
        Assert.Equal(5, incident.AgentRuns.Count);
    }
    [Fact]
    public async Task ReusingConversationTurnForDifferentReportConflicts()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        await Create(client);
        var body = JsonSerializer.SerializeToNode(Report())!.AsObject(); body["description"] = "Synthetic electricity streetlight fault";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(Route + "report_incident", body)).StatusCode);
        Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }
    [Fact]
    public async Task ConversationAndTurnDelimitersCannotCollideInIdempotencyKeys()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var first = await Create(client, "c", "a:b");
        var second = await Create(client, "b:c", "a");
        Assert.NotEqual(first, second); Assert.Equal(2, (await app.Services.GetRequiredService<IncidentStore>().ListAsync(default)).Count);
    }
    [Theory]
    [InlineData("", HttpStatusCode.Unauthorized)] [InlineData("test-wrong-secret", HttpStatusCode.Unauthorized)]
    [InlineData("test-tool-secret", HttpStatusCode.OK)]
    public async Task ConfiguredToolSecretIsRequiredEvenInDemo(string supplied, HttpStatusCode expected)
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_ELEVENLABS_TOOL_SECRET"] = "test-tool-secret";
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Add("X-CivicOps-Tool-Secret", supplied);
        Assert.Equal(expected, (await client.PostAsJsonAsync(Route + "report_incident", Report())).StatusCode);
    }
    [Fact]
    public async Task UnconfiguredToolSecretOutsideDemoReturns503()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync(Route + "report_incident", Report())).StatusCode);
    }
    [Theory]
    [InlineData("[]")] [InlineData("null")] [InlineData("\"text\"")]
    [InlineData("{\"description\":42}")] [InlineData("{\"conversation_id\":false}")]
    [InlineData("{\"consent_to_contact\":\"true\"}")]
    public async Task MalformedToolPayloadFailsCleanly(string payload)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(Route + "report_incident", new StringContent(payload, Encoding.UTF8, "application/json"))).StatusCode);
    }
    [Theory]
    [InlineData("description", 4001)] [InlineData("area", 201)] [InlineData("language", 13)]
    [InlineData("contact_email", 255)] [InlineData("contact_phone", 41)] [InlineData("conversation_id", 129)] [InlineData("turn_id", 129)]
    public async Task EveryToolTextFieldIsBounded(string name, int length)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var body = JsonSerializer.SerializeToNode(Report())!.AsObject(); body[name] = new string('x', length);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Route + "report_incident", body)).StatusCode);
    }
    [Fact]
    public async Task MaximumConversationAndTurnLengthsHaveBoundedHashedIdempotencyKey()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        await Create(client, new string('t', 128), new string('c', 128));
    }
    [Fact]
    public async Task AddInformationHasDurableTurnReplayAndRejectsCrossConversationMutation()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var reference = await Create(client);
        var extra = new { conversation_id = "synthetic-conversation", turn_id = "turn-extra", reference_number = reference, information = "Synthetic additional landmark." };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Route + "add_case_information", extra)).StatusCode);
        var after = Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        var replay = await client.PostAsJsonAsync(Route + "add_case_information", extra);
        Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
        Assert.Equal(after.Version, (await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default))!.Version);
        var spoof = new { conversation_id = "other-conversation", turn_id = "turn-extra", reference_number = reference, information = "Spoofed additional information." };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Route + "add_case_information", spoof)).StatusCode);
    }
    [Fact]
    public async Task StatusLookupReturnsMinimalSpeakableFields()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var reference = await Create(client);
        var response = await client.PostAsJsonAsync(Route + "lookup_case_status", new { conversation_id = "synthetic-other", turn_id = "lookup", reference_number = reference });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("contactEmail", text); Assert.DoesNotContain("rawTranscript", text); Assert.DoesNotContain("synthetic-conversation", text);
        Assert.Contains("nextStep", text);
    }
    [Fact]
    public async Task StatusToolNeverReturnsArbitraryStoredPublicUpdate()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var reference = await Create(client);
        await app.Services.GetRequiredService<IncidentStore>().ChangeAsync(reference, null, incident =>
        { incident.PublicUpdate = "Private draft containing resident@example.invalid contact."; return null; }, default);
        var response = await client.PostAsJsonAsync(Route + "lookup_case_status", new { conversation_id = "synthetic-other", turn_id = "lookup", reference_number = reference });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("resident@example.invalid", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Private draft", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task HumanHandoffRecordsReviewWithoutDispatch()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var reference = await Create(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Route + "request_human_handoff", new { conversation_id = "synthetic-conversation", turn_id = "handoff", reference_number = reference, reason = "Please ask a person to review." })).StatusCode);
        var incident = (await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default))!;
        Assert.True(incident.NeedsHumanReview); Assert.Null(incident.Approval); Assert.Null(incident.AssignedCrewId);
    }
    [Fact]
    public async Task SignedPostCallAttachesPrivateTranscriptAndReplayIsDurable()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var reference = await Create(client);
        var payload = Webhook("synthetic-conversation");
        Assert.Equal(HttpStatusCode.OK, (await SendWebhook(client, payload)).StatusCode);
        var store = app.Services.GetRequiredService<IncidentStore>();
        var incident = (await store.GetAsync(reference, default))!;
        Assert.Contains("Synthetic resident transcript", incident.RawTranscript); Assert.Equal("Synthetic post-call summary", incident.TranscriptSummary);
        var replay = await SendWebhook(client, payload);
        Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
        Assert.Equal(incident.Version, (await store.GetAsync(reference, default))!.Version);
        var publicText = await client.GetStringAsync("/api/incidents/" + reference);
        Assert.DoesNotContain("Synthetic resident transcript", publicText); Assert.DoesNotContain("Synthetic post-call summary", publicText);
        Assert.True((await store.VerifyAuditAsync(default)).Valid);
    }
    [Fact]
    public async Task ToolAndPostCallReplaySurviveHostRestart()
    {
        var first = new TestApp { KeepData = true };
        var directory = first.DataDirectory;
        string reference;
        var payload = Webhook("synthetic-conversation");
        using (var client = first.CreateClient())
        {
            reference = await Create(client);
            Assert.Equal(HttpStatusCode.OK, (await SendWebhook(client, payload)).StatusCode);
        }
        first.Dispose();
        using var second = new TestApp { DataDirectory = directory };
        using var restarted = second.CreateClient();
        Assert.Equal(reference, await Create(restarted));
        var response = await SendWebhook(restarted, payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
        Assert.Single(await second.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        Assert.True((await second.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
    }
    [Theory]
    [InlineData("test-wrong-secret", 0)] [InlineData("test-webhook-secret", -1801)]
    public async Task InvalidOrStaleWebhookDoesNotAttach(string secret, int seconds)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        await Create(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendWebhook(client, Webhook("synthetic-conversation"), secret, seconds)).StatusCode);
        Assert.Null(Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default)).RawTranscript);
    }
    [Fact]
    public async Task PostCallCreatesMissingReportFromCollectedFieldsForReview()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var response = await SendWebhook(client, Webhook("synthetic-orphan", includeDescription: true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var incident = Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        Assert.Equal("PendingApproval", incident.Status.ToString()); Assert.True(incident.NeedsHumanReview);
        Assert.Equal("synthetic-orphan", incident.ConversationId); Assert.Null(incident.AssignedCrewId);
    }
    [Fact]
    public async Task AudioWebhookIsIgnoredWithoutStorage()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var response = await SendWebhook(client, JsonSerializer.Serialize(new { type = "post_call_audio", data = new { conversation_id = "synthetic-audio", full_audio = "synthetic-base64" } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ignored").GetBoolean());
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        Assert.Empty(Directory.GetFiles(app.DataDirectory, "*audio*", SearchOption.AllDirectories));
    }
    [Fact]
    public async Task CallInitiationFailureCreatesDispatcherAlert()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var payload = JsonSerializer.Serialize(new { type = "call_initiation_failure", data = new { conversation_id = "synthetic-failed-call", failure_reason = "busy" } });
        Assert.Equal(HttpStatusCode.OK, (await SendWebhook(client, payload)).StatusCode);
        var alerts = await client.GetStringAsync("/api/channels/elevenlabs/alerts");
        Assert.Contains("busy", alerts); Assert.Contains("Human follow-up", alerts);
        Assert.Contains(await app.Services.GetRequiredService<IncidentStore>().AuditAsync(default), x => x.Action == "elevenlabs-unlinked-call-failed");
        Assert.True((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
    }
    [Fact]
    public async Task AudioReplayDoesNotDuplicateAuditAndStoresNoAudio()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var payload = JsonSerializer.Serialize(new { type = "post_call_audio", data = new { conversation_id = "synthetic-audio-repeat", full_audio = "synthetic-base64" } });
        await SendWebhook(client, payload); var store = app.Services.GetRequiredService<IncidentStore>();
        var before = await store.AuditAsync(default);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhook(client, payload)).StatusCode);
        Assert.Equal(before.Count, (await store.AuditAsync(default)).Count);
        Assert.DoesNotContain("synthetic-base64", await File.ReadAllTextAsync(Path.Combine(app.DataDirectory, "elevenlabs-webhooks.json")));
    }
    [Fact]
    public async Task PostCallNewResidentHazardRaisesUrgencyAndAlert()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var reference = await Create(client);
        var payload = JsonSerializer.Serialize(new { type = "post_call_transcription", data = new { conversation_id = "synthetic-conversation", transcript = new[] { new { role = "user", message = "There is a live wire beside the pothole." } } } });
        Assert.Equal(HttpStatusCode.OK, (await SendWebhook(client, payload)).StatusCode);
        Assert.Equal("Urgent", (await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default))!.Priority);
        Assert.Contains("danger language", await client.GetStringAsync("/api/channels/elevenlabs/alerts"));
    }
    [Fact]
    public async Task AgentsGenericEmergencyDisclaimerDoesNotRaiseUrgency()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var reference = await Create(client);
        var payload = JsonSerializer.Serialize(new { type = "post_call_transcription", data = new { conversation_id = "synthetic-conversation", transcript = new[] { new { role = "agent", message = "If there is fire or danger, call emergency services." }, new { role = "user", message = "It is only a small pothole in the synthetic road." } } } });
        Assert.Equal(HttpStatusCode.OK, (await SendWebhook(client, payload)).StatusCode);
        Assert.Equal("Medium", (await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default))!.Priority);
    }
    [Fact]
    public async Task RoutinePostCallPreservesApprovedCaseGate()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var reference = await Create(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/incidents/" + reference + "/approve", new { approvedBy = "caller-supplied" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhook(client, Webhook("synthetic-conversation"))).StatusCode);
        var incident = (await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default))!;
        Assert.False(incident.NeedsHumanReview); Assert.NotNull(incident.Approval);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/incidents/" + reference + "/assign", new { crewId = "roads-01", approvedBy = "caller-supplied" })).StatusCode);
    }
    [Fact]
    public async Task NewAssignedCaseHazardRequiresHumanReviewBeforeResponseAndCanBeReapproved()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var reference = await Create(client);
        await client.PostAsJsonAsync("/api/incidents/" + reference + "/approve", new { approvedBy = "caller-supplied" });
        await client.PostAsJsonAsync("/api/incidents/" + reference + "/assign", new { crewId = "roads-01", approvedBy = "caller-supplied" });
        var information = new { conversation_id = "synthetic-conversation", turn_id = "new-hazard", reference_number = reference, information = "There is now a live wire near the pothole." };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Route + "add_case_information", information)).StatusCode);
        var store = app.Services.GetRequiredService<IncidentStore>();
        var flagged = (await store.GetAsync(reference, default))!;
        Assert.True(flagged.NeedsHumanReview); Assert.Equal("Urgent", flagged.Priority); Assert.Equal("Assigned", flagged.Status.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync("/api/incidents/" + reference + "/status", new { status = "InProgress", approvedBy = "caller-supplied" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/incidents/" + reference + "/approve", new { approvedBy = "caller-supplied" })).StatusCode);
        var reviewed = (await store.GetAsync(reference, default))!;
        Assert.False(reviewed.NeedsHumanReview); Assert.Equal("Assigned", reviewed.Status.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync("/api/incidents/" + reference + "/status", new { status = "InProgress", approvedBy = "caller-supplied" })).StatusCode);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CollectedBooleanHandoffIsRetainedAndReopensHumanReview(bool wrapped)
    {
        // This test owns case versions while checking handoff authorization; delivery scheduling is covered by outbox tests.
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var reference = await Create(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/incidents/" + reference + "/approve", new { approvedBy = "caller-supplied" })).StatusCode);
        var collection = new Dictionary<string, object> { ["human_handoff_requested"] = wrapped ? new { value = true } : true };
        var payload = JsonSerializer.Serialize(new { type = "post_call_transcription", data = new
        { conversation_id = "synthetic-conversation", analysis = new { data_collection_results = collection } } });
        Assert.Equal(HttpStatusCode.OK, (await SendWebhook(client, payload)).StatusCode);
        var incident = (await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default))!;
        Assert.True(incident.NeedsHumanReview); Assert.NotNull(incident.Approval); Assert.Null(incident.AssignedCrewId);
        using var metadata = JsonDocument.Parse(incident.CallMetadata!);
        Assert.True(metadata.RootElement.GetProperty("data_collection").GetProperty("human_handoff_requested").GetBoolean());
        Assert.Contains("Resident requested a human handoff", await client.GetStringAsync("/api/channels/elevenlabs/alerts"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/incidents/" + reference + "/assign", new { crewId = "roads-01" })).StatusCode);
    }
    [Fact]
    public async Task ToolConversationHeaderMustMatchBodyWhenProvided()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-CivicOps-Conversation-Id", "different-conversation");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Route + "report_incident", Report())).StatusCode);
    }
    [Fact]
    public async Task ConversationsSharingProviderIpHaveSeparateToolQuotas()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_INTAKE_LIMIT"] = "1";
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Add("X-CivicOps-Conversation-Id", "first-conversation");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Route + "report_incident", Report("first", "first-conversation"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(Route + "report_incident", Report("second", "first-conversation"))).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CivicOps-Conversation-Id"); client.DefaultRequestHeaders.Add("X-CivicOps-Conversation-Id", "second-conversation");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Route + "report_incident", Report("first", "second-conversation"))).StatusCode);
    }
    [Fact]
    public async Task InvalidToolAuthenticationCannotChargeAuthenticatedConversationQuota()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_INTAKE_LIMIT"] = "1"; app.Settings["CIVICOPS_ELEVENLABS_TOOL_SECRET"] = "test-tool-secret";
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Add("X-CivicOps-Conversation-Id", "synthetic-conversation");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Route + "report_incident", Report())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(Route + "report_incident", Report())).StatusCode);
        client.DefaultRequestHeaders.Add("X-CivicOps-Tool-Secret", "test-tool-secret");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(Route + "report_incident", Report())).StatusCode);
    }
    [Fact]
    public async Task BigBodyIsOnlyAllowedForPostCall()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var huge = new string('x', 40000);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsJsonAsync(Route + "report_incident", new { description = huge })).StatusCode);
        var payload = JsonSerializer.Serialize(new { type = "post_call_audio", data = new { conversation_id = "synthetic-audio", full_audio = huge } });
        Assert.Equal(HttpStatusCode.OK, (await SendWebhook(client, payload)).StatusCode);
    }
    [Fact]
    public async Task UnconfiguredCallbacksAreDisabled()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var reference = await Create(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/incidents/" + reference + "/callback", null)).StatusCode);
    }
    [Fact]
    public async Task CallbackRequiresRecordedContactAndBothConsentFlags()
    {
        using var app = new TestApp(); app.Settings["ELEVENLABS_AGENT_PHONE_NUMBER_ID"] = "fake-phone-id";
        using var client = app.CreateClient(); var reference = await Create(client);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "fake-callback");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/incidents/" + reference + "/callback", null)).StatusCode);
    }
    [Fact]
    public async Task ConsentedCallbackSimulatesAndSuppressesRetries()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; app.Settings["ELEVENLABS_AGENT_PHONE_NUMBER_ID"] = "fake-phone-id";
        using var client = app.CreateClient(); var reference = await Create(client);
        var store = app.Services.GetRequiredService<IncidentStore>();
        await store.ChangeAsync(reference, null, incident => { incident.CallbackConsent = true; incident.ConsentToContact = true; incident.ContactPhone = "+27 00 000 0000"; return null; }, default);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "fake-callback");
        var response = await client.PostAsync("/api/incidents/" + reference + "/callback", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no call placed", await response.Content.ReadAsStringAsync());
        var after = (await store.GetAsync(reference, default))!;
        var replay = await client.PostAsync("/api/incidents/" + reference + "/callback", null);
        Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
        Assert.Equal(after.Version, (await store.GetAsync(reference, default))!.Version);
    }
    [Fact]
    public async Task SessionAndChannelStatusExposeModeWithoutKeys()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var response = await client.GetAsync("/api/channels/elevenlabs/session");
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("simulated", await response.Content.ReadAsStringAsync());
        var status = await client.GetStringAsync("/api/channels/status"); Assert.Contains("simulated", status); Assert.DoesNotContain("test-webhook-secret", status);
    }
    private static string Webhook(string conversation, bool includeDescription = false) => JsonSerializer.Serialize(new
    {
        type = "post_call_transcription", data = new
        {
            agent_id = "synthetic-agent", conversation_id = conversation,
            transcript = new[] { new { role = "user", message = "Synthetic resident transcript about pothole" } },
            metadata = new { call_duration_secs = 12, phone_call = new { caller_number = "+27 00 000 0000" } },
            analysis = new { transcript_summary = "Synthetic post-call summary", data_collection_results = includeDescription
                ? new Dictionary<string, object> { ["description"] = new { value = "Synthetic pothole collected after conversation" }, ["area"] = new { value = "Synthetic ward" } }
                : new Dictionary<string, object>() }
        }
    });
    private static Task<HttpResponseMessage> SendWebhook(HttpClient client, string payload, string secret = "test-webhook-secret", int offset = 0)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/channels/elevenlabs/post-call") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        request.Headers.Add("ElevenLabs-Signature", ElevenLabsSignatureTests.Sign(payload, secret, DateTimeOffset.UtcNow.AddSeconds(offset)));
        return client.SendAsync(request);
    }
}
