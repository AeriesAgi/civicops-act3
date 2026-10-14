using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed record AgentDefinition(
    string Name,
    string SystemPrompt,
    JsonElement Schema,
    IReadOnlyList<string> ToolPermissions,
    Func<JsonElement, bool> Validate);

public sealed record AgentDecision(JsonElement Output, AgentRun Run);

/// <summary>One logical decision has a shared timeout and at most two HTTP attempts. Invalid output falls back.</summary>
public sealed class AiReasoningClient
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentQueue<double> _latencies = new();
    private long _calls;
    private long _httpRequests;
    private long _fallbacks;
    private const int LatencyWindow = 512;
    private readonly ProviderCircuit _circuit;
    private readonly ManagedIdentityTokenSource _identity;
    private readonly AiReasoningClient? _secondary;
    private readonly SpendCaps? _caps;

    public AiReasoningClient(HttpClient http, AiOptions options, TimeProvider time, SpendCaps? caps = null)
    {
        _caps = caps;
        _http = http;
        _identity = new(http, options, time);
        if (options.Secondary is { } secondary) _secondary = new(http, secondary, time);
        _options = options;
        _time = time;
        _circuit = new(time, handlesFailure: error => error is not AiOutputException { Code: "provider_rejected_request" });
    }

    /// <summary>OpenAI/Azure json_schema names must match ^[a-zA-Z0-9_-]{1,64}$ ("IntakeRoom.Coordinator" was rejected with HTTP 400).</summary>
    public static string SchemaName(string agentName)
    {
        var safe = System.Text.RegularExpressions.Regex.Replace(agentName, "[^A-Za-z0-9_-]", "_");
        return safe.Length > 64 ? safe[..64] : safe;
    }

    public static object BuildRequest(AiOptions options, AgentDefinition agent, object input) => options.Provider switch
    {
        "azure-openai" => BuildAzureRequest(options, agent, input),
        "gemini" => BuildGeminiRequest(options, agent, input),
        _ => BuildCompatibleRequest(options, agent, input)
    };

    /// <summary>Gemini OpenAI-compatible endpoint: thinking models need the larger budget; JSON schema is enforced.</summary>
    private static Dictionary<string, object> BuildGeminiRequest(AiOptions options, AgentDefinition agent, object input)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = options.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = agent.SystemPrompt },
                new { role = "user", content = PiiRedactor.Serialize(input) }
            },
            ["temperature"] = 0,
            ["max_tokens"] = Math.Clamp(options.MaxCompletionTokens, 256, 16_000),
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = SchemaName(agent.Name), strict = true, schema = agent.Schema }
            }
        };
        if (options.ReasoningEffort != "none") body["reasoning_effort"] = options.ReasoningEffort;
        return body;
    }

    /// <summary>Azure OpenAI reasoning deployments (gpt-5-mini): no temperature, max_completion_tokens covers hidden reasoning.</summary>
    private static Dictionary<string, object> BuildAzureRequest(AiOptions options, AgentDefinition agent, object input)
    {
        var body = new Dictionary<string, object>
        {
            ["messages"] = new object[]
            {
                new { role = "system", content = agent.SystemPrompt },
                new { role = "user", content = PiiRedactor.Serialize(input) }
            },
            ["max_completion_tokens"] = Math.Clamp(options.MaxCompletionTokens, 256, 16_000),
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new { name = SchemaName(agent.Name), strict = true, schema = agent.Schema }
            }
        };
        if (options.ReasoningEffort != "none") body["reasoning_effort"] = options.ReasoningEffort;
        return body;
    }

    private static object BuildCompatibleRequest(AiOptions options, AgentDefinition agent, object input) => new
    {
        model = options.Model,
        messages = new object[]
        {
            new { role = "system", content = agent.SystemPrompt },
            new { role = "user", content = PiiRedactor.Serialize(input) }
        },
        temperature = 0,
        max_tokens = 800,
        response_format = new
        {
            type = "json_schema",
            json_schema = new { name = SchemaName(agent.Name), strict = true, schema = agent.Schema }
        },
        // Qwen3 otherwise spends much of the small budget on hidden thinking.
        chat_template_kwargs = new { enable_thinking = false }
    };

    public Task<AgentDecision> DecideAsync(AgentDefinition agent, object input,
        Func<JsonElement> deterministicFallback, CancellationToken cancellationToken = default)
        => DecideCoreAsync(agent, input, deterministicFallback, null, cancellationToken);

    /// <summary>Records a labelled deterministic decision without calling a provider (for example after a room timeout).</summary>
    public Task<AgentDecision> DecideOfflineAsync(AgentDefinition agent, object input, Func<JsonElement> deterministicFallback, string reason)
        => DecideCoreAsync(agent, input, deterministicFallback, reason, CancellationToken.None);

    private async Task<AgentDecision> DecideCoreAsync(AgentDefinition agent, object input,
        Func<JsonElement> deterministicFallback, string? forceFallback, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = CivicOpsTelemetry.Activities.StartActivity("agent." + agent.Name);
        activity?.SetTag("agent.name", agent.Name);
        var start = _time.GetTimestamp();
        var inputJson = PiiRedactor.Serialize(input);
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputJson))).ToLowerInvariant();
        JsonElement output;
        string? error;
        // Hard spend cut-off: one logical decision with a configured live lane consumes one unit of the global cap.
        if (forceFallback is null && _caps is not null && (_options.CompletionUri is not null || _secondary?._options.CompletionUri is not null)
            && !_caps.TryConsume(SpendCaps.Llm))
            forceFallback = "llm_cap_reached: Spend cap reached. Simulation mode";
        var (attempt, primaryError) = forceFallback is null ? await TryAsync(agent, input, cancellationToken) : (null, forceFallback);
        var lane = this;
        if (attempt is null && _secondary is not null && forceFallback is null)
        {
            var (second, secondError) = await _secondary.TryAsync(agent, input, cancellationToken);
            var first = $"{_options.Provider}:{primaryError ?? "not_configured"}";
            if (second is not null) { attempt = second; lane = _secondary; error = first + " (secondary used)"; }
            else error = $"{first};{_secondary._options.Provider}:{secondError ?? "not_configured"}";
        }
        else error = primaryError;
        var fallback = attempt is null;
        output = attempt ?? deterministicFallback();
        var uri = lane._options.CompletionUri;

        output = PiiRedactor.RedactJson(output);
        // Fail fast if an internal fallback violates its own contract. This is an implementation error.
        if (!agent.Validate(output)) throw new InvalidOperationException($"{agent.Name} fallback violated its schema.");
        var elapsed = Math.Round(_time.GetElapsedTime(start).TotalMilliseconds, 2);
        Interlocked.Increment(ref _calls);
        if (fallback) Interlocked.Increment(ref _fallbacks);
        CivicOpsTelemetry.AgentDecisions.Add(1, new KeyValuePair<string, object?>("agent", agent.Name));
        if (fallback) CivicOpsTelemetry.AiFallbacks.Add(1, new KeyValuePair<string, object?>("agent", agent.Name));
        activity?.SetTag("agent.fallback", fallback);
        activity?.SetTag("agent.fallback_reason", error);
        _latencies.Enqueue(elapsed);
        while (_latencies.Count > LatencyWindow) _latencies.TryDequeue(out _);
        var confidence = output.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 1;
        var rationale = output.TryGetProperty("rationale", out var reason) ? reason.GetString()! : "Structured extraction.";
        var run = new AgentRun(agent.Name, inputHash,
            fallback ? "deterministic-fallback" : lane._options.ProviderLabel,
            fallback ? "rules-v1" : lane._options.Model,
            fallback ? "offline" : SafeEndpointLabel(uri!),
            elapsed, output.GetRawText(), confidence, rationale, fallback, error,
            _time.GetUtcNow(), agent.ToolPermissions);
        return new AgentDecision(output, run);
    }

    /// <summary>One provider lane: returns validated output, or null plus a bounded failure code.</summary>
    private async Task<(JsonElement? Output, string? Error)> TryAsync(AgentDefinition agent, object input, CancellationToken cancellationToken)
    {
        var uri = _options.CompletionUri;
        if (uri is null) return (null, _options.Configured ? "invalid_endpoint_configuration" : null);
        try
        {
            return (await _circuit.ExecuteAsync(async token =>
            {
                using var provider = CivicOpsTelemetry.Activities.StartActivity("provider.ai", System.Diagnostics.ActivityKind.Client);
                provider?.SetTag("ai.provider", _options.Provider);
                var result = await RequestAsync(agent, input, uri, token);
                if (!agent.Validate(result)) throw new AiOutputException("schema_validation_failed");
                return result;
            }, TimeSpan.FromMilliseconds(Math.Clamp(_options.TimeoutMs, 250, 60_000)), cancellationToken), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (null, "timeout"); }
        catch (AiOutputException exception) { return (null, exception.Code); }
        catch (HttpRequestException exception) { return (null, exception is ProviderCircuitOpenException ? "circuit_open" : "http_request_failed"); }
        catch (ManagedIdentityException) { return (null, "identity_unavailable"); }
        catch (IOException) { return (null, "response_read_failed"); }
        catch (JsonException) { return (null, "invalid_json"); }
    }

    private async Task<JsonElement> RequestAsync(AgentDefinition agent, object input, Uri uri, CancellationToken token)
    {
        var attempts = Math.Clamp(_options.MaxAttempts, 1, 2);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(JsonSerializer.Serialize(BuildRequest(_options, agent, input)), Encoding.UTF8, "application/json")
            };
            CivicOpsTelemetry.Propagate(request);
            if (_options.IsAzure)
            {
                if (_options.AzureAuth == "key")
                {
                    if (string.IsNullOrWhiteSpace(_options.ApiKey)) throw new AiOutputException("missing_credentials");
                    request.Headers.Add("api-key", _options.ApiKey);
                }
                else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _identity.GetTokenAsync(token));
            }
            else if (_options.Provider == "gemini" && string.IsNullOrWhiteSpace(_options.ApiKey)) throw new AiOutputException("missing_credentials");
            else if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            HttpResponseMessage response;
            Interlocked.Increment(ref _httpRequests);
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            }
            catch (HttpRequestException) when (attempt + 1 < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), _time, token);
                continue;
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    if (attempt + 1 < attempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), _time, token);
                        continue;
                    }
                    throw new AiOutputException("provider_unavailable");
                }
                if (response.StatusCode == HttpStatusCode.PaymentRequired) throw new AiOutputException("provider_credits");
                if (!response.IsSuccessStatusCode) throw new AiOutputException("provider_rejected_request");
                var maxBytes = Math.Clamp(_options.MaxResponseBytes, 1024, 65_536);
                if (response.Content.Headers.ContentLength > maxBytes) throw new AiOutputException("response_too_large");
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(chunk, token)) > 0)
                {
                    if (buffer.Length + count > maxBytes) throw new AiOutputException("response_too_large");
                    buffer.Write(chunk, 0, count);
                }
                using var envelope = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
                if (envelope.RootElement.ValueKind != JsonValueKind.Object
                    || !envelope.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() != 1 || choices[0].ValueKind != JsonValueKind.Object
                    || !choices[0].TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                    throw new AiOutputException("invalid_response_shape");
                var json = content.GetString()!;
                if (json.Length > 8192) throw new AiOutputException("output_too_large");
                if (choices[0].TryGetProperty("finish_reason", out var finish))
                {
                    if (finish.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw new AiOutputException("invalid_response_shape");
                    if (finish.ValueKind == JsonValueKind.String && finish.GetString() == "length")
                        throw new AiOutputException("truncated_output");
                }
                using var result = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
                return result.RootElement.Clone();
            }
        }
        throw new AiOutputException("retry_budget_exhausted");
    }

    public AiStatus GetStatus()
    {
        var samples = _latencies.ToArray();
        Array.Sort(samples);
        var calls = Interlocked.Read(ref _calls);
        var fallbacks = Interlocked.Read(ref _fallbacks);
        var configured = _options.CompletionUri;
        var secondary = _secondary?._options.CompletionUri is null ? "" : "+" + _secondary._options.Provider;
        return new AiStatus(configured is null ? "simulated" : (_options.IsAzure ? "live-ready:azure-openai:" + _options.AzureAuth : "live-ready:" + _options.Provider) + secondary,
            configured is null ? "rules-v1" : _options.Model,
            configured is null ? "deterministic-fallback" : _options.ProviderLabel,
            configured is null ? "offline" : SafeEndpointLabel(configured), calls,
            Interlocked.Read(ref _httpRequests), fallbacks,
            Percentile(samples, .50), Percentile(samples, .95), calls == 0 ? 0 : Math.Round((double)fallbacks / calls, 4),
            samples.Length, Math.Clamp(_options.TimeoutMs, 250, 60_000), Math.Clamp(_options.MaxAttempts, 1, 2));
    }
    public ProviderHealth Health => _circuit.Describe(_options.CompletionUri is not null);

    private static double Percentile(double[] values, double fraction) => values.Length == 0
        ? 0 : values[Math.Max(0, (int)Math.Ceiling(values.Length * fraction) - 1)];
    private static string SafeEndpointLabel(Uri uri) => $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    private sealed class AiOutputException(string code) : Exception(code) { public string Code { get; } = code; }
}

