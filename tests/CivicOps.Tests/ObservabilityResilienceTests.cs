using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace CivicOps.Tests;

public sealed class ObservabilityResilienceTests
{
    [Fact]
    public async Task CircuitOpensSkipsRequestsAndAllowsOneRecoveryProbe()
    {
        var clock = new ZendeskClock(); var circuit = new ProviderCircuit(clock); var calls = 0;
        Task<int> Failing(CancellationToken _) { calls++; throw new HttpRequestException("Synthetic provider failure"); }
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<HttpRequestException>(() => circuit.ExecuteAsync(Failing, TimeSpan.FromSeconds(1), default));
        await Assert.ThrowsAsync<ProviderCircuitOpenException>(() => circuit.ExecuteAsync(Failing, TimeSpan.FromSeconds(1), default));
        Assert.Equal(3, calls); Assert.Equal("open", circuit.Describe(true).Circuit);
        clock.Advance(TimeSpan.FromSeconds(31));
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = circuit.ExecuteAsync(_ => release.Task, TimeSpan.FromSeconds(1), default);
        await Assert.ThrowsAsync<ProviderCircuitOpenException>(() => circuit.ExecuteAsync(_ => Task.FromResult(2), TimeSpan.FromSeconds(1), default));
        release.SetResult(7); Assert.Equal(7, await recovery); Assert.Equal("healthy", circuit.Describe(true).Status);
        Assert.Equal("closed", circuit.Describe(true).Circuit);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCountAsProviderFailure()
    {
        var circuit = new ProviderCircuit(TimeProvider.System); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        for (var i = 0; i < 4; i++) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => circuit.ExecuteAsync<int>(
            token => Task.FromCanceled<int>(token), TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.Equal("closed", circuit.Describe(true).Circuit);
    }

    public static IEnumerable<object[]> ProvidersAndBackends() => StoreBackends.Available().SelectMany(backend => new[] { "ai", "elevenlabs", "zendesk" }
        .SelectMany(provider => new[] { false, true }.Select(slow => new object[] { provider, slow, backend[0] })));
    [Theory]
    [MemberData(nameof(ProvidersAndBackends))]
    public async Task UnavailableAndSlowProvidersKeepIntakeAndReadinessAvailable(string provider, bool slow, string backend)
    {
        using var selected = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false };
        app.Settings["CIVICOPS_DEMO_MODE"] = "false"; app.Settings["CIVICOPS_PROVIDER_TIMEOUT_MS"] = "250";
        app.Settings["CIVICOPS_LLM_TIMEOUT_MS"] = "250";
        if (provider == "ai") app.Settings["CIVICOPS_LLM_BASE_URL"] = "http://127.0.0.1:18001";
        if (provider == "elevenlabs") app.Settings["ELEVENLABS_API_KEY"] = "synthetic-resilience-key";
        if (provider == "zendesk") { app.Settings["ZENDESK_SUBDOMAIN"] = "synthetic"; app.Settings["ZENDESK_OAUTH_TOKEN"] = "synthetic-resilience-token"; }
        var calls = 0;
        using var factory = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient("civicops-" + (provider == "elevenlabs" ? "elevenlabs" : provider)).ConfigurePrimaryHttpMessageHandler(() => new AsyncHandler(async (_, token) =>
            {
                Interlocked.Increment(ref calls);
                if (slow) await Task.Delay(TimeSpan.FromSeconds(5), token);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Synthetic provider unavailable") };
            }))));
        using var http = factory.CreateClient();
        if (provider == "elevenlabs")
        {
            if (slow) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.Services.GetRequiredService<ElevenLabsClient>().GetAgentAsync("synthetic-agent"));
            else await Assert.ThrowsAnyAsync<HttpRequestException>(() => factory.Services.GetRequiredService<ElevenLabsClient>().GetAgentAsync("synthetic-agent"));
        }
        var response = await http.PostAsJsonAsync("/api/incidents", new { description = "Synthetic water leak beside demonstration hall", area = "Synthetic ward", privacyConsentAccepted = true, privacyConsentVersion = PrivacyConsent.Version });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Task<int>? delivery = null;
        if (provider == "zendesk") delivery = factory.Services.GetRequiredService<IntegrationOutbox>().ProcessDueAsync(default);
        // Readiness must not wait on the provider queue lock or perform a provider network probe.
        var ready = await http.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        if (delivery is not null) { Assert.Equal(1, await delivery); ready = await http.GetAsync("/health/ready"); }
        var health = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("degraded", health.GetProperty("status").GetString()); Assert.True(health.GetProperty("ready").GetBoolean());
        Assert.Equal("degraded", health.GetProperty("providers").GetProperty(provider).GetProperty("status").GetString());
        Assert.True(calls > 0);
        var incident = Assert.Single(await factory.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        Assert.True((await factory.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
        if (provider == "ai") { Assert.All(incident.AgentRuns, run => Assert.True(run.FallbackUsed)); Assert.Contains(incident.AgentRuns, run => run.Error == "circuit_open"); }
        if (provider == "zendesk")
        {
            var item = Assert.Single(await factory.Services.GetRequiredService<IntegrationOutbox>().ListAsync(default));
            Assert.Equal(IntegrationDeliveryState.Pending, item.State); Assert.True(item.NextAttemptAt > item.CreatedAt);
        }
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task HealthSeparatesProcessLivenessFromBacklogReadinessAndKeepsLegacyCompatible(string backend)
    {
        using var selected = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false }; app.Settings["CIVICOPS_OUTBOX_READY_MAX"] = "0";
        using var http = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/ready")).StatusCode);
        var legacy = await http.GetFromJsonAsync<JsonElement>("/healthz"); Assert.True(legacy.GetProperty("demoMode").GetBoolean());
        await FoundationTests.Create(http);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ReadinessIncludesPersistedMunicipalityQueuesBeforeDeliveryWorkerLoadsThem(string backend)
    {
        using var selected = StoreBackends.Use(backend);
        string directory;
        using (var app = new TestApp { EnableBackgroundDelivery = false, KeepData = true })
        {
            directory = app.DataDirectory; using var http = app.CreateClient(); using var tenant = TenantContext.Use("synthetic-coast");
            var incident = (await app.Services.GetRequiredService<IncidentStore>().CreateAsync(new Incident
            { Description = "Synthetic coastal water leak", Area = "Synthetic coast", NormalizedArea = "Synthetic coast" }, null, new DeterministicClassifier(), default)).Incident;
            await app.Services.GetRequiredService<IntegrationOutbox>().EnqueueAsync(incident, "create", default);
        }
        using var restarted = new TestApp { EnableBackgroundDelivery = false, DataDirectory = directory };
        restarted.Settings["CIVICOPS_OUTBOX_READY_MAX"] = "0"; using var client = restarted.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task CorrelationAndTraceparentAreEchoedInHeadersAndProblemDetails()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var http = app.CreateClient();
        const string trace = "1234567890abcdef1234567890abcdef";
        http.DefaultRequestHeaders.Add("traceparent", "00-" + trace + "-1234567890abcdef-01");
        http.DefaultRequestHeaders.Add("X-Correlation-ID", "synthetic-request-123");
        var response = await http.GetAsync("/api/incidents/synthetic-missing"); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("synthetic-request-123", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Contains(trace, response.Headers.GetValues("traceparent").Single());
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(trace, problem.GetProperty("traceId").GetString()); Assert.Equal("synthetic-request-123", problem.GetProperty("correlationId").GetString());
    }

    [Theory]
    [InlineData("resident@example.invalid")] [InlineData("0825550100")] [InlineData("bad id")] [InlineData("bad/id")]
    public void UntrustedCorrelationValuesDoNotEnterLogsOrBaggage(string value) => Assert.False(CorrelationMiddleware.IsValid(value));

    [Fact]
    public async Task ProviderRequestsPropagateW3cContextAndCorrelationWithoutResidentContent()
    {
        using var parent = new Activity("synthetic.request").SetIdFormat(ActivityIdFormat.W3C).Start();
        parent.AddBaggage("civicops.correlation_id", "synthetic-correlation");
        var propagated = 0;
        using var http = new HttpClient(new AsyncHandler((request, _) =>
        {
            Assert.Contains(parent.TraceId.ToHexString(), request.Headers.GetValues("traceparent").Single());
            Assert.Equal("synthetic-correlation", request.Headers.GetValues("X-Correlation-ID").Single()); propagated++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"agent_id\":\"synthetic-agent\"}") });
        }));
        await new ElevenLabsClient(http, new ElevenLabsOptions { ApiKey = "synthetic-test-key" }).GetAgentAsync("synthetic-agent");
        Assert.Equal(1, propagated);
    }

    [Fact]
    public void TraceExporterReceivesSanitizedTagsAndNoUrlOrSqlPayload()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_LLM_API_KEY"] = "synthetic-observability-secret" }).Build();
        var exporter = new CaptureExporter();
        using var telemetry = Sdk.CreateTracerProviderBuilder().AddSource("Synthetic.SafeSpan")
            .AddProcessor(new CivicOpsTelemetry.SafeSpanProcessor(new SensitiveValueRedactor(configuration)))
            .AddProcessor(new SimpleActivityExportProcessor(exporter)).Build();
        using var source = new ActivitySource("Synthetic.SafeSpan");
        using (var activity = source.StartActivity("synthetic.operation"))
        {
            Assert.NotNull(activity);
            activity.SetTag("url.full", "https://example.invalid/?email=resident@example.invalid");
            activity.SetTag("db.query.text", "select 'resident@example.invalid'");
            activity.SetTag("synthetic.detail", "synthetic-observability-secret resident@example.invalid");
        }
        var exported = Assert.Single(exporter.Spans); Assert.Null(exported.GetTagItem("url.full")); Assert.Null(exported.GetTagItem("db.query.text"));
        Assert.DoesNotContain("synthetic-observability-secret", exported.GetTagItem("synthetic.detail")!.ToString());
        Assert.DoesNotContain("resident@example.invalid", exported.GetTagItem("synthetic.detail")!.ToString());
    }

    [Fact]
    public void StructuredLogsAndExternalScopesRetainFieldsAfterSecretAndPiiRedaction()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_LLM_API_KEY"] = "synthetic-structured-secret" }).Build();
        var captured = new StructuredProvider();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(new RedactingLoggerProvider(captured, new SensitiveValueRedactor(configuration))));
        var logger = logs.CreateLogger("Synthetic.Logging");
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = "synthetic-scope", ["Resident"] = "resident@example.invalid" });
        LoggerMessage.Define<string, int>(LogLevel.Information, new EventId(4603), "Provider {ProviderValue} status {StatusCode}")(logger, "synthetic-structured-secret", 503, null);
        Assert.Equal(503, captured.Fields["StatusCode"]); Assert.Equal("[REDACTED-SECRET]", captured.Fields["ProviderValue"]);
        Assert.Equal("synthetic-scope", captured.Scopes["CorrelationId"]); Assert.DoesNotContain("resident@example.invalid", captured.Scopes["Resident"]!.ToString());
    }

    [Fact]
    public void DemoConfigurationSuppressesExternalTelemetryAndModelRequests()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false };
        app.Settings["CIVICOPS_TELEMETRY"] = "otlp"; app.Settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:18099";
        app.Settings["CIVICOPS_LLM_BASE_URL"] = "http://127.0.0.1:18001";
        using var http = app.CreateClient(); Assert.Null(app.Services.GetService<TracerProvider>());
        Assert.Null(app.Services.GetRequiredService<AiOptions>().CompletionUri);
    }

    public static IEnumerable<object[]> ProbeCredentialsAndBackends() => StoreBackends.Available().SelectMany(backend =>
        new[] { "none", "bearer", "bootstrap-cookie" }.Select(credential => new object[] { credential, backend[0] }));
    [Theory]
    [MemberData(nameof(ProbeCredentialsAndBackends))]
    public async Task GlobalProbesSkipTenantCatalogueAndCredentialReads(string credential, string backend)
    {
        using var selected = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var storage = app.Services.GetRequiredService<DurableStorage>();
        if (credential == "bearer") client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-unrecognised-probe-token");
        if (credential == "bootstrap-cookie")
        {
            var issued = await app.Services.GetRequiredService<BootstrapUserStore>().CreateAsync("default", new("synthetic-probe-supervisor", "Synthetic probe supervisor", ["supervisor"]), "Synthetic administrator", default);
            var status = await client.GetFromJsonAsync<JsonElement>("/auth/status");
            client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
            var login = await client.PostAsJsonAsync("/auth/bootstrap-login", new { token = issued.Token }); login.EnsureSuccessStatusCode();
            // A poisoned user file proves OnValidatePrincipal does not refresh bootstrap credentials on probes.
            await storage.WriteAsync(Path.Combine(app.DataDirectory, "bootstrap-users.json"), "{}", default);
        }
        await storage.WriteAsync(Path.Combine(app.DataDirectory, "tenants.json"), "{}", default);
        await storage.WriteAsync(Path.Combine(app.DataDirectory, "catalogue-revisions.json"), "{}", default);
        foreach (var path in new[] { "/health/live", "/healthz" })
        {
            var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.Contains("X-Correlation-ID")); Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        }
        if (!storage.IsPostgres) await storage.WriteAsync(Path.Combine(app.DataDirectory, "integration-outbox.json"), "{}", default);
        // Readiness alone owns bounded storage checks; malformed durable queue state reports unavailable.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        var ordinary = await client.GetAsync("/api/crews");
        Assert.Equal(HttpStatusCode.InternalServerError, ordinary.StatusCode);
        Assert.True(TestApp.HandledErrors.TryRemove(ordinary.Headers.GetValues("X-Correlation-ID").Single(), out var diagnostic));
        Assert.Contains("CivicOps.Services", diagnostic);
    }

    [Fact]
    public async Task MetricsAndAgentSpansRecordFallbackWithoutResidentAttributes()
    {
        var measurements = new List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener { InstrumentPublished = (instrument, meter) => { if (instrument.Meter.Name == CivicOpsTelemetry.Name) meter.EnableMeasurementEvents(instrument); } };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => { lock (measurements) measurements.Add((instrument.Name, value, tags.ToArray())); }); listener.Start();
        var spans = new CaptureExporter(); using var tracing = Sdk.CreateTracerProviderBuilder().AddSource(CivicOpsTelemetry.Name)
            .AddProcessor(new SimpleActivityExportProcessor(spans)).Build();
        var ai = new AiReasoningClient(new HttpClient(new AsyncHandler((_, _) => throw new InvalidOperationException("Simulation must not call HTTP"))), new AiOptions(), TimeProvider.System);
        await ai.DecideAsync(AgentSchemas.Intake, new { description = "Synthetic resident@example.invalid water leak" }, () => AgentSchemas.Element(new
        { what = "Synthetic leak", where = "Synthetic ward", hazards = Array.Empty<string>(), language = "en", contactPreference = "none", confidence = .9, rationale = "Synthetic deterministic fallback" }));
        Assert.Contains(measurements, item => item.Name == "civicops.ai.fallbacks" && item.Value == 1);
        Assert.Contains(spans.Spans, span => span.OperationName == "agent.IntakeAgent" && Equals(span.GetTagItem("agent.fallback"), true));
        Assert.DoesNotContain(measurements.SelectMany(item => item.Tags), tag => tag.Value?.ToString()?.Contains("resident@", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void RetryJitterRemainsBoundedAndVaries()
    {
        using var fixture = new ZendeskFixture(); using var queue = new IntegrationOutbox(new ZendeskClient(new HttpClient(), fixture.Options), fixture.Options, fixture.Store, fixture.Clock);
        var delays = Enumerable.Range(0, 30).Select(_ => queue.RetryDelay(2).TotalMilliseconds).ToArray();
        Assert.All(delays, delay => Assert.InRange(delay, 10_000, 12_000)); Assert.True(delays.Distinct().Count() > 1);
        Assert.Equal(TimeSpan.FromHours(1), queue.RetryDelay(100));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class CaptureExporter : BaseExporter<Activity>
    {
        public List<Activity> Spans { get; } = [];
        public override ExportResult Export(in Batch<Activity> batch) { foreach (var span in batch) Spans.Add(span); return ExportResult.Success; }
    }
    private sealed class StructuredProvider : ILoggerProvider, ILogger, ISupportExternalScope
    {
        private IExternalScopeProvider? _scopes;
        public Dictionary<string, object?> Fields { get; } = [];
        public Dictionary<string, object?> Scopes { get; } = [];
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _scopes?.Push(state);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> fields) foreach (var field in fields) Fields[field.Key] = field.Value;
            _scopes?.ForEachScope((scope, _) => { if (scope is IEnumerable<KeyValuePair<string, object?>> values) foreach (var value in values) Scopes[value.Key] = value.Value; }, 0);
        }
        public void Dispose() { }
    }
}
