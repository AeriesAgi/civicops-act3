using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class TrainingTests
{
    private const string WorkerId = "synthetic-water-01-a";
    private const string Prefix = "/api/v1/training/";
    private static TrainingCompletionRequest Completion() => new(WorkerId, "synthetic-water-module-v1", "WATER.BURST_MAIN",
        WorkOrderCatalog.Default.Find("WATER.BURST_MAIN")!.RequiredSkills.Select(skill => skill.Code).ToArray(),
        92, true, 480, 1, "synthetic-device", new string('a', 64), DateTimeOffset.UtcNow.AddMinutes(-1));
    private static HttpClient Client(TestApp app, string? token = TrainingAccess.DemoToken)
    {
        var client = app.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    private static async Task<HttpResponseMessage> Post<T>(HttpClient client, string endpoint, T request, string? key = "training-key")
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Prefix + endpoint) { Content = JsonContent.Create(request) };
        if (key is not null) message.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(message);
    }
    private static async Task<string> Complete(HttpClient client, TrainingCompletionRequest? record = null, string key = "training-key")
    {
        using var response = await Post(client, "completions", record ?? Completion(), key);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("completion").GetProperty("id").GetString()!;
    }

    [Theory]
    [InlineData("work-order-types", "workOrderTypes", "work-order-types-response.schema.json")]
    [InlineData("skills", "skills", "skills-response.schema.json")]
    [InlineData("certification-types", "certificationTypes", "certification-types-response.schema.json")]
    public async Task CatalogueUsesSharedValidatedVersionEtagAndSchemas(string path, string collection, string schema)
    {
        using var app = new TestApp(); using var client = Client(app);
        using var response = await client.GetAsync(Prefix + path); response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(WorkOrderCatalog.Default.Version, body.GetProperty("version").GetString());
        Assert.NotEmpty(body.GetProperty(collection).EnumerateArray());
        Assert.Equal(WorkOrderCatalog.Default.ETag, response.Headers.ETag!.Tag);
        Assert.Equal(WorkOrderCatalog.Default.Version, Assert.Single(response.Headers.GetValues("X-CivicOps-Catalog-Version")));
        SchemaContract.Validate(body, schema);
        client.DefaultRequestHeaders.IfNoneMatch.Add(response.Headers.ETag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.GetAsync(Prefix + path)).StatusCode);
    }

    [Fact]
    public void CheckedInCatalogueConformsToPortableSchema() => SchemaContract.Validate(
        JsonDocument.Parse(File.ReadAllText(Path.Combine(SchemaContract.RepositoryRoot, "products/civicops/catalog/work-orders.v1.json"))).RootElement,
        "work-order-catalog.schema.json");

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task QualificationsAndScenarioHintsExposeSyntheticDataOnly(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = Client(app);
        var body = await client.GetFromJsonAsync<JsonElement>(Prefix + "workers/" + WorkerId + "/qualifications");
        SchemaContract.Validate(body, "worker-qualifications.schema.json"); Assert.True(body.GetProperty("synthetic").GetBoolean());
        var hint = await client.GetFromJsonAsync<JsonElement>(Prefix + "work-order-types/WATER.BURST_MAIN/scenarios-hint");
        Assert.True(hint.GetProperty("synthetic").GetBoolean()); Assert.Equal(2, hint.GetProperty("examples").GetArrayLength());
        Assert.DoesNotContain("incidentId", hint.GetRawText()); Assert.DoesNotContain("contactEmail", hint.GetRawText());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Prefix + "workers/unknown/qualifications")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Prefix + "work-order-types/UNKNOWN.CODE/scenarios-hint")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task CompletionRequiresAuthenticationMandatoryIdempotencyAndMatchesSchema(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var anonymous = Client(app, null); using var client = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, "completions", Completion())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "completions", Completion(), null)).StatusCode);
        var request = Completion(); SchemaContract.Validate(JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "training-completion.schema.json");
        using var response = await Post(client, "completions", request); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(); SchemaContract.Validate(body, "training-completion-response.schema.json");
        Assert.False(body.GetProperty("replayed").GetBoolean());
        using var replay = await Post(client, "completions", request); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, "completions", request with { Score = 91 })).StatusCode);
        Assert.Single(await app.Services.GetRequiredService<TrainingStore>().AuditAsync(CancellationToken.None));
        Assert.Single(await app.Services.GetRequiredService<IncidentStore>().AuditAsync(CancellationToken.None), entry => entry.Action == "training.completed");
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ConcurrentExactReplayCreatesOnlyOneCompletionAndAuditEntry(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = Client(app); var request = Completion();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Post(client, "completions", request)));
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, results.Count(response => response.StatusCode == HttpStatusCode.OK));
        foreach (var response in results) response.Dispose();
        Assert.Single(await app.Services.GetRequiredService<TrainingStore>().AuditAsync(CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task StoreIsolatesMutableRequestAndReadCollectionsFromItsAuthenticatedSnapshot(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = Client(app); var store = app.Services.GetRequiredService<TrainingStore>();
        var request = Completion(); var skills = request.SkillsPractised.ToArray(); request = request with { SkillsPractised = skills };
        var result = await store.CompleteAsync(request, "isolated-collections", "demo-training", CancellationToken.None);
        skills[0] = "UNKNOWN.CODE";
        ((string[])result.Record!.Record.SkillsPractised)[0] = "UNKNOWN.RESULT";
        var read = await store.QualificationsAsync(WorkerId, CancellationToken.None);
        ((string[])read[0].Record.SkillsPractised)[0] = "UNKNOWN.READ";
        Assert.True((await store.VerifyAsync(CancellationToken.None)).Valid);
        Assert.Equal("WATER.PIPE_REPAIR", (await store.QualificationsAsync(WorkerId, CancellationToken.None))[0].Record.SkillsPractised[0]);
    }

    [Theory]
    [InlineData("work-order-types")] [InlineData("skills")] [InlineData("certification-types")]
    [InlineData("workers/synthetic-water-01-a/qualifications")] [InlineData("stream")]
    public async Task LiveModeDoesNotAcceptDemoTrainingClient(string endpoint)
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false"; app.Settings["CIVICOPS_TRAINING_CLIENTS"] = "";
        using var client = Client(app); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(Prefix + endpoint)).StatusCode);
    }

    [Theory]
    [InlineData("/api/incidents")] [InlineData("/api/dispatch/incidents/unknown")]
    [InlineData("/api/audit")] [InlineData("/api/training/audit")]
    [InlineData("/api/channels/elevenlabs/tools/list_service_categories")]
    public async Task TrainingBearerCannotReadOtherRoutesEvenInDemo(string endpoint)
    {
        using var app = new TestApp(); using var client = Client(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(endpoint)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task TrainingBearerCannotWriteIncidentsOrUseDispatcherTokenOnTrainingRoutes(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = Client(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic pipe leak" })).StatusCode);
        using var dispatcher = Client(app, "test-dispatch-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await dispatcher.GetAsync(Prefix + "skills")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ExplicitClientsDisableDemoTokenPartitionQuotasAndAuthenticateBeforeCharging(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); app.Settings["CIVICOPS_TRAINING_CLIENTS"] = "client-one:" + IncidentStore.Hash("synthetic-token-one") + ";client-two:" + IncidentStore.Hash("synthetic-token-two");
        app.Settings["CIVICOPS_TRAINING_LIMIT"] = "1";
        using var bad = Client(app, "invalid-token"); using var first = Client(app, "synthetic-token-one"); using var second = Client(app, "synthetic-token-two"); using var demo = Client(app);
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync(Prefix + "skills")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await demo.GetAsync(Prefix + "skills")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync(Prefix + "skills")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await first.GetAsync(Prefix + "skills")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync(Prefix + "skills")).StatusCode);
    }

    [Theory]
    [InlineData("malformed")] [InlineData("a:invalid")]
    public void MalformedTrainingClientConfigurationFailsClosed(string setting)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_TRAINING_CLIENTS"] = setting }).Build();
        Assert.Throws<InvalidOperationException>(() => new TrainingAccess(config));
    }

    [Theory]
    [InlineData("duplicate-name")] [InlineData("duplicate-token")] [InlineData("dispatcher-hash")] [InlineData("legacy-dispatcher")]
    public void DuplicateOrCrossScopeTokensFailClosed(string kind)
    {
        var hash = IncidentStore.Hash("synthetic-token-one");
        var settings = new Dictionary<string, string?> { ["CIVICOPS_TRAINING_CLIENTS"] = "first:" + hash };
        if (kind == "duplicate-name") settings["CIVICOPS_TRAINING_CLIENTS"] += ";first:" + IncidentStore.Hash("synthetic-token-two");
        if (kind == "duplicate-token") settings["CIVICOPS_TRAINING_CLIENTS"] += ";second:" + hash;
        if (kind == "dispatcher-hash") settings["CIVICOPS_DISPATCHERS"] = "dispatcher:" + hash;
        if (kind == "legacy-dispatcher") settings["CIVICOPS_DISPATCH_TOKEN"] = "synthetic-token-one";
        Assert.Throws<InvalidOperationException>(() => new TrainingAccess(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }

    [Theory]
    [InlineData("workerId")] [InlineData("moduleId")] [InlineData("workOrderTypeCode")]
    [InlineData("skillsPractised")] [InlineData("score")] [InlineData("passed")]
    [InlineData("durationSeconds")] [InlineData("attempt")] [InlineData("deviceClientLabel")]
    [InlineData("evidenceHash")] [InlineData("completedAt")]
    public async Task EveryCompletionSchemaFieldIsRequired(string missing)
    {
        using var app = new TestApp(); using var client = Client(app);
        var json = JsonSerializer.SerializeToNode(Completion(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject(); json.Remove(missing);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "completions", json)).StatusCode);
    }

    [Theory]
    [InlineData("unknown-worker")] [InlineData("unknown-work-type")] [InlineData("unknown-skill")]
    [InlineData("duplicate-skill")] [InlineData("empty-skills")] [InlineData("bad-evidence")]
    [InlineData("score")] [InlineData("duration")] [InlineData("attempt")] [InlineData("future-time")]
    [InlineData("device-label")] [InlineData("extra-field")]
    public async Task CompletionRejectsInvalidOrUnboundedFields(string kind)
    {
        using var app = new TestApp(); using var client = Client(app); var request = Completion();
        object invalid = kind switch
        {
            "unknown-worker" => request with { WorkerId = "unknown" },
            "unknown-work-type" => request with { WorkOrderTypeCode = "UNKNOWN.CODE" },
            "unknown-skill" => request with { SkillsPractised = ["UNKNOWN.CODE"] },
            "duplicate-skill" => request with { SkillsPractised = [request.SkillsPractised[0], request.SkillsPractised[0]] },
            "empty-skills" => request with { SkillsPractised = [] },
            "bad-evidence" => request with { EvidenceHash = "raw synthetic recording" },
            "score" => request with { Score = 101 }, "duration" => request with { DurationSeconds = 86401 },
            "attempt" => request with { Attempt = 0 }, "future-time" => request with { CompletedAt = DateTimeOffset.UtcNow.AddHours(1) },
            "device-label" => request with { DeviceClientLabel = new string('a', 81) },
            _ => JsonSerializer.SerializeToNode(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject()
        };
        if (kind == "extra-field") ((JsonObject)invalid)["residentEmail"] = "synthetic@example.invalid";
        Assert.Equal(kind == "unknown-worker" ? HttpStatusCode.NotFound : HttpStatusCode.BadRequest,
            (await Post(client, "completions", invalid)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task CompletionRejectsDuplicateFieldsAndOversizedPayload(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = Client(app);
        var raw = JsonSerializer.Serialize(Completion(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var duplicate = new HttpRequestMessage(HttpMethod.Post, Prefix + "completions")
        { Content = new StringContent(raw[..^1] + ",\"passed\":true}", Encoding.UTF8, "application/json") };
        duplicate.Headers.Add("Idempotency-Key", "duplicate-fields");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(duplicate)).StatusCode);
        using var oversized = new HttpRequestMessage(HttpMethod.Post, Prefix + "completions")
        { Content = new StringContent(new string(' ', 17000) + raw, Encoding.UTF8, "application/json") };
        oversized.Headers.Add("Idempotency-Key", "oversized");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(oversized)).StatusCode);
        using var duplicateKeys = new HttpRequestMessage(HttpMethod.Post, Prefix + "completions") { Content = JsonContent.Create(Completion()) };
        duplicateKeys.Headers.Add("Idempotency-Key", new[] { "first-key", "second-key" });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(duplicateKeys)).StatusCode);
    }

    [Theory]
    [InlineData("workerId")] [InlineData("certificationTypeCode")] [InlineData("completionId")]
    [InlineData("supervisorSignOff")] [InlineData("signOffReference")]
    public async Task EveryCertificationSchemaFieldAndItsIdempotencyKeyAreRequired(string missing)
    {
        using var app = new TestApp(); using var client = Client(app); var id = await Complete(client);
        var request = new TrainingCertificationRequest(WorkerId, "SAFETY.PPE", id, true, "synthetic-signoff");
        var json = JsonSerializer.SerializeToNode(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject(); json.Remove(missing);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "certifications", json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "certifications", request, null)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task CertificationPayloadBoundAppliesBeforeJsonParsing(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Post, Prefix + "certifications")
        { Content = new StringContent(new string(' ', 8193) + "{}", Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "oversized-certification");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task IdempotencyKeysArePartitionedByAuthenticatedClient(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); app.Settings["CIVICOPS_TRAINING_CLIENTS"] = "client-one:" + IncidentStore.Hash("synthetic-token-one") + ";client-two:" + IncidentStore.Hash("synthetic-token-two");
        using var first = Client(app, "synthetic-token-one"); using var second = Client(app, "synthetic-token-two");
        var request = Completion(); var firstId = await Complete(first, request); var secondId = await Complete(second, request);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, (await app.Services.GetRequiredService<TrainingStore>().AuditAsync(CancellationToken.None)).Count);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task SimulationGrantRequiresPassingCompletionAndSignOffAndNeverGrantsExternalCertificates(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = Client(app); var id = await Complete(client);
        var request = new TrainingCertificationRequest(WorkerId, "SAFETY.PPE", id, true, "synthetic-supervisor-signoff");
        SchemaContract.Validate(JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "training-certification.schema.json");
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "certifications", request with { SupervisorSignOff = false }, "no-signoff")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "certifications", request with { CertificationTypeCode = "ELEC.AUTHORISED_PERSON" }, "external")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "certifications", request with { CompletionId = "unknown" }, "missing")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "certifications", request with { WorkerId = "synthetic-water-01-b" }, "wrong-worker")).StatusCode);
        using var grant = await Post(client, "certifications", request, "grant"); Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
        var body = await grant.Content.ReadFromJsonAsync<JsonElement>(); SchemaContract.Validate(body, "training-certification-response.schema.json");
        var cert = body.GetProperty("grant").GetProperty("certification");
        Assert.Equal("simulation", cert.GetProperty("source").GetString()); Assert.Equal("demo-training", cert.GetProperty("issuer").GetString());
        Assert.Equal(cert.GetProperty("issuedAt").GetDateTimeOffset().AddMonths(12), cert.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "certifications", request, "grant")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, "certifications", request with { SignOffReference = "different" }, "grant")).StatusCode);
        Assert.True((await app.Services.GetRequiredService<TrainingStore>().VerifyAsync(CancellationToken.None)).Valid);
        Assert.True((await app.Services.GetRequiredService<WorkforceStore>().VerifyAsync(CancellationToken.None)).Valid);
        Assert.True((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(CancellationToken.None)).Valid);
    }

    [Theory]
    [InlineData("failed")] [InlineData("score-policy")] [InlineData("grants-disabled")] [InlineData("missing-required-skill")]
    public async Task ConfigurableSimulationRulesBlockIneligibleGrants(string reason)
    {
        using var app = new TestApp();
        if (reason == "score-policy") app.Settings["CIVICOPS_TRAINING_PASS_SCORE"] = "95";
        if (reason == "grants-disabled") app.Settings["CIVICOPS_TRAINING_CERTIFICATIONS_ENABLED"] = "false";
        using var client = Client(app); var request = Completion();
        if (reason == "failed") request = request with { Passed = false };
        if (reason == "missing-required-skill") request = request with { SkillsPractised = [WorkOrderCatalog.Default.Skills.First(skill => !request.SkillsPractised.Contains(skill.Code)).Code] };
        var id = await Complete(client, request);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "certifications", new TrainingCertificationRequest(WorkerId, "SAFETY.PPE", id, true, "synthetic-signoff"))).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task RestartPreservesCompletionGrantReplayQualificationAndAudit(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        var first = new TestApp { KeepData = true }; var directory = first.DataDirectory; var completion = Completion(); string id;
        try
        {
            using (first)
            using (var client = Client(first))
            {
                id = await Complete(client, completion);
                (await Post(client, "certifications", new TrainingCertificationRequest(WorkerId, "SAFETY.PPE", id, true, "synthetic-signoff"), "grant")).EnsureSuccessStatusCode();
            }
            using var restarted = new TestApp { DataDirectory = directory }; using var restartClient = Client(restarted);
            using var replay = await Post(restartClient, "completions", completion); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var replayId = (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("completion").GetProperty("id").GetString(); Assert.Equal(id, replayId);
            Assert.Equal(HttpStatusCode.OK, (await Post(restartClient, "certifications", new TrainingCertificationRequest(WorkerId, "SAFETY.PPE", id, true, "synthetic-signoff"), "grant")).StatusCode);
            var qualification = await restartClient.GetFromJsonAsync<JsonElement>(Prefix + "workers/" + WorkerId + "/qualifications");
            Assert.Single(qualification.GetProperty("trainingCompletions").EnumerateArray());
            Assert.Contains(qualification.GetProperty("certifications").EnumerateArray(), cert => cert.GetProperty("evidenceRef").GetString() == "sha256:" + completion.EvidenceHash);
            Assert.Equal(2, (await restarted.Services.GetRequiredService<TrainingStore>().AuditAsync(CancellationToken.None)).Count);
            Assert.Equal(2, (await restarted.Services.GetRequiredService<IncidentStore>().AuditAsync(CancellationToken.None)).Count(entry => entry.Action is "training.completed" or "certification.granted"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StartupRecoversSavedGrantIntentAndMissingSharedAuditWithoutDuplicates()
    {
        var first = new TestApp { KeepData = true }; var directory = first.DataDirectory;
        try
        {
            using (first)
            using (var client = Client(first))
            {
                var id = await Complete(client);
                (await Post(client, "certifications", new TrainingCertificationRequest(WorkerId, "SAFETY.PPE", id, true, "synthetic-signoff"), "grant")).EnsureSuccessStatusCode();
            }
            // A saved training intent is authoritative even if the other stores have not retained the projection.
            File.Delete(Path.Combine(directory, "workforce.json")); File.Delete(Path.Combine(directory, "incidents.json"));
            using var restart = new TestApp { DataDirectory = directory }; using var client2 = Client(restart);
            var worker = await restart.Services.GetRequiredService<WorkforceStore>().GetWorkerAsync(WorkerId, CancellationToken.None);
            Assert.Single(worker!.Certifications, cert => cert.EvidenceRef.StartsWith("sha256:", StringComparison.Ordinal));
            await restart.Services.GetRequiredService<TrainingStore>().RecoverAsync(CancellationToken.None);
            Assert.Equal(2, (await restart.Services.GetRequiredService<IncidentStore>().AuditAsync(CancellationToken.None)).Count);
            Assert.True((await restart.Services.GetRequiredService<TrainingStore>().VerifyAsync(CancellationToken.None)).Valid);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("score")] [InlineData("actor")] [InlineData("previousHash")]
    public async Task TamperedTrainingSnapshotOrChainFailsClosedOnRestart(string field)
    {
        var first = new TestApp { KeepData = true }; var directory = first.DataDirectory;
        try
        {
            using (first) using (var client = Client(first)) await Complete(client);
            var path = Path.Combine(directory, "training.json"); var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            if (field == "score") snapshot["completions"]![0]!["record"]!["score"] = 0;
            else snapshot["audit"]![0]![field] = "tampered";
            await File.WriteAllTextAsync(path, snapshot.ToJsonString());
            // Exercise the real startup service directly. WebApplicationFactory's
            // deferred host may dispose its provider before surfacing a failed
            // IHostedService.StartAsync, masking the integrity exception with an
            // ObjectDisposedException. A fresh store still has to reject the
            // exact integrity failure, without repairing or overwriting evidence.
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["CIVICOPS_DATA_DIR"] = directory, ["CIVICOPS_STORAGE"] = "file", ["CIVICOPS_DEMO_MODE"] = "true" }).Build();
            var environment = new SyntheticEnvironment();
            using var incidents = new IncidentStore(configuration, environment);
            using var workforce = new WorkforceStore(configuration, environment, WorkOrderCatalog.Default, TimeProvider.System);
            using var training = new TrainingStore(configuration, environment, WorkOrderCatalog.Default, workforce,
                new TrainingAccess(configuration), incidents, new(), TimeProvider.System);
            var recovery = new TrainingRecoveryService(training);
            var evidence = await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => recovery.StartAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => training.AuditAsync(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() => training.QualificationsAsync(WorkerId, CancellationToken.None));
            Assert.Equal(evidence, await File.ReadAllBytesAsync(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task TrainingAndDispatcherStreamsPublishNamedEventsWithoutOperationalData(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var training = Client(app); using var dispatcher = app.CreateClient();
        // Each HTTP handshake/write owns its startup budget; an expected SSE line
        // retains the original ten-second deadline after the stream is active.
        training.Timeout = dispatcher.Timeout = TimeSpan.FromSeconds(30);
        using var lifetime = new CancellationTokenSource();
        try
        {
            using var trainResponse = await training.GetAsync(Prefix + "stream", HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            using var dispatchResponse = await dispatcher.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            using var trainReader = new StreamReader(await trainResponse.Content.ReadAsStreamAsync(lifetime.Token));
            using var dispatchReader = new StreamReader(await dispatchResponse.Content.ReadAsStreamAsync(lifetime.Token));
            async Task<string?> ReadLineAsync(StreamReader reader) => await reader.ReadLineAsync(lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var reader in new[] { trainReader, dispatchReader })
            {
                Assert.Equal("event: ready", await ReadLineAsync(reader)); Assert.Equal("data: {}", await ReadLineAsync(reader)); await ReadLineAsync(reader);
            }
            var id = await Complete(training);
            // Shared audit writes also produce the usual dispatcher refresh; training stream filters them.
            Assert.Equal("event: training.completed", await ReadLineAsync(trainReader));
            Assert.Equal("data: {}", await ReadLineAsync(trainReader)); await ReadLineAsync(trainReader);
            var found = false;
            for (var i = 0; i < 3; i++)
            {
                var name = await ReadLineAsync(dispatchReader);
                Assert.Equal("data: {}", await ReadLineAsync(dispatchReader)); await ReadLineAsync(dispatchReader);
                if (name == "event: training.completed") { found = true; break; }
            }
            Assert.True(found);
            (await Post(training, "certifications", new TrainingCertificationRequest(WorkerId, "SAFETY.PPE", id, true, "synthetic-signoff"), "grant")).EnsureSuccessStatusCode();
            Assert.Equal("event: certification.granted", await ReadLineAsync(trainReader)); Assert.Equal("data: {}", await ReadLineAsync(trainReader));
        }
        finally { lifetime.Cancel(); }
    }
}

// Generic contract check against the checked-in schemas. It covers every keyword used by these files,
// including cross-file references, and does not duplicate API validation or catalogue membership rules.
internal static class SchemaContract
{
    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs", "schemas"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("docs/schemas not found.");
        }
    }
    public static void Validate(JsonElement data, string schemaFile) => Check(data,
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "docs/schemas", schemaFile))).RootElement, schemaFile);
    private static void Check(JsonElement data, JsonElement schema, string schemaFile)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var parts = reference.GetString()!.Split('#', 2); var file = parts[0].Length == 0 ? schemaFile : parts[0];
            var target = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "docs/schemas", file))).RootElement;
            if (parts.Length == 2) foreach (var part in parts[1].Split('/', StringSplitOptions.RemoveEmptyEntries)) target = target.GetProperty(part);
            Check(data, target, file); return;
        }
        foreach (var keyword in new[] { "anyOf", "oneOf" })
            if (schema.TryGetProperty(keyword, out var choices))
            {
                var matches = 0;
                foreach (var choice in choices.EnumerateArray())
                {
                    try { Check(data, choice, schemaFile); matches++; }
                    catch (Xunit.Sdk.XunitException) { }
                }
                Assert.True(keyword == "anyOf" ? matches > 0 : matches == 1, $"No valid {keyword} contract for {data.ValueKind}.");
            }
        if (schema.TryGetProperty("const", out var fixedValue)) Assert.Equal(fixedValue.GetRawText(), data.GetRawText());
        if (schema.TryGetProperty("enum", out var values)) Assert.Contains(values.EnumerateArray(), value => value.GetRawText() == data.GetRawText());
        if (!schema.TryGetProperty("type", out var type)) return;
        switch (type.GetString())
        {
            case "object":
                Assert.Equal(JsonValueKind.Object, data.ValueKind);
                if (schema.TryGetProperty("required", out var required)) foreach (var name in required.EnumerateArray()) Assert.True(data.TryGetProperty(name.GetString()!, out _), "Missing " + name);
                var properties = schema.GetProperty("properties");
                foreach (var property in data.EnumerateObject())
                {
                    if (properties.TryGetProperty(property.Name, out var child)) Check(property.Value, child, schemaFile);
                    else Assert.True(schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.True, "Unexpected " + property.Name);
                }
                break;
            case "array":
                Assert.Equal(JsonValueKind.Array, data.ValueKind);
                if (schema.TryGetProperty("minItems", out var minItems)) Assert.True(data.GetArrayLength() >= minItems.GetInt32());
                if (schema.TryGetProperty("maxItems", out var maxItems)) Assert.True(data.GetArrayLength() <= maxItems.GetInt32());
                if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean()) Assert.Equal(data.GetArrayLength(), data.EnumerateArray().Select(value => value.GetRawText()).Distinct().Count());
                var tupleCount = 0;
                if (schema.TryGetProperty("prefixItems", out var tuple))
                {
                    tupleCount = tuple.GetArrayLength();
                    for (var index = 0; index < Math.Min(data.GetArrayLength(), tupleCount); index++) Check(data[index], tuple[index], schemaFile);
                }
                if (schema.TryGetProperty("items", out var items))
                    foreach (var item in data.EnumerateArray().Skip(tupleCount))
                    {
                        Assert.False(items.ValueKind == JsonValueKind.False, "Unexpected additional tuple coordinate.");
                        if (items.ValueKind != JsonValueKind.True) Check(item, items, schemaFile);
                    }
                break;
            case "string":
                Assert.Equal(JsonValueKind.String, data.ValueKind); var text = data.GetString()!;
                if (schema.TryGetProperty("minLength", out var minLength)) Assert.True(text.Length >= minLength.GetInt32());
                if (schema.TryGetProperty("maxLength", out var maxLength)) Assert.True(text.Length <= maxLength.GetInt32());
                if (schema.TryGetProperty("pattern", out var pattern)) Assert.Matches(new Regex(pattern.GetString()!), text);
                if (schema.TryGetProperty("format", out var format) && format.GetString() == "date-time") Assert.True(DateTimeOffset.TryParse(text, out _));
                break;
            case "integer": case "number":
                Assert.Equal(JsonValueKind.Number, data.ValueKind); var number = data.GetDouble();
                if (type.GetString() == "integer") Assert.Equal(Math.Truncate(number), number);
                if (schema.TryGetProperty("minimum", out var minimum)) Assert.True(number >= minimum.GetDouble());
                if (schema.TryGetProperty("maximum", out var maximum)) Assert.True(number <= maximum.GetDouble());
                break;
            case "boolean": Assert.True(data.ValueKind is JsonValueKind.True or JsonValueKind.False); break;
            case "null": Assert.Equal(JsonValueKind.Null, data.ValueKind); break;
            default: throw new InvalidDataException("Unsupported schema keyword/type.");
        }
    }
}