public sealed class ManagedIdentityException(string message) : Exception(message);

/// <summary>Minimal Azure managed identity token source (Container Apps/App Service IDENTITY_ENDPOINT protocol), cached until 5 minutes before expiry.</summary>
public sealed class ManagedIdentityTokenSource(HttpClient http, AiOptions options, TimeProvider time)
{
    private string? _token;
    private DateTimeOffset _expires;
    public const string Resource = "https://cognitiveservices.azure.com";

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && time.GetUtcNow() < _expires) return _token;
        try
        {
            if (!Uri.TryCreate(options.IdentityEndpoint, UriKind.Absolute, out var endpoint) || string.IsNullOrWhiteSpace(options.IdentityHeader)
                || !(endpoint.IsLoopback || endpoint.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || System.Net.IPAddress.TryParse(endpoint.Host, out _)))
                throw new ManagedIdentityException("Managed identity endpoint is not available.");
            var query = "api-version=2019-08-01&resource=" + Uri.EscapeDataString(Resource)
                + (string.IsNullOrWhiteSpace(options.AzureClientId) ? "" : "&client_id=" + Uri.EscapeDataString(options.AzureClientId));
            using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(endpoint) { Query = query }.Uri);
            request.Headers.Add("X-IDENTITY-HEADER", options.IdentityHeader);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new ManagedIdentityException("Managed identity token request failed.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct), new JsonDocumentOptions { MaxDepth = 4 });
            if (!document.RootElement.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(access.GetString()))
                throw new ManagedIdentityException("Managed identity token response was invalid.");
            var expiresOn = document.RootElement.TryGetProperty("expires_on", out var exp)
                && long.TryParse(exp.ValueKind == JsonValueKind.Number ? exp.GetRawText() : exp.GetString(), out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds) : time.GetUtcNow().AddMinutes(30);
            _token = access.GetString();
            _expires = expiresOn.AddMinutes(-5);
            return _token!;
        }
        catch (JsonException) { throw new ManagedIdentityException("Managed identity token response was invalid."); }
    }
}
