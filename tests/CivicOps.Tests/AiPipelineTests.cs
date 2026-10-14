using System.Net;
using System.Text;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;

namespace CivicOps.Tests;

public sealed class AiPipelineTests
{
    private static readonly JsonElement GoodTriage = AgentSchemas.Element(new
    {
        workOrderTypeCode = "ROADS.POTHOLE_REPAIR", category = "Roads", department = "Roads and Stormwater", priority = "Medium", confidence = .95,
        rationale = "Potholes need roads maintenance."
    });
    private static JsonElement Fallback() => GoodTriage.Clone();
    private static AiReasoningClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle,
        AiOptions? options = null, TimeProvider? time = null) => new(new HttpClient(new FakeHandler(handle)),
            options ?? new AiOptions { BaseUrl = "http://localhost:8000", Model = "synthetic-model", ProviderLabel = "test AMD endpoint" },
            time ?? TimeProvider.System);
    private static Task<HttpResponseMessage> Envelope(object output) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = JsonSerializer.Serialize(output) }, finish_reason = "stop" } }
        }), Encoding.UTF8, "application/json")
    });

    [Fact]
    public async Task ValidTriageUsesStructuredRequestAndRecordsProvenance()
    {
        string? captured = null;
        var client = Client(async (request, _) =>
        {
            Assert.Equal("http://localhost:8000/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-llm-secret", request.Headers.Authorization.Parameter);
            captured = await request.Content.ReadAsStringAsync(_);
            return await Envelope(GoodTriage);
        }, new AiOptions { BaseUrl = "http://localhost:8000/v1", ApiKey = "test-llm-secret", Model = "test-model", ProviderLabel = "test AMD endpoint" });
        var result = await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.False(result.Run.FallbackUsed);
        Assert.Equal("test AMD endpoint", result.Run.Provider);
        Assert.Equal("test-model", result.Run.Model);
        Assert.Equal(64, result.Run.InputHash.Length);
        Assert.Equal("http://localhost:8000", result.Run.EndpointLabel);
        Assert.DoesNotContain("test-llm-secret", result.Run.Output);
        Assert.Equal(.95, result.Run.Confidence);
        using var body = JsonDocument.Parse(captured!);
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.True(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.False(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(0, body.RootElement.GetProperty("temperature").GetInt32());
    }

    [Theory]
    [InlineData("http://localhost:8000", "http://localhost:8000/v1/chat/completions")]
    [InlineData("http://localhost:8000/", "http://localhost:8000/v1/chat/completions")]
    [InlineData("https://example.invalid/v1", "https://example.invalid/v1/chat/completions")]
    [InlineData("https://example.invalid/proxy/v1/", "https://example.invalid/proxy/v1/chat/completions")]
    [InlineData("https://example.invalid/proxy", "https://example.invalid/proxy/v1/chat/completions")]
    public void EndpointPathNormalization(string value, string expected) => Assert.Equal(expected, new AiOptions { BaseUrl = value }.CompletionUri!.ToString());

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")]
    [InlineData("file:///synthetic")]
    [InlineData("https://test-user:test-secret@example.invalid")]
    [InlineData("https://example.invalid?token=test-secret")]
    [InlineData("https://example.invalid/#fragment")]
    [InlineData("invalid")]
    public async Task MissingOrUnsafeEndpointFallsBackWithoutHttp(string? endpoint)
    {
        var client = Client((_, _) => throw new InvalidOperationException("Must not send"), new AiOptions { BaseUrl = endpoint });
        var decision = await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.True(decision.Run.FallbackUsed);
        Assert.Equal("deterministic-fallback", decision.Run.Provider);
        Assert.Equal("offline", decision.Run.EndpointLabel);
        Assert.Equal(0, client.GetStatus().HttpRequestCount);
        Assert.Equal("simulated", client.GetStatus().Mode);
    }

    [Theory]
    [InlineData("not-json", "invalid_json")]
    [InlineData("[]", "invalid_response_shape")]
    [InlineData("{}", "invalid_response_shape")]
    [InlineData("{\"choices\":[]}", "invalid_response_shape")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null}}]}", "invalid_response_shape")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":7}]}", "invalid_response_shape")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"not-json\"}}]}", "invalid_json")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}", "schema_validation_failed")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"[]\"}}]}", "schema_validation_failed")]
    public async Task InvalidProviderOutputFallsBack(string body, string expectedError)
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        var result = await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.True(result.Run.FallbackUsed);
        Assert.Equal(expectedError, result.Run.Error);
        Assert.Equal("Roads", result.Output.GetProperty("category").GetString());
    }

    [Theory]
    [InlineData("category", "Bogus")]
    [InlineData("department", "Waste Management")]
    [InlineData("priority", "Critical")]
    [InlineData("confidence", "high")]
    [InlineData("rationale", "")]
    public async Task TriageRejectsEnumMismatchAndWrongTypes(string field, string invalid)
    {
        var payload = new Dictionary<string, object>
        {
            ["workOrderTypeCode"] = "ROADS.POTHOLE_REPAIR", ["category"] = "Roads", ["department"] = "Roads and Stormwater", ["priority"] = "Medium", ["confidence"] = .95,
            ["rationale"] = "Synthetic roads issue."
        };
        payload[field] = invalid;
        var result = await Client((_, _) => Envelope(payload)).DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.True(result.Run.FallbackUsed);
        Assert.Equal("schema_validation_failed", result.Run.Error);
    }

    [Theory]
    [InlineData(-.01)] [InlineData(1.01)]
    public async Task OutOfRangeConfidenceFallsBack(double confidence)
    {
        var result = await Client((_, _) => Envelope(new { workOrderTypeCode = "ROADS.POTHOLE_REPAIR", category = "Roads", department = "Roads and Stormwater", priority = "Medium", confidence, rationale = "Synthetic." }))
            .DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.True(result.Run.FallbackUsed);
    }

    [Fact]
    public async Task UnexpectedPropertiesAndDuplicateJsonKeysAreRejected()
    {
        var extra = AgentSchemas.Element(new { workOrderTypeCode = "ROADS.POTHOLE_REPAIR", category = "Roads", department = "Roads and Stormwater", priority = "Medium", confidence = .95, rationale = "Synthetic.", assignedCrew = "roads-01" });
        Assert.False(AgentSchemas.Triage.Validate(extra));
        using var duplicate = JsonDocument.Parse("{\"category\":\"Roads\",\"category\":\"Roads\",\"department\":\"Roads and Stormwater\",\"priority\":\"Medium\",\"confidence\":0.9,\"rationale\":\"Synthetic.\"}");
        Assert.False(AgentSchemas.Triage.Validate(duplicate.RootElement));
        var result = await Client((_, _) => Envelope(extra)).DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.True(result.Run.FallbackUsed);
    }

    [Fact]
    public async Task TransientFailureRetriesWithinBudget()
    {
        var attempts = 0;
        var client = Client((_, _) => ++attempts == 1
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) : Envelope(GoodTriage));
        var result = await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.False(result.Run.FallbackUsed);
        Assert.Equal(2, attempts);
        Assert.Equal(2, client.GetStatus().HttpRequestCount);
        Assert.Equal(1, client.GetStatus().CallCount);
    }

    [Theory]
    [InlineData(429, 2, "provider_unavailable")]
    [InlineData(500, 2, "provider_unavailable")]
    [InlineData(503, 2, "provider_unavailable")]
    [InlineData(400, 1, "provider_rejected_request")]
    [InlineData(401, 1, "provider_rejected_request")]
    public async Task HttpErrorsHaveBoundedRetries(int status, int expectedAttempts, string error)
    {
        var attempts = 0;
        var client = Client((_, _) => { attempts++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)); });
        var result = await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.True(result.Run.FallbackUsed);
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(error, result.Run.Error);
    }

    [Fact]
    public async Task TimeoutFallsBackAndCallerCancellationPropagates()
    {
        var client = Client(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return await Envelope(GoodTriage); },
            new AiOptions { BaseUrl = "http://localhost:8000", TimeoutMs = 250 });
        var result = await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.True(result.Run.FallbackUsed);
        Assert.Equal("timeout", result.Run.Error);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback, cancellation.Token));
    }

    [Fact]
    public async Task OversizedAndTruncatedResponsesAreRejected()
    {
        var large = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65_537)) }));
        var result = await large.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.Equal("response_too_large", result.Run.Error);
        var truncated = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = GoodTriage.GetRawText() }, finish_reason = "length" } } })) }));
        result = await truncated.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.Equal("truncated_output", result.Run.Error);
    }

    [Fact]
    public async Task MetricsAndInputHashesAreStableAndBounded()
    {
        var client = Client((_, _) => throw new InvalidOperationException(), new AiOptions());
        var first = await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        var second = await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.Equal(first.Run.InputHash, second.Run.InputHash);
        for (var count = 0; count < 513; count++) await client.DecideAsync(AgentSchemas.Triage, new { description = count }, Fallback);
        var status = client.GetStatus();
        Assert.Equal(515, status.CallCount);
        Assert.Equal(515, status.FallbackCount);
        Assert.Equal(512, status.LatencyWindowSize);
        Assert.Equal(1, status.FallbackRate);
        Assert.True(status.P50LatencyMs <= status.P95LatencyMs);
    }

    [Fact]
    public async Task DeterministicPipelineRecordsFiveAgentsAndNeverAssignsOrSends()
    {
        var incident = SyntheticIncident("Pothole on a synthetic road");
        await Pipeline().EnrichAsync(incident);
        Assert.Equal(new[] { "IntakeAgent", "TriageAgent", "DuplicateAgent", "DispatchAgent", "CommsAgent" }, incident.AgentRuns.Select(run => run.AgentName));
        Assert.All(incident.AgentRuns, run => Assert.True(run.FallbackUsed));
        Assert.Equal("roads-01", incident.RecommendedCrewId);
        Assert.Null(incident.AssignedCrewId);
        Assert.Null(incident.Approval);
        Assert.True(incident.NeedsHumanReview);
        Assert.Equal("PendingReview", Assert.Single(incident.CommsDrafts).State);
        Assert.DoesNotContain(incident.AgentRuns.SelectMany(run => run.ToolPermissions), permission => permission is "assign:crew" or "send:email");
    }

    [Theory]
    [InlineData("Synthetic fire near a road")]
    [InlineData("Synthetic resident trapped by a gate")]
    [InlineData("Synthetic live wire on a pavement")]
    [InlineData("Synthetic flooding home after a storm")]
    [InlineData("Synthetic flooding inside a home")]
    [InlineData("Synthetic flooding my house")]
    [InlineData("Synthetic electrocution hazard")]
    public async Task DangerAlwaysFloorsPriority(string description)
    {
        var incident = SyntheticIncident(description);
        await Pipeline().EnrichAsync(incident);
        Assert.Equal("Urgent", incident.Priority);
        Assert.NotEmpty(incident.Hazards);
        Assert.True(incident.NeedsHumanReview);
    }

    [Fact]
    public async Task ModelDisagreementLowConfidenceAndDangerFloorRequireReview()
    {
        var incident = SyntheticIncident("Synthetic live wire over the road");
        incident.NeedsHumanReview = false;
        await Pipeline(Client(RespondToAgent)).EnrichAsync(incident);
        Assert.Equal("Electricity", incident.Category);
        Assert.Equal("ELEC.LIVE_WIRE_DOWN", incident.WorkOrderTypeCode);
        Assert.Equal("Urgent", incident.Priority);
        Assert.True(incident.NeedsHumanReview);
        Assert.Contains(incident.AgentRuns, run => run.AgentName == "TriageSafetyFloor");
        Assert.Null(incident.AssignedCrewId);
    }

    [Fact]
    public async Task ModelLowConfidenceRequiresReviewEvenWhenClassificationAgrees()
    {
        var incident = SyntheticIncident("Synthetic water leak at a pipe");
        incident.NeedsHumanReview = false;
        await Pipeline(Client(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString() == "TriageAgent")
                return await Envelope(new { workOrderTypeCode = "WATER.BURST_MAIN", category = "Water", department = "Water and Sanitation", priority = "Medium", confidence = .7, rationale = "Synthetic uncertainty." });
            return await RespondToAgent(request, token);
        })).EnrichAsync(incident);
        Assert.True(incident.NeedsHumanReview);
        Assert.Equal("Medium", incident.Priority);
        Assert.False(incident.AgentRuns.Single(run => run.AgentName == "TriageAgent").FallbackUsed);
    }

    [Fact]
    public async Task ModelDisagreementRequiresReviewEvenWhenConfidenceIsHigh()
    {
        var incident = SyntheticIncident("Synthetic pothole in the road");
        incident.NeedsHumanReview = false;
        await Pipeline(Client(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString() == "TriageAgent")
                return await Envelope(new { workOrderTypeCode = "WATER.BURST_MAIN", category = "Water", department = "Water and Sanitation", priority = "Medium", confidence = .99, rationale = "Synthetic conflicting recommendation." });
            return await RespondToAgent(request, token);
        })).EnrichAsync(incident);
        Assert.True(incident.NeedsHumanReview);
        Assert.Equal("Water", incident.Category);
        Assert.False(incident.AgentRuns.Single(run => run.AgentName == "TriageAgent").FallbackUsed);
    }

    [Fact]
    public async Task ModelCannotGrantContactConsentOrReplaceResidentLocation()
    {
        var incident = SyntheticIncident("Synthetic water leak");
        await Pipeline(Client(RespondToAgent)).EnrichAsync(incident);
        Assert.Equal("Synthetic Ward", incident.Area);
        Assert.False(incident.ConsentToContact);
        Assert.False(incident.CallbackConsent);
    }

    [Fact]
    public async Task ProviderExceptionNeverLeaksItsMessageIntoRunOrStatus()
    {
        var client = Client((_, _) => throw new HttpRequestException("test-llm-secret must not appear"));
        var result = await client.DecideAsync(AgentSchemas.Triage, new { description = "Synthetic pothole" }, Fallback);
        Assert.Equal("http_request_failed", result.Run.Error);
        Assert.DoesNotContain("test-llm-secret", JsonSerializer.Serialize(result.Run));
        Assert.DoesNotContain("test-llm-secret", JsonSerializer.Serialize(client.GetStatus()));
    }

    [Fact]
    public async Task EligibleCrewValidationRejectsCrossDepartmentRecommendations()
    {
        var incident = SyntheticIncident("Synthetic pothole on the road");
        await Pipeline(Client(async (request, token) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var name = document.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString();
            if (name == "DispatchAgent") return await Envelope(new { crewId = "water-01", confidence = .9, rationale = "Incorrect department." });
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        })).EnrichAsync(incident);
        Assert.Equal("roads-01", incident.RecommendedCrewId);
        var run = incident.AgentRuns.Single(run => run.AgentName == "DispatchAgent");
        Assert.True(run.FallbackUsed);
        Assert.Equal("schema_validation_failed", run.Error);
    }

    [Fact]
    public async Task Open311SignalSurvivesFallbackAndConflictRequiresReview()
    {
        var incident = SyntheticIncident("Synthetic unclear issue");
        incident.ServiceCode = "roads";
        incident.Category = "Roads";
        incident.Department = "Roads and Stormwater";
        incident.NeedsHumanReview = false;
        await Pipeline().EnrichAsync(incident);
        Assert.Equal("Roads", incident.Category);
        Assert.Equal("Roads and Stormwater", incident.Department);
        Assert.True(incident.NeedsHumanReview);
    }

    [Fact]
    public async Task DuplicateIsRecommendedForReviewAndNeverMerged()
    {
        var incident = SyntheticIncident("Synthetic pothole on the road");
        incident.DuplicateOf = "CIV-SYNTHETIC-ORIGINAL";
        incident.DuplicateConfidence = .8;
        await Pipeline().EnrichAsync(incident);
        Assert.Equal("CIV-SYNTHETIC-ORIGINAL", incident.DuplicateOf);
        Assert.Contains(incident.AgentRuns, run => run.AgentName == "DuplicateAgent");
        Assert.True(incident.NeedsHumanReview);
        Assert.NotEqual(incident.ReferenceNumber, incident.DuplicateOf);
    }

    [Fact]
    public async Task DuplicateConfirmationReceivesBothReportsAfterTriage()
    {
        var original = SyntheticIncident("Synthetic pothole on the same road surface");
        original.ReferenceNumber = "CIV-SYNTHETIC-ORIGINAL";
        original.Department = "Roads and Stormwater";
        original.NormalizedArea = "Synthetic Ward";
        var incident = SyntheticIncident("Synthetic pothole on the same road surface");
        incident.NormalizedArea = "Synthetic Ward";
        string? duplicateInput = null;
        var client = Client(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString() == "DuplicateAgent")
            {
                duplicateInput = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
                return await Envelope(new { isDuplicate = true, confidence = .95, rationale = "Both synthetic reports describe the same road issue." });
            }
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });
        await Pipeline(client).EnrichAsync(incident, [original]);
        using var captured = JsonDocument.Parse(duplicateInput!);
        Assert.Equal(original.Description, captured.RootElement.GetProperty("candidateDescription").GetString());
        Assert.Equal(original.ReferenceNumber, incident.DuplicateOf);
        Assert.False(incident.AgentRuns.Single(run => run.AgentName == "DuplicateAgent").FallbackUsed);
        Assert.Equal(IncidentStatus.Triaged, original.Status);
    }

    [Theory]
    [InlineData("en", "Reference")]
    [InlineData("af", "Verwysing")]
    [InlineData("afr", "Verwysing")]
    [InlineData("unsupported", "Reference")]
    public async Task CommsUsesLanguageTemplateAndNeverIncludesContacts(string language, string expected)
    {
        var incident = SyntheticIncident("Synthetic pothole on the road");
        incident.ResidentLanguage = language;
        incident.ContactEmail = "synthetic@example.invalid";
        incident.RawTranscript = "Synthetic confidential transcript.";
        await Pipeline().DraftCommsAsync(incident);
        var draft = Assert.Single(incident.CommsDrafts);
        Assert.Contains(expected, draft.Text);
        Assert.Contains(incident.ReferenceNumber, draft.Text);
        Assert.DoesNotContain(incident.ContactEmail, draft.Text);
        Assert.DoesNotContain(incident.RawTranscript, draft.Text);
    }

    [Theory]
    [InlineData("Your team will arrive in 20 minutes.")]
    [InlineData("We guarantee arrival tomorrow.")]
    [InlineData("Ons span sal aankom oor 2 uur.")]
    public void CommsRejectsPromisedEtas(string text)
    {
        Assert.False(AgentSchemas.Comms("en", "CIV-SYNTHETIC").Validate(AgentSchemas.Element(new
        { language = "en", text = "CIV-SYNTHETIC " + text, confidence = .9, rationale = "Synthetic." })));
    }

    [Theory]
    [InlineData(IncidentStatus.New)] [InlineData(IncidentStatus.Triaged)] [InlineData(IncidentStatus.PendingApproval)]
    [InlineData(IncidentStatus.Approved)] [InlineData(IncidentStatus.Assigned)] [InlineData(IncidentStatus.InProgress)]
    [InlineData(IncidentStatus.WaitingForCitizen)] [InlineData(IncidentStatus.Resolved)] [InlineData(IncidentStatus.Closed)]
    public async Task EveryWorkflowStatusHasCommsDraft(IncidentStatus status)
    {
        var incident = SyntheticIncident("Synthetic issue");
        incident.Status = status;
        await Pipeline().DraftCommsAsync(incident);
        Assert.Equal(status.ToString(), Assert.Single(incident.CommsDrafts).Status);
        Assert.Equal("PendingReview", incident.CommsDrafts[0].State);
    }

    [Fact]
    public void OptionsReadBlankConfigurationWithoutSecrets()
    {
        var options = AiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CIVICOPS_LLM_BASE_URL"] = " ", ["CIVICOPS_LLM_MODEL"] = " ", ["CIVICOPS_LLM_TIMEOUT_MS"] = "1" }).Build());
        Assert.Null(options.CompletionUri);
        Assert.Equal("Qwen/Qwen3-8B", options.Model);
        Assert.Equal(250, options.TimeoutMs);
        Assert.Null(options.ApiKey);
    }

    private static Incident SyntheticIncident(string description) => new()
    { Description = description, Area = "Synthetic Ward", ReferenceNumber = "CIV-SYNTHETIC-0001" };
    private static AgentPipeline Pipeline(AiReasoningClient? client = null) => new(client ?? Client((_, _) => throw new InvalidOperationException(), new AiOptions()),
        new DeterministicClassifier(), TimeProvider.System);
    private static async Task<HttpResponseMessage> RespondToAgent(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var name = body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString();
        return name switch
        {
            "IntakeAgent" => await Envelope(new { what = "Synthetic issue", where = "Invented ward", hazards = Array.Empty<string>(), language = "en", contactPreference = "none", confidence = .9, rationale = "Synthetic extraction." }),
            "TriageAgent" => await Envelope(new { workOrderTypeCode = "WATER.BURST_MAIN", category = "Water", department = "Water and Sanitation", priority = "Low", confidence = .4, rationale = "Synthetic ambiguous issue." }),
            "DuplicateAgent" => await Envelope(new { isDuplicate = false, confidence = .9, rationale = "No candidate." }),
            "DispatchAgent" => await Envelope(new { crewId = "water-01", confidence = .9, rationale = "Water department match." }),
            "CommsAgent" => await Envelope(new { language = "en", text = "Reference CIV-SYNTHETIC-0001. Your report awaits human review.", confidence = .9, rationale = "Synthetic status draft." }),
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        };
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
}
