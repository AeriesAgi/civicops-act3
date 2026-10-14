using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CivicOps.Models;

namespace CivicOps.Services;

/// <summary>Raw Agents API transport. Missing credentials use a deterministic simulation without HTTP.</summary>
public sealed class ElevenLabsClient(HttpClient http, ElevenLabsOptions options, TimeProvider? clock = null)
{
    private readonly ProviderCircuit _circuit = new(clock ?? TimeProvider.System);
    public ProviderHealth Health => _circuit.Describe(!options.Simulated);
    public bool Simulated => options.Simulated;
    public object GetStatus() => ElevenLabsEndpoints.Status(options);
    public Task<JsonObject> CreateAgentAsync(JsonObject body, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/v1/convai/agents/create", body, ct);
    public Task<JsonObject> UpdateAgentAsync(string id, JsonObject body, CancellationToken ct = default) => SendAsync(HttpMethod.Patch, $"/v1/convai/agents/{Escape(id)}", body, ct);
    public Task<JsonObject> GetAgentAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"/v1/convai/agents/{Escape(id)}", null, ct);
    public Task<JsonObject> CreateToolAsync(JsonObject body, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/v1/convai/tools", body, ct);
    public Task<JsonObject> UpdateToolAsync(string id, JsonObject body, CancellationToken ct = default) => SendAsync(HttpMethod.Patch, $"/v1/convai/tools/{Escape(id)}", body, ct);
    public Task<JsonObject> GetToolAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"/v1/convai/tools/{Escape(id)}", null, ct);
    public Task<JsonObject> GetKnowledgeAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"/v1/convai/knowledge-base/{Escape(id)}", null, ct);
    public async Task<string> GetKnowledgeContentAsync(string id, CancellationToken ct = default)
    {
        if (Simulated) return "Synthetic knowledge only.";
        var bytes = await SendContentAsync(HttpMethod.Get, $"/v1/convai/knowledge-base/{Escape(id)}/content", null, ct);
        // The endpoint streams document text, rather than the JSON object used by the other APIs.
        return Encoding.UTF8.GetString(bytes);
    }
    public Task<JsonObject> ListToolsAsync(string? search = null, string? cursor = null, CancellationToken ct = default) => SendAsync(HttpMethod.Get, ListPath("tools", search, cursor), null, ct);
    public Task<JsonObject> ListAgentsAsync(string? search = null, string? cursor = null, CancellationToken ct = default) => SendAsync(HttpMethod.Get, ListPath("agents", search, cursor), null, ct);
    public Task<JsonObject> ListKnowledgeAsync(string? search = null, string? cursor = null, CancellationToken ct = default) => SendAsync(HttpMethod.Get, ListPath("knowledge-base", search, cursor), null, ct);
    public Task<JsonObject> CreateKnowledgeTextAsync(string name, string text, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/v1/convai/knowledge-base/text", new() { ["name"] = name, ["text"] = text }, ct);
    public Task<JsonObject> GetSignedUrlAsync(string agentId, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"/v1/convai/conversation/get-signed-url?agent_id={Escape(agentId)}", null, ct);
    public Task<JsonObject> GetConversationTokenAsync(string agentId, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"/v1/convai/conversation/token?agent_id={Escape(agentId)}", null, ct);
    public Task<JsonObject> GetConversationAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"/v1/convai/conversations/{Escape(id)}", null, ct);
    public Task<JsonObject> OutboundCallAsync(string agentId, string phoneNumberId, string toNumber,
        JsonObject dynamicVariables, string provider = "twilio", CancellationToken ct = default)
    {
        if (provider is not ("twilio" or "sip-trunk")) throw new ArgumentException("Phone provider must be twilio or sip-trunk.", nameof(provider));
        return SendAsync(HttpMethod.Post, $"/v1/convai/{provider}/outbound-call", new()
        {
            ["agent_id"] = agentId, ["agent_phone_number_id"] = phoneNumberId, ["to_number"] = toNumber,
            ["conversation_initiation_client_data"] = new JsonObject { ["dynamic_variables"] = dynamicVariables.DeepClone() }
        }, ct);
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        if (Simulated) return Simulate(method, path, body);
        return JsonNode.Parse(await SendContentAsync(method, path, body, ct)) as JsonObject
            ?? throw new InvalidDataException("ElevenLabs response was not a JSON object.");
    }

    private async Task<byte[]> SendContentAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
        => await _circuit.ExecuteAsync(token => SendContentCoreAsync(method, path, body, token), options.RequestTimeout, ct);

    private async Task<byte[]> SendContentCoreAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var activity = CivicOpsTelemetry.Activities.StartActivity("provider.elevenlabs", System.Diagnostics.ActivityKind.Client);
        activity?.SetTag("http.request.method", method.Method);
        var baseUri = ValidateApiBase(options.ApiBase);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Add("xi-api-key", options.ApiKey);
        CivicOpsTelemetry.Propagate(request);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.RequestTimeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        // Provider response bodies may contain resident data. Do not include them in logs or errors.
        if (!response.IsSuccessStatusCode)
        {
            // Read a small prefix only to classify a credit/quota rejection; the body itself is never logged or returned.
            var prefix = "";
            try { using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)); var chars = new char[2048]; prefix = new string(chars, 0, await reader.ReadBlockAsync(chars, timeout.Token)); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException) { }
            if ((int)response.StatusCode is 402 or 429 || prefix.Contains("quota", StringComparison.OrdinalIgnoreCase))
                throw new ElevenLabsQuotaException(response.StatusCode);
            throw new HttpRequestException($"ElevenLabs returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
        const int maximumResponse = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximumResponse) throw new InvalidDataException("ElevenLabs response exceeded its size budget.");
        await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await responseStream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (bounded.Length + count > maximumResponse) throw new InvalidDataException("ElevenLabs response exceeded its size budget.");
            bounded.Write(buffer, 0, count);
        }
        return bounded.ToArray();
    }

    public static Uri ValidateApiBase(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("ElevenLabs API base must be HTTP(S) without embedded credentials, a query or a fragment.");
        return new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static JsonObject Simulate(HttpMethod method, string path, JsonObject? body)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + path + body?.ToJsonString())))[..16].ToLowerInvariant();
        if (path.Contains("get-signed-url", StringComparison.Ordinal)) return new() { ["mode"] = "simulated", ["signed_url"] = $"wss://example.invalid/simulated/{suffix}" };
        if (path.Contains("/conversation/token", StringComparison.Ordinal)) return new() { ["mode"] = "simulated", ["token"] = "simulated-session", ["conversation_id"] = $"simulated-{suffix}" };
        if (path.Contains("outbound-call", StringComparison.Ordinal)) return new() { ["mode"] = "simulated", ["success"] = true, ["message"] = "Simulated callback; no call placed.", ["conversation_id"] = $"simulated-{suffix}" };
        if (method == HttpMethod.Get && path.Contains("page_size=", StringComparison.Ordinal))
            return new() { [path.Contains("knowledge-base", StringComparison.Ordinal) ? "documents" : path.Contains("agents", StringComparison.Ordinal) ? "agents" : "tools"] = new JsonArray(), ["has_more"] = false };
        return new() { ["mode"] = "simulated", ["agent_id"] = $"simulated-agent-{suffix}", ["id"] = $"simulated-{suffix}" };
    }
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string ListPath(string resource, string? search, string? cursor) =>
        $"/v1/convai/{resource}?page_size=100" + (search is null ? "" : $"&search={Escape(search)}") + (cursor is null ? "" : $"&cursor={Escape(cursor)}");
}

/// <summary>ElevenLabs rejected the request because the workspace or key credit quota is exhausted.</summary>
public sealed class ElevenLabsQuotaException(System.Net.HttpStatusCode status)
    : HttpRequestException($"ElevenLabs quota exhausted (HTTP {(int)status}).", null, status);
