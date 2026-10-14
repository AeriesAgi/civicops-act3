using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;

namespace CivicOps.Tests;

public sealed class ProviderReadinessTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static IConfiguration Config(params (string Key, string? Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => x.Value)).Build();

    [Theory]
    [InlineData(false, "default")]
    [InlineData(true, "amd")]
    public async Task DryRunJsonMatchesReviewedGoldenFileWithoutNetwork(bool amd, string name)
    {
        var handler = new ElevenLabsFakeHandler { Respond = _ => throw new InvalidOperationException("Dry run cannot call HTTP") };
        var output = new StringWriter();
        var args = amd ? new[] { "elevenlabs", "provision", "--json", "--llm", "amd" } : new[] { "elevenlabs", "provision", "--json" };
        Assert.Equal(0, await ElevenLabsProvisioner.RunAsync(args, Config(), Root, output, new(handler)));
        var golden = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Root, "tests/CivicOps.Tests/fixtures/elevenlabs", name + ".json")));
        Assert.True(JsonNode.DeepEquals(golden, JsonNode.Parse(output.ToString())));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://example.invalid")]
    [InlineData("example.invalid/widget")]
    [InlineData("example.invalid:8443")]
    public void AllowlistRejectsUrlsPortsAndPaths(string hosts) => Assert.Throws<InvalidOperationException>(() =>
        ElevenLabsProvisioner.BuildPlan(Config(("ELEVENLABS_WIDGET_ALLOWED_HOSTS", hosts)), Root));

    [Fact]
    public async Task CheckAndApplyCannotBeCombined()
    {
        var handler = new ElevenLabsFakeHandler();
        Assert.Equal(2, await ElevenLabsProvisioner.RunAsync(["elevenlabs", "provision", "--check", "--apply"], Config(), Root, new StringWriter(), new(handler)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckWithoutCredentialsMakesNoRequest()
    {
        var handler = new ElevenLabsFakeHandler();
        Assert.Equal(2, await ElevenLabsProvisioner.RunAsync(["elevenlabs", "provision", "--check"], Config(), Root, new StringWriter(), new(handler)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void AgentPlanAttachesWebhookWithDocumentedTranscriptEvents()
    {
        var plan = ElevenLabsProvisioner.BuildPlan(Config(("ELEVENLABS_POST_CALL_WEBHOOK_ID", "synthetic-webhook-id")), Root);
        foreach (var operation in plan.Where(x => x.Resource == "agent"))
        {
            var webhook = operation.Body["platform_settings"]!["workspace_overrides"]!["webhooks"]!;
            Assert.Equal("synthetic-webhook-id", webhook["post_call_webhook_id"]!.GetValue<string>());
            Assert.Equal(new[] { "transcript", "call_initiation_failure" }, webhook["events"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
            Assert.Equal("json", webhook["transcript_format"]!.GetValue<string>());
            Assert.False(webhook["exclude_transcript"]!.GetValue<bool>());
        }
    }

    [Fact]
    public void LanguagePresetsAndDataCollectionMatchProviderContract()
    {
        foreach (var operation in ElevenLabsProvisioner.BuildPlan(Config(), Root).Where(x => x.Resource == "agent"))
        {
            var preset = operation.Body["conversation_config"]!["language_presets"]!["af"]!;
            Assert.Equal("af", preset["overrides"]!["agent"]!["language"]!.GetValue<string>());
            Assert.NotNull(preset["overrides"]!["agent"]!["first_message"]);
            foreach (var item in operation.Body["platform_settings"]!["data_collection"]!.AsObject())
            {
                Assert.Contains(item.Value!["type"]!.GetValue<string>(), new[] { "boolean", "string", "integer", "number" });
                Assert.NotNull(item.Value["description"]);
            }
        }
        var callback = ElevenLabsProvisioner.BuildPlan(Config(), Root).Single(x => x.Name == "Culltron CivicOps consented callback");
        Assert.Contains("{{case_reference}}", callback.Body["conversation_config"]!["language_presets"]!["af"]!["overrides"]!["agent"]!["first_message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckReportsFieldDriftUsingOnlyGetsAndDoesNotWriteState(bool drift)
    {
        var directory = Path.Combine(Path.GetTempPath(), "elevenlabs-check-tests-" + Guid.NewGuid().ToString("N"));
        var config = Config(("ELEVENLABS_API_KEY", "test-elevenlabs-key"), ("PUBLIC_BASE_URL", "https://civicops.example.invalid"),
            ("ELEVENLABS_WORKSPACE_TOOL_SECRET_ID", "test-workspace-reference"), ("CIVICOPS_DATA_DIR", directory));
        var plan = ElevenLabsProvisioner.BuildPlan(config, Root);
        var state = new JsonObject();
        var payloads = new Dictionary<string, JsonObject>();
        foreach (var operation in plan)
        {
            var id = "synthetic-" + payloads.Count;
            state[operation.Resource + ":" + operation.Name] = id;
            payloads[id] = operation.Body.DeepClone().AsObject();
        }
        foreach (var operation in plan.Where(x => x.Resource == "agent"))
        {
            var prompt = payloads[state["agent:" + operation.Name]!.GetValue<string>()]["conversation_config"]!["agent"]!["prompt"]!;
            prompt["tool_ids"] = new JsonArray(plan.Where(x => x.Resource == "tool").Select(x => (JsonNode)JsonValue.Create(state["tool:" + x.Name]!.GetValue<string>())!).ToArray());
            prompt["knowledge_base"]![0]!["id"] = state["knowledge:" + plan.Single(x => x.Resource == "knowledge").Name]!.GetValue<string>();
            // Provider reads may reorder these sets; that is not configuration drift.
            prompt["tool_ids"] = new JsonArray(prompt["tool_ids"]!.AsArray().Reverse().Select(x => x!.DeepClone()).ToArray());
        }
        var knowledge = plan.Single(x => x.Resource == "knowledge");
        payloads[state["knowledge:" + knowledge.Name]!.GetValue<string>()]["type"] = "text";
        if (drift) payloads[state["agent:Culltron CivicOps resident line"]!.GetValue<string>()]["conversation_config"]!["agent"]!["prompt"]!["temperature"] = 1;
        var handler = new ElevenLabsFakeHandler { Respond = request =>
        {
            var segments = request.RequestUri!.AbsolutePath.Split('/');
            if (segments[^1] == "content") return new(HttpStatusCode.OK) { Content = new StringContent(knowledge.Body["text"]!.GetValue<string>()) };
            return ElevenLabsFakeHandler.Json(payloads[segments[^1]].ToJsonString());
        } };
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "elevenlabs-provision-state.json");
        var before = state.ToJsonString();
        await File.WriteAllTextAsync(path, before);
        var output = new StringWriter();
        try
        {
            Assert.Equal(drift ? 1 : 0, await ElevenLabsProvisioner.RunAsync(["elevenlabs", "provision", "--check"], config, Root, output, new(handler)));
            Assert.Equal(9, handler.Requests.Count);
            Assert.All(handler.Requests, request => { Assert.Equal("GET", request.Method); Assert.Null(request.Body); });
            Assert.Equal(before, await File.ReadAllTextAsync(path));
            Assert.Single(Directory.GetFiles(directory));
            Assert.DoesNotContain("test-workspace-reference", output.ToString());
            Assert.DoesNotContain("test-elevenlabs-key", output.ToString());
            Assert.Contains(drift ? "$.conversation_config.agent.prompt.temperature" : "No drift", output.ToString());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CheckDiscoversMissingResourcesWithoutCreatingLocalDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "elevenlabs-missing-check-" + Guid.NewGuid().ToString("N"));
        var handler = new ElevenLabsFakeHandler();
        var output = new StringWriter();
        Assert.Equal(1, await ElevenLabsProvisioner.RunAsync(["elevenlabs", "provision", "--check"], Config(("ELEVENLABS_API_KEY", "test-elevenlabs-key"),
            ("PUBLIC_BASE_URL", "https://civicops.example.invalid"), ("ELEVENLABS_WORKSPACE_TOOL_SECRET_ID", "test-workspace-reference"),
            ("CIVICOPS_DATA_DIR", directory)), Root, output, new(handler)));
        Assert.Equal(8, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("GET", request.Method));
        Assert.Contains("MISSING", output.ToString());
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("public")]
    [InlineData("signed-url")]
    [InlineData("webrtc")]
    public async Task DemoModeOverridesConfiguredProviderKey(string mode)
    {
        var handler = new ElevenLabsFakeHandler { Respond = _ => throw new InvalidOperationException("No network in demo") };
        var options = ElevenLabsOptions.From(Config(("CIVICOPS_DEMO_MODE", "true"), ("ELEVENLABS_API_KEY", "test-elevenlabs-key"), ("ELEVENLABS_SESSION_MODE", mode)));
        Assert.True(options.Simulated);
        await new ElevenLabsClient(new(handler), options).GetSignedUrlAsync("synthetic");
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("ELEVENLABS_SESSION_MODE", "typo")]
    [InlineData("ELEVENLABS_PHONE_PROVIDER", "invalid")]
    public void InvalidChannelOptionFailsBeforeTransport(string key, string value) => Assert.Throws<InvalidOperationException>(() =>
        ElevenLabsOptions.From(Config((key, value))));

    [Theory]
    [InlineData("tool", "/v1/convai/tools/synthetic")]
    [InlineData("knowledge", "/v1/convai/knowledge-base/synthetic")]
    public async Task ReadOnlyReconciliationUsesDocumentedPaths(string resource, string path)
    {
        var handler = new ElevenLabsFakeHandler();
        var client = new ElevenLabsClient(new(handler), new() { ApiKey = "test-elevenlabs-key" });
        if (resource == "tool") await client.GetToolAsync("synthetic"); else await client.GetKnowledgeAsync("synthetic");
        Assert.Equal("https://api.elevenlabs.io" + path, Assert.Single(handler.Requests).Url);
        Assert.Equal("GET", handler.Requests[0].Method);
    }

    [Fact]
    public async Task KnowledgeContentIsBoundedStreamingTextRatherThanJsonObject()
    {
        var handler = new ElevenLabsFakeHandler { Respond = _ => new(HttpStatusCode.OK) { Content = new StringContent("Synthetic document content.") } };
        Assert.Equal("Synthetic document content.", await new ElevenLabsClient(new(handler), new() { ApiKey = "test-elevenlabs-key" }).GetKnowledgeContentAsync("synthetic"));
        Assert.Equal("https://api.elevenlabs.io/v1/convai/knowledge-base/synthetic/content", Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task ExplicitZendeskCheckUsesOneGetAndDoesNotPrintIdentityOrSecrets()
    {
        var handler = new ZendeskFakeHandler((_, _) => ZendeskFakeHandler.Json("{\"user\":{\"id\":123,\"name\":\"Synthetic Operator\",\"email\":\"operator@example.invalid\"}}"));
        var output = new StringWriter();
        Assert.Equal(0, await ZendeskCheck.RunAsync(["zendesk", "--check"], Config(("ZENDESK_SUBDOMAIN", "synthetic"),
            ("ZENDESK_OAUTH_TOKEN", "test-zendesk-oauth-token")), output, new(handler)));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://synthetic.zendesk.com/api/v2/users/me.json", request.Url);
        Assert.Null(request.Body);
        Assert.DoesNotContain("Synthetic Operator", output.ToString());
        Assert.DoesNotContain("operator@example.invalid", output.ToString());
        Assert.DoesNotContain("test-zendesk-oauth-token", output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZendeskCheckSkipsDemoOrMissingCredentials(bool demo)
    {
        var handler = new ZendeskFakeHandler((_, _) => throw new InvalidOperationException("No network expected"));
        var config = demo ? Config(("CIVICOPS_DEMO_MODE", "true"), ("ZENDESK_SUBDOMAIN", "synthetic"), ("ZENDESK_OAUTH_TOKEN", "test-oauth")) : Config();
        Assert.Equal(2, await ZendeskCheck.RunAsync(["zendesk", "--check"], config, new StringWriter(), new(handler)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ZendeskCheckRequiresExplicitFlag()
    {
        var handler = new ZendeskFakeHandler((_, _) => throw new InvalidOperationException("No network expected"));
        Assert.Equal(2, await ZendeskCheck.RunAsync(["zendesk"], Config(), new StringWriter(), new(handler)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ZendeskSearchReconcilesLaterPages()
    {
        var handler = new ZendeskFakeHandler((_, count) => ZendeskFakeHandler.Json(count == 1
            ? "{\"results\":[],\"next_page\":\"https://synthetic.zendesk.com/api/v2/search.json?page=2\"}"
            : "{\"results\":[{\"id\":321,\"external_id\":\"CIV-SYNTHETIC-1\"}],\"next_page\":null}"));
        Assert.Equal(321, await new ZendeskClient(new(handler), ZendeskFixture.LiveOptions()).FindByExternalIdAsync("CIV-SYNTHETIC-1", default));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task ZendeskCommentsReconcileLaterPagesBeforeSendingAnotherUpdate()
    {
        var item = ZendeskFixture.Item("status"); item.Attempts = 2;
        var handler = new ZendeskFakeHandler((_, count) => ZendeskFakeHandler.Json(count == 1
            ? "{\"comments\":[],\"next_page\":\"https://synthetic.zendesk.com/api/v2/tickets/123/comments.json?page=2\"}"
            : "{\"comments\":[{\"body\":\"[CivicOps event fake-outbox-event]\"}],\"next_page\":null}"));
        var delivery = await new ZendeskClient(new(handler), ZendeskFixture.LiveOptions()).DeliverAsync(item, 123, default);
        Assert.True(delivery.Replayed);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData("https://other.example.invalid/api/v2/search.json")]
    [InlineData("http://synthetic.zendesk.com/api/v2/search.json")]
    [InlineData("https://synthetic.zendesk.com/api/v2/search.json#fragment")]
    [InlineData("https://synthetic.zendesk.com/agent/tickets")]
    public async Task ZendeskPaginationCannotSendCredentialsToAnotherOrigin(string next)
    {
        var handler = new ZendeskFakeHandler((_, _) => ZendeskFakeHandler.Json(new JsonObject { ["results"] = new JsonArray(), ["next_page"] = next }.ToJsonString()));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ZendeskClient(new(handler), ZendeskFixture.LiveOptions()).FindByExternalIdAsync("CIV-SYNTHETIC-1", default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AmbiguousZendeskExternalIdFailsBeforeCreation()
    {
        var handler = new ZendeskFakeHandler((_, _) => ZendeskFakeHandler.Json("{\"results\":[{\"id\":123,\"external_id\":\"CIV-SYNTHETIC-1\"},{\"id\":456,\"external_id\":\"CIV-SYNTHETIC-1\"}]}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ZendeskClient(new(handler), ZendeskFixture.LiveOptions()).FindByExternalIdAsync("CIV-SYNTHETIC-1", default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ZendeskPaginationBudgetFailsClosed()
    {
        var handler = new ZendeskFakeHandler((_, _) => ZendeskFakeHandler.Json("{\"results\":[],\"next_page\":\"https://synthetic.zendesk.com/api/v2/search.json?page=2\"}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ZendeskClient(new(handler), ZendeskFixture.LiveOptions()).FindByExternalIdAsync("CIV-SYNTHETIC-1", default));
        Assert.Equal(20, handler.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZendeskResponseBudgetIncludesUnknownContentLength(bool knownLength)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"results\":[],\"extra\":\"" + new string('x', 1024 * 1024) + "\"}");
        var handler = new ZendeskFakeHandler((_, _) => new(HttpStatusCode.OK) { Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new Unseekable(bytes)) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new ZendeskClient(new(handler), ZendeskFixture.LiveOptions()).FindByExternalIdAsync("CIV-SYNTHETIC-1", default));
    }
    private sealed class Unseekable(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
}
