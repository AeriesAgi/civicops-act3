using System.Net;
using System.Text;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace CivicOps.Tests;

public sealed class LiveProviderTests
{
    private static readonly JsonElement Triage = JsonSerializer.SerializeToElement(new
    {
        workOrderTypeCode = "ROADS.POTHOLE_REPAIR", category = "Roads", department = "Roads and Stormwater", priority = "Medium", confidence = .95,
        rationale = "Potholes need roads maintenance."
    });

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

    private static AiOptions Azure(string auth = "key") => new()
    {
        Provider = "azure-openai", AzureEndpoint = "https://synthetic-resource.openai.azure.com", AzureDeployment = "synthetic-deployment",
        AzureAuth = auth, ApiKey = auth == "key" ? "test-azure-key" : null, Model = "gpt-5-mini", ProviderLabel = "Azure OpenAI (gpt-5-mini)",
        IdentityEndpoint = "http://localhost:42356/msi/token", IdentityHeader = "synthetic-identity-header", TimeoutMs = 2_000
    };

    private static HttpResponseMessage Envelope(object output, string finish = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = JsonSerializer.Serialize(output) }, finish_reason = finish } },
            usage = new { completion_tokens_details = new { reasoning_tokens = 512 } }
        }), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Requests.Add(request); return handle(request); }
    }

    [Fact]
    public async Task AzureKeyRequestUsesDeploymentUrlApiKeyHeaderAndReasoningBudget()
    {
        string? body = null;
        var handler = new Handler(async request =>
        {
            Assert.Equal("https://synthetic-resource.openai.azure.com/openai/deployments/synthetic-deployment/chat/completions?api-version=2025-04-01-preview", request.RequestUri!.ToString());
            Assert.Equal("test-azure-key", request.Headers.GetValues("api-key").Single());
            Assert.Null(request.Headers.Authorization);
            body = await request.Content!.ReadAsStringAsync();
            return Envelope(Triage);
        });
        var result = await new AiReasoningClient(new HttpClient(handler), Azure(), TimeProvider.System).DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, () => Triage.Clone());
        Assert.False(result.Run.FallbackUsed);
        Assert.Equal("Azure OpenAI (gpt-5-mini)", result.Run.Provider);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(4000, json.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("low", json.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(json.RootElement.TryGetProperty("temperature", out _));
        Assert.False(json.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(json.RootElement.TryGetProperty("chat_template_kwargs", out _));
        Assert.Equal("json_schema", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task AzureManagedIdentityFetchesAndCachesBearerToken()
    {
        var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/msi/token")
            {
                Assert.Equal("synthetic-identity-header", request.Headers.GetValues("X-IDENTITY-HEADER").Single());
                Assert.Contains("resource=https%3A%2F%2Fcognitiveservices.azure.com", request.RequestUri.Query);
                var expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"access_token\":\"synthetic-aad-token\",\"expires_on\":\"{expires}\"}}") });
            }
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-aad-token", request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("api-key"));
            return Task.FromResult(Envelope(Triage));
        });
        var client = new AiReasoningClient(new HttpClient(handler), Azure("managed-identity"), TimeProvider.System);
        Assert.False((await client.DecideAsync(AgentSchemas.Triage, new { description = "a" }, () => Triage.Clone())).Run.FallbackUsed);
        Assert.False((await client.DecideAsync(AgentSchemas.Triage, new { description = "b" }, () => Triage.Clone())).Run.FallbackUsed);
        Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath == "/msi/token");
        Assert.StartsWith("live-ready:azure-openai:managed-identity", client.GetStatus().Mode);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("length")]
    [InlineData("identity")]
    public async Task AzureFailuresFallBackToDeterministicRules(string failure)
    {
        var options = Azure(failure == "identity" ? "managed-identity" : "key");
        if (failure == "identity") options.IdentityEndpoint = null;
        var handler = new Handler(_ => Task.FromResult(failure switch
        {
            "error" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => Envelope(Triage, "length")
        }));
        var result = await new AiReasoningClient(new HttpClient(handler), options, TimeProvider.System)
            .DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, () => Triage.Clone());
        Assert.True(result.Run.FallbackUsed);
        Assert.Equal("deterministic-fallback", result.Run.Provider);
        Assert.Equal(failure switch { "error" => "provider_unavailable", "length" => "truncated_output", _ => "identity_unavailable" }, result.Run.Error);
    }

    [Theory]
    [InlineData("http://synthetic.openai.azure.com")]
    [InlineData("https://evil.example.invalid")]
    [InlineData("https://synthetic.openai.azure.com/?x=1")]
    public void AzureEndpointRejectsNonAzureOrInsecureHosts(string endpoint) =>
        Assert.Null(new AiOptions { Provider = "azure-openai", AzureEndpoint = endpoint, AzureDeployment = "d" }.CompletionUri);

    [Fact]
    public void DemoModeStaysOfflineUnlessLlmIsExplicitlyOptedIn()
    {
        var baseSettings = new[] { ("CIVICOPS_DEMO_MODE", "true"), ("CIVICOPS_LLM_PROVIDER", "azure-openai"),
            ("CIVICOPS_AZURE_OPENAI_ENDPOINT", "https://synthetic.openai.azure.com"), ("CIVICOPS_AZURE_OPENAI_DEPLOYMENT", "tablekeeper-demo") };
        Assert.Null(AiOptions.FromConfiguration(Config(baseSettings)).CompletionUri);
        var live = AiOptions.FromConfiguration(Config([.. baseSettings, ("CIVICOPS_DEMO_LIVE_PROVIDERS", "llm")]));
        Assert.NotNull(live.CompletionUri);
        Assert.Equal("managed-identity", live.AzureAuth);
        Assert.Equal("Azure OpenAI (tablekeeper-demo)", live.ProviderLabel);
        Assert.Equal(20_000, live.TimeoutMs);
    }

    [Fact]
    public void ElevenLabsDemoStaysSimulatedUnlessOptedInWithKeyAndAgent()
    {
        (string, string)[] demo = [("CIVICOPS_DEMO_MODE", "true"), ("ELEVENLABS_API_KEY", "test-elevenlabs-key"), ("ELEVENLABS_SESSION_MODE", "signed-url")];
        Assert.True(ElevenLabsOptions.From(Config(demo)).Simulated);
        Assert.True(ElevenLabsOptions.From(Config([.. demo, ("CIVICOPS_DEMO_LIVE_PROVIDERS", "elevenlabs")])).Simulated);
        Assert.False(ElevenLabsOptions.From(Config([.. demo, ("CIVICOPS_DEMO_LIVE_PROVIDERS", "llm,elevenlabs"), ("ELEVENLABS_AGENT_ID", "synthetic-agent")])).Simulated);
        Assert.True(ElevenLabsOptions.From(Config([("CIVICOPS_DEMO_MODE", "true"), ("CIVICOPS_DEMO_LIVE_PROVIDERS", "elevenlabs"), ("ELEVENLABS_AGENT_ID", "synthetic-agent")])).Simulated);
    }

    [Fact]
    public async Task DemoWithLiveElevenLabsMintsSignedUrlServerSideAndKeepsSimulatedFallback()
    {
        using var app = new TestApp();
        app.Settings["CIVICOPS_DEMO_MODE"] = "true";
        app.Settings["CIVICOPS_DEMO_LIVE_PROVIDERS"] = "elevenlabs";
        app.Settings["ELEVENLABS_API_KEY"] = "test-elevenlabs-key";
        app.Settings["ELEVENLABS_AGENT_ID"] = "synthetic-agent";
        app.Settings["ELEVENLABS_SESSION_MODE"] = "signed-url";
        var handler = new ElevenLabsFakeHandler { Respond = _ => ElevenLabsFakeHandler.Json("{\"signed_url\":\"wss://api.elevenlabs.io/v1/convai/conversation?agent_id=synthetic-agent&conversation_signature=synthetic\"}") };
        using var configured = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(sp => new ElevenLabsClient(new(handler), sp.GetRequiredService<ElevenLabsOptions>()))));
        using var client = configured.CreateClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/channels/status");
        Assert.Equal("live", status.GetProperty("elevenlabs").GetProperty("mode").GetString());
        var response = await client.GetAsync("/api/channels/elevenlabs/session");
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("wss://api.elevenlabs.io", text);
        Assert.DoesNotContain("test-elevenlabs-key", text);
        Assert.Contains(handler.Requests, r => r.Url.Contains("get-signed-url?agent_id=synthetic-agent"));
        Assert.All(handler.Requests, r => Assert.Equal("test-elevenlabs-key", r.ApiKey));
    }

    private static AiOptions Gemini() => new()
    {
        Provider = "gemini", BaseUrl = AiOptions.GeminiCompletionUrl, ApiKey = "test-gemini-key", Model = "gemini-2.5-flash",
        ProviderLabel = "Google Gemini (gemini-2.5-flash)", TimeoutMs = 2_000
    };

    [Fact]
    public async Task GeminiUsesOpenAiCompatibleEndpointBearerKeyAndThinkingBudget()
    {
        string? body = null;
        var handler = new Handler(async request =>
        {
            Assert.Equal(AiOptions.GeminiCompletionUrl, request.RequestUri!.ToString());
            Assert.Equal("test-gemini-key", request.Headers.Authorization!.Parameter);
            body = await request.Content!.ReadAsStringAsync();
            return Envelope(Triage);
        });
        var result = await new AiReasoningClient(new HttpClient(handler), Gemini(), TimeProvider.System).DecideAsync(AgentSchemas.Triage, new { description = "x" }, () => Triage.Clone());
        Assert.False(result.Run.FallbackUsed);
        Assert.Equal("Google Gemini (gemini-2.5-flash)", result.Run.Provider);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("gemini-2.5-flash", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(4000, json.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public async Task ChainUsesSecondaryWhenPrimaryFailsThenDeterministic()
    {
        var primary = Azure(); primary.Secondary = Gemini();
        var handler = new Handler(request => Task.FromResult(request.RequestUri!.Host.EndsWith("azure.com", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.PaymentRequired) : Envelope(Triage)));
        var used = await new AiReasoningClient(new HttpClient(handler), primary, TimeProvider.System).DecideAsync(AgentSchemas.Triage, new { description = "x" }, () => Triage.Clone());
        Assert.False(used.Run.FallbackUsed);
        Assert.Equal("Google Gemini (gemini-2.5-flash)", used.Run.Provider);
        Assert.Equal("azure-openai:provider_credits (secondary used)", used.Run.Error);

        var failing = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var both = Azure(); both.Secondary = Gemini();
        var fallback = await new AiReasoningClient(new HttpClient(failing), both, TimeProvider.System).DecideAsync(AgentSchemas.Triage, new { description = "x" }, () => Triage.Clone());
        Assert.True(fallback.Run.FallbackUsed);
        Assert.Equal("deterministic-fallback", fallback.Run.Provider);
        Assert.Equal("azure-openai:provider_unavailable;gemini:provider_unavailable", fallback.Run.Error);
    }

    [Fact]
    public void ProviderChainConfigurationBuildsAzurePrimaryAndGeminiSecondary()
    {
        var options = AiOptions.FromConfiguration(Config(("CIVICOPS_LLM_PROVIDER", "azure-openai,gemini"),
            ("CIVICOPS_AZURE_OPENAI_ENDPOINT", "https://synthetic.openai.azure.com"), ("CIVICOPS_AZURE_OPENAI_DEPLOYMENT", "tablekeeper-demo"),
            ("GEMINI_API_KEY", "test-gemini-key")));
        Assert.True(options.IsAzure);
        Assert.NotNull(options.Secondary);
        Assert.Equal("gemini-2.5-flash", options.Secondary!.Model);
        Assert.Equal(new Uri(AiOptions.GeminiCompletionUrl), options.Secondary.CompletionUri);
        Assert.Null(AiOptions.FromConfiguration(Config(("CIVICOPS_LLM_PROVIDER", "azure-openai,gemini"),
            ("CIVICOPS_AZURE_OPENAI_ENDPOINT", "https://synthetic.openai.azure.com"), ("CIVICOPS_AZURE_OPENAI_DEPLOYMENT", "d"))).Secondary);
        Assert.Throws<InvalidOperationException>(() => AiOptions.FromConfiguration(Config(("CIVICOPS_LLM_PROVIDER", "gemini,gemini"))));
    }

    private static async Task<HttpResponseMessage> Seat(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var name = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString();
        return name switch
        {
            "IntakeRoom_Coordinator" => Envelope(new { queue = "standard-field", confidence = .9, rationale = "Routine roads report." }),
            "IntakeRoom_Extractor" => Envelope(new { locationHint = "Synthetic Ward", hazards = Array.Empty<string>(), confidence = .9, rationale = "Area restated." }),
            "IntakeRoom_Reviewer" => Envelope(new { requiresHumanReview = false, safetyConcern = "none", confidence = .9, rationale = "No hazard." }),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        };
    }

    [Fact]
    public async Task IntakeRoomRunsThreeLabelledSeatsAndAdvisoryRecommendation()
    {
        var client = new AiReasoningClient(new HttpClient(new Handler(Seat)), Azure(), TimeProvider.System);
        var room = new IntakeRoom(client, new IntakeRoomOptions { Enabled = true }, TimeProvider.System);
        var incident = new Incident { Description = "Synthetic pothole on the main road", Area = "Synthetic Ward", ReferenceNumber = "CIV-SYNTHETIC-0001", Category = "Roads", Priority = "Medium" };
        await room.ConveneAsync(incident, default);
        Assert.Equal(["IntakeRoom.Coordinator", "IntakeRoom.Extractor", "IntakeRoom.Reviewer", "IntakeRoom.Recommendation"], incident.AgentRuns.Select(r => r.AgentName));
        Assert.All(incident.AgentRuns.Take(3), r => Assert.Equal("Azure OpenAI (gpt-5-mini)", r.Provider));
        var recommendation = incident.AgentRuns[3];
        Assert.Equal("intake-room-rules", recommendation.Provider);
        Assert.Contains("live: 3/3 seats live", recommendation.Rationale);
        Assert.Contains("a dispatcher approves any dispatch", recommendation.Rationale);
        Assert.Null(incident.AssignedCrewId);
    }

    [Fact]
    public async Task IntakeRoomFallsBackPerSeatAndNeverDowngradesLifeSafety()
    {
        var client = new AiReasoningClient(new HttpClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString() == "IntakeRoom_Coordinator"
                ? Envelope(new { queue = "information-only", confidence = .9, rationale = "Model tries to downgrade." })
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        })), Azure(), TimeProvider.System);
        var room = new IntakeRoom(client, new IntakeRoomOptions { Enabled = true }, TimeProvider.System);
        var incident = new Incident { Description = "Live wire down and a person trapped", Area = "Synthetic Ward", ReferenceNumber = "CIV-SYNTHETIC-0002" };
        await room.ConveneAsync(incident, default);
        Assert.True(incident.AgentRuns[1].FallbackUsed);
        Assert.True(incident.AgentRuns[2].FallbackUsed);
        Assert.Contains("mixed: 1/3 seats live", incident.AgentRuns[3].Rationale);
        Assert.Contains("queue emergency-escalation", incident.AgentRuns[3].Rationale);
        Assert.True(incident.NeedsHumanReview);
    }

    [Fact]
    public async Task IntakeRoomIsOffByDefaultAndSimulatedWithoutProvider()
    {
        var client = new AiReasoningClient(new HttpClient(new Handler(_ => throw new InvalidOperationException("must not call"))), new AiOptions(), TimeProvider.System);
        var incident = new Incident { Description = "Synthetic pothole", Area = "Synthetic Ward", ReferenceNumber = "CIV-SYNTHETIC-0003" };
        await new IntakeRoom(client, IntakeRoomOptions.From(Config()), TimeProvider.System).ConveneAsync(incident, default);
        Assert.Empty(incident.AgentRuns);
        await new IntakeRoom(client, new IntakeRoomOptions { Enabled = true }, TimeProvider.System).ConveneAsync(incident, default);
        Assert.All(incident.AgentRuns.Take(3), r => Assert.Equal("deterministic-fallback", r.Provider));
        Assert.Contains("simulated: 0/3 seats live", incident.AgentRuns[3].Rationale);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task LlmDailyCapSkipsLiveProviderAndLabelsCapReached()
    {
        var calls = 0;
        var handler = new Handler(_ => { calls++; return Task.FromResult(Envelope(Triage)); });
        var caps = new SpendCaps(null, TimeProvider.System, llmDaily: 2, llmMonthly: 100);
        var client = new AiReasoningClient(new HttpClient(handler), Azure(), TimeProvider.System, caps);
        for (var i = 0; i < 2; i++)
            Assert.False((await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, () => Triage.Clone())).Run.FallbackUsed);
        var capped = await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, () => Triage.Clone());
        Assert.True(capped.Run.FallbackUsed);
        Assert.Equal("deterministic-fallback", capped.Run.Provider);
        Assert.Contains("Spend cap reached. Simulation mode", capped.Run.Error);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void SpendCapsPersistAcrossRestartsAndResetPerDayAndMonth()
    {
        var dir = Directory.CreateTempSubdirectory("civicops-caps-").FullName;
        try
        {
            var time = new ManualTime(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));
            var first = new SpendCaps(dir, time, llmDaily: 2, llmMonthly: 3);
            Assert.True(first.TryConsume(SpendCaps.Llm));
            Assert.True(first.TryConsume(SpendCaps.Llm));
            var restarted = new SpendCaps(dir, time, llmDaily: 2, llmMonthly: 3);
            Assert.False(restarted.TryConsume(SpendCaps.Llm));
            time.Now = time.Now.AddDays(1);
            Assert.True(restarted.TryConsume(SpendCaps.Llm));
            Assert.False(restarted.TryConsume(SpendCaps.Llm)); // monthly cap of 3 reached
            time.Now = new DateTimeOffset(2026, 11, 1, 0, 0, 1, TimeSpan.Zero);
            Assert.True(restarted.TryConsume(SpendCaps.Llm));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SpendCapsReadConfiguredLimitsAndZeroDisablesLane()
    {
        var caps = SpendCaps.From(Config(("CIVICOPS_VOICE_DAILY_SESSION_CAP", "0"), ("CIVICOPS_LLM_DAILY_CALL_CAP", "1")), null, TimeProvider.System);
        Assert.False(caps.TryConsume(SpendCaps.Voice));
        Assert.True(caps.TryConsume(SpendCaps.Llm));
        Assert.False(caps.TryConsume(SpendCaps.Llm));
    }

    [Fact]
    public void RoomSeatSchemaNamesAreValidForAzureAndGemini()
    {
        foreach (var agent in new[] { AgentSchemas.RoomCoordinator, AgentSchemas.RoomExtractor, AgentSchemas.RoomReviewer })
            foreach (var options in new[] { Azure(), new AiOptions { Provider = "gemini", Model = "gemini-3.5-flash-lite", ApiKey = "test" } })
            {
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(AiReasoningClient.BuildRequest(options, agent, new { description = "Synthetic" })));
                var name = json.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString()!;
                Assert.Matches("^[A-Za-z0-9_-]{1,64}$", name);
            }
    }
}
