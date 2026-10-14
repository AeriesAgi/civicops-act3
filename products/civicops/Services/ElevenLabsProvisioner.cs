using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed record ElevenLabsProvisionOperation(string Resource, string Name, string Method, string Path, JsonObject Body);

public static class ElevenLabsProvisioner
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    public static bool IsCommand(string[] args) => args.Length >= 2 && args[0] == "elevenlabs" && args[1] == "provision";

    public static IReadOnlyList<ElevenLabsProvisionOperation> BuildPlan(IConfiguration configuration, string contentRoot, bool amd = false)
    {
        var directory = FindAssets(contentRoot);
        var operations = new List<ElevenLabsProvisionOperation>();
        var publicBase = Configured(configuration, "PUBLIC_BASE_URL", "{{PUBLIC_BASE_URL}}").TrimEnd('/');
        var secretId = Configured(configuration, "ELEVENLABS_WORKSPACE_TOOL_SECRET_ID", "{{ELEVENLABS_WORKSPACE_TOOL_SECRET_ID}}");
        foreach (var path in Directory.GetFiles(Path.Combine(directory, "tools"), "*.json").Order(StringComparer.Ordinal))
        {
            var body = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var tool = body["tool_config"]!.AsObject();
            tool["api_schema"]!["url"] = tool["api_schema"]!["url"]!.GetValue<string>().Replace("{{PUBLIC_BASE_URL}}", publicBase, StringComparison.Ordinal);
            tool["api_schema"]!["request_headers"]!["Authorization"]!["secret_id"] = secretId;
            operations.Add(new("tool", tool["name"]!.GetValue<string>(), "POST", "/v1/convai/tools", body));
        }
        var knowledge = string.Join("\n\n", Directory.GetFiles(Path.Combine(directory, "knowledge"), "*.md").Order(StringComparer.Ordinal).Select(File.ReadAllText));
        // Text documents are immutable. Content-addressed names make updates/retries discoverable without duplicate uploads.
        var knowledgeName = "CivicOps synthetic municipal knowledge " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(knowledge)))[..12].ToLowerInvariant();
        operations.Add(new("knowledge", knowledgeName, "POST", "/v1/convai/knowledge-base/text", new() { ["name"] = knowledgeName, ["text"] = knowledge }));
        foreach (var path in Directory.GetFiles(Path.Combine(directory, "agents"), "*.json").Order(StringComparer.Ordinal))
        {
            var body = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var prompt = body["conversation_config"]!["agent"]!["prompt"]!.AsObject();
            prompt["tool_ids"] = new JsonArray(operations.Where(x => x.Resource == "tool").Select(x => JsonValue.Create($"{{{{tool:{x.Name}}}}}") as JsonNode).ToArray());
            prompt["knowledge_base"] = new JsonArray(new JsonObject { ["type"] = "text", ["name"] = knowledgeName, ["id"] = "{{knowledge:id}}", ["usage_mode"] = "prompt" });
            if (configuration["ELEVENLABS_VOICE_ID"] is { Length: > 0 } voiceId) body["conversation_config"]!["tts"]!["voice_id"] = voiceId;
            if (configuration["ELEVENLABS_TRANSFER_PHONE"] is { Length: > 0 } transfer)
                prompt["built_in_tools"]!["transfer_to_number"] = new JsonObject
                {
                    ["type"] = "system", ["name"] = "transfer_to_number", ["params"] = new JsonObject
                    {
                        ["system_tool_type"] = "transfer_to_number", ["transfers"] = new JsonArray(new JsonObject
                        {
                            ["transfer_destination"] = new JsonObject { ["type"] = "phone", ["phone_number"] = transfer },
                            ["condition"] = "Resident explicitly requests a human and agrees to a transfer.", ["transfer_type"] = "conference"
                        })
                    }
                };
            if (amd)
            {
                var url = Configured(configuration, "CIVICOPS_LLM_PUBLIC_URL", "{{CIVICOPS_LLM_PUBLIC_URL}}");
                prompt["llm"] = "custom-llm";
                prompt["custom_llm"] = new JsonObject { ["url"] = url, ["model_id"] = Configured(configuration, "CIVICOPS_LLM_MODEL", "Qwen/Qwen3-8B"), ["api_type"] = "chat_completions" };
                if (configuration["ELEVENLABS_WORKSPACE_LLM_SECRET_ID"] is { Length: > 0 } llmSecret)
                    prompt["custom_llm"]!["api_key"] = new JsonObject { ["secret_id"] = llmSecret };
            }
            if (configuration["ELEVENLABS_WIDGET_ALLOWED_HOSTS"] is { Length: > 0 } hosts)
            {
                var hostnames = hosts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (hostnames.Any(host => Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains(':') || host.Contains('/')))
                    throw new InvalidOperationException("ELEVENLABS_WIDGET_ALLOWED_HOSTS must contain hostnames without schemes, ports or paths.");
                body["platform_settings"]!["auth"]!["allowlist"] = new JsonArray(hostnames.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(host => (JsonNode)new JsonObject { ["hostname"] = host }).ToArray());
            }
            if (body["tags"]!.AsArray().Any(x => x?.GetValue<string>() == "resident-line"))
                body["platform_settings"]!["auth"]!["enable_auth"] = ElevenLabsOptions.From(configuration).SessionMode is "signed-url" or "webrtc";
            if (configuration["ELEVENLABS_POST_CALL_WEBHOOK_ID"] is { Length: > 0 } webhookId)
                body["platform_settings"]!["workspace_overrides"] = new JsonObject { ["webhooks"] = new JsonObject
                {
                    ["post_call_webhook_id"] = webhookId, ["events"] = new JsonArray("transcript", "call_initiation_failure"),
                    ["transcript_format"] = "json", ["exclude_transcript"] = false
                } };
            operations.Add(new("agent", body["name"]!.GetValue<string>(), "POST", "/v1/convai/agents/create", body));
        }
        return operations;
    }

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, string contentRoot,
        TextWriter? output = null, HttpClient? httpClient = null, CancellationToken ct = default)
    {
        output ??= Console.Out;
        var apply = args.Contains("--apply", StringComparer.Ordinal);
        var check = args.Contains("--check", StringComparer.Ordinal);
        if (apply && check) { await output.WriteLineAsync("Choose --check (read only) or --apply; they cannot be combined."); return 2; }
        var amd = args.Zip(args.Skip(1)).Any(pair => pair.First == "--llm" && pair.Second == "amd");
        var plan = BuildPlan(configuration, contentRoot, amd);
        if (check)
        {
            try { return await CheckAsync(plan, configuration, contentRoot, output, httpClient, ct); }
            catch (HttpRequestException ex)
            {
                await output.WriteLineAsync(ex.StatusCode is null ? "CHECK failed: ElevenLabs transport unavailable." : $"CHECK failed: ElevenLabs HTTP {(int)ex.StatusCode}.");
                return 2;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or TaskCanceledException)
            { await output.WriteLineAsync("CHECK failed: invalid, oversized, timed-out or ambiguous provider/state data."); return 2; }
        }
        if (!apply)
        {
            if (args.Contains("--json", StringComparer.Ordinal))
            { await output.WriteLineAsync(RenderDryRunJson(plan).ToJsonString(Pretty)); return 0; }
            await output.WriteLineAsync("DRY RUN: no API requests. Dependency placeholders are resolved to returned IDs during --apply; existing names are updated with PATCH.");
            foreach (var operation in plan)
            {
                await output.WriteLineAsync($"{operation.Method} {operation.Path} ({operation.Name})");
                var body = operation.Body.DeepClone();
                RedactReferences(body);
                await output.WriteLineAsync(body.ToJsonString(Pretty));
            }
            return 0;
        }
        var options = ElevenLabsOptions.From(configuration);
        if (options.Simulated) { await output.WriteLineAsync("Provisioning requires ELEVENLABS_API_KEY. No request was sent."); return 2; }
        if (!Uri.TryCreate(configuration["PUBLIC_BASE_URL"], UriKind.Absolute, out var publicUrl) || publicUrl.Scheme != "https"
            || string.IsNullOrWhiteSpace(configuration["ELEVENLABS_WORKSPACE_TOOL_SECRET_ID"]))
        { await output.WriteLineAsync("Apply requires HTTPS PUBLIC_BASE_URL and ELEVENLABS_WORKSPACE_TOOL_SECRET_ID."); return 2; }
        if (amd && (!Uri.TryCreate(configuration["CIVICOPS_LLM_PUBLIC_URL"], UriKind.Absolute, out var llmUrl) || llmUrl.Scheme != "https"))
        { await output.WriteLineAsync("AMD agent brain requires an HTTPS CIVICOPS_LLM_PUBLIC_URL Chat Completions endpoint."); return 2; }
        using var ownedClient = httpClient is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : null;
        var client = new ElevenLabsClient(httpClient ?? ownedClient!, options);
        var configuredData = configuration["CIVICOPS_DATA_DIR"];
        var dataDirectory = string.IsNullOrWhiteSpace(configuredData) ? Path.Combine(Path.GetDirectoryName(FindAssets(contentRoot))!, "Data") : Path.GetFullPath(configuredData);
        Directory.CreateDirectory(dataDirectory);
        var statePath = Path.Combine(dataDirectory, "elevenlabs-provision-state.json");
        var state = File.Exists(statePath) ? JsonNode.Parse(await File.ReadAllTextAsync(statePath, ct))!.AsObject() : new JsonObject();
        foreach (var operation in plan)
        {
            var key = operation.Resource + ":" + operation.Name;
            var id = state[key]?.GetValue<string>();
            id ??= await FindExistingAsync(client, operation, ct);
            var body = operation.Body.DeepClone().AsObject();
            if (operation.Resource == "agent")
            {
                var prompt = body["conversation_config"]!["agent"]!["prompt"]!;
                prompt["tool_ids"] = new JsonArray(plan.Where(x => x.Resource == "tool").Select(x => JsonValue.Create(state["tool:" + x.Name]!.GetValue<string>()) as JsonNode).ToArray());
                prompt["knowledge_base"]![0]!["id"] = state["knowledge:" + plan.Single(x => x.Resource == "knowledge").Name]!.GetValue<string>();
            }
            JsonObject response;
            if (operation.Resource == "tool") response = id is null ? await client.CreateToolAsync(body, ct) : await client.UpdateToolAsync(id, body, ct);
            else if (operation.Resource == "agent") response = id is null ? await client.CreateAgentAsync(body, ct) : await client.UpdateAgentAsync(id, body, ct);
            else response = id is null ? await client.CreateKnowledgeTextAsync(operation.Name, body["text"]!.GetValue<string>(), ct) : new JsonObject { ["id"] = id };
            id ??= response[operation.Resource == "agent" ? "agent_id" : "id"]?.GetValue<string>() ?? throw new InvalidDataException("Provider did not return a resource identifier.");
            state[key] = id;
            await AtomicJsonFile.WriteAsync(statePath, state.ToJsonString(Pretty), ct);
            await output.WriteLineAsync($"{operation.Resource}: {operation.Name}: {id}");
        }
        await output.WriteLineAsync($"ELEVENLABS_AGENT_ID={state["agent:Culltron CivicOps resident line"]!.GetValue<string>()}");
        await output.WriteLineAsync($"ELEVENLABS_CALLBACK_AGENT_ID={state["agent:Culltron CivicOps consented callback"]!.GetValue<string>()}");
        return 0;
    }

    public static JsonArray RenderDryRunJson(IReadOnlyList<ElevenLabsProvisionOperation> plan) => new(plan.Select(operation =>
    {
        var body = operation.Body.DeepClone();
        RedactReferences(body);
        return (JsonNode)new JsonObject { ["resource"] = operation.Resource, ["name"] = operation.Name,
            ["method"] = operation.Method, ["path"] = operation.Path, ["body"] = body };
    }).ToArray());

    private static async Task<int> CheckAsync(IReadOnlyList<ElevenLabsProvisionOperation> plan,
        IConfiguration configuration, string contentRoot, TextWriter output, HttpClient? httpClient, CancellationToken ct)
    {
        var options = ElevenLabsOptions.From(configuration);
        if (options.Simulated) { await output.WriteLineAsync("CHECK unavailable: ELEVENLABS_API_KEY is missing. No request was sent."); return 2; }
        if (!Uri.TryCreate(configuration["PUBLIC_BASE_URL"], UriKind.Absolute, out var origin) || origin.Scheme != "https"
            || string.IsNullOrWhiteSpace(configuration["ELEVENLABS_WORKSPACE_TOOL_SECRET_ID"]))
        { await output.WriteLineAsync("CHECK requires HTTPS PUBLIC_BASE_URL and ELEVENLABS_WORKSPACE_TOOL_SECRET_ID to compare configured tool URLs and secret references."); return 2; }
        using var ownedClient = httpClient is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : null;
        var client = new ElevenLabsClient(httpClient ?? ownedClient!, options);
        var configuredData = configuration["CIVICOPS_DATA_DIR"];
        var dataDirectory = string.IsNullOrWhiteSpace(configuredData) ? Path.Combine(Path.GetDirectoryName(FindAssets(contentRoot))!, "Data") : Path.GetFullPath(configuredData);
        var statePath = Path.Combine(dataDirectory, "elevenlabs-provision-state.json");
        // Read-only checks do not create directories, rewrite state or save provider payloads.
        var state = File.Exists(statePath) ? JsonNode.Parse(await File.ReadAllTextAsync(statePath, ct))!.AsObject() : new JsonObject();
        var drift = false;
        await output.WriteLineAsync("CHECK: GET requests only. Drift lists field paths, never provider values or credentials.");
        foreach (var operation in plan)
        {
            var key = operation.Resource + ":" + operation.Name;
            var configuredAgent = operation.Resource == "agent" ? configuration[operation.Name == "Culltron CivicOps resident line"
                ? "ELEVENLABS_AGENT_ID" : "ELEVENLABS_CALLBACK_AGENT_ID"] : null;
            var id = string.IsNullOrWhiteSpace(configuredAgent) ? state[key]?.GetValue<string>() : configuredAgent;
            id ??= await FindExistingAsync(client, operation, ct);
            if (id is null) { drift = true; await output.WriteLineAsync($"MISSING {operation.Resource}: {operation.Name}"); continue; }
            state[key] = id;
        }
        foreach (var operation in plan.Where(x => state[x.Resource + ":" + x.Name] is not null))
        {
            var id = state[operation.Resource + ":" + operation.Name]!.GetValue<string>();
            JsonObject remote;
            try
            {
                remote = operation.Resource switch
                {
                    "agent" => await client.GetAgentAsync(id, ct),
                    "tool" => await client.GetToolAsync(id, ct),
                    _ => await client.GetKnowledgeAsync(id, ct)
                };
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            { drift = true; await output.WriteLineAsync($"MISSING {operation.Resource}: {operation.Name} (saved identifier no longer exists)"); continue; }
            var expected = operation.Body.DeepClone().AsObject();
            if (operation.Resource == "agent")
            {
                var dependencies = plan.Where(x => x.Resource is "tool" or "knowledge").ToArray();
                if (dependencies.Any(x => state[x.Resource + ":" + x.Name] is null))
                { drift = true; await output.WriteLineAsync($"DRIFT agent: {operation.Name}: unresolved tool/knowledge dependencies"); continue; }
                expected["conversation_config"]!["agent"]!["prompt"]!["tool_ids"] = new JsonArray(plan.Where(x => x.Resource == "tool")
                    .Select(x => JsonValue.Create(state["tool:" + x.Name]!.GetValue<string>()) as JsonNode).ToArray());
                expected["conversation_config"]!["agent"]!["prompt"]!["knowledge_base"]![0]!["id"] = state["knowledge:" + plan.Single(x => x.Resource == "knowledge").Name]!.GetValue<string>();
            }
            var paths = new List<string>();
            if (operation.Resource == "knowledge")
            {
                Compare(new JsonObject { ["name"] = operation.Name, ["type"] = "text" }, remote, "$", paths);
                var text = await client.GetKnowledgeContentAsync(id, ct);
                if (text != expected["text"]!.GetValue<string>()) paths.Add("$.text");
            }
            else
            {
                Compare(expected, remote, "$", paths);
                if (operation.Resource == "agent")
                {
                    var desiredTools = expected["conversation_config"]!["agent"]!["prompt"]!["built_in_tools"]!.AsObject();
                    if (remote["conversation_config"]?["agent"]?["prompt"]?["built_in_tools"] is JsonObject actualTools)
                        foreach (var tool in actualTools.Where(x => x.Value is not null && !desiredTools.ContainsKey(x.Key)))
                            paths.Add("$.conversation_config.agent.prompt.built_in_tools." + tool.Key);
                }
            }
            if (paths.Count > 0) { drift = true; await output.WriteLineAsync($"DRIFT {operation.Resource}: {operation.Name}: {string.Join(", ", paths)}"); }
            else await output.WriteLineAsync($"OK {operation.Resource}: {operation.Name}");
        }
        await output.WriteLineAsync(drift ? "Drift detected; no resources were changed." : "No drift in CivicOps-managed fields; no resources were changed.");
        return drift ? 1 : 0;
    }

    // API reads add defaults and metadata. Compare the fields managed by the plan; arrays must match completely.
    private static void Compare(JsonNode? expected, JsonNode? actual, string path, List<string> differences)
    {
        if (expected is JsonObject obj && actual is JsonObject remote)
        {
            foreach (var property in obj) Compare(property.Value, remote[property.Key], path + "." + property.Key, differences);
        }
        else if (expected is JsonArray array && actual is JsonArray remoteArray)
        {
            if (array.Count != remoteArray.Count) { differences.Add(path); return; }
            if (path.EndsWith(".tool_ids", StringComparison.Ordinal) || path.EndsWith(".tags", StringComparison.Ordinal)
                || path.EndsWith(".client_events", StringComparison.Ordinal) || path.EndsWith(".events", StringComparison.Ordinal)
                || path.EndsWith(".allowlist", StringComparison.Ordinal))
            {
                var unmatched = remoteArray.ToList();
                foreach (var item in array)
                {
                    var index = unmatched.FindIndex(candidate => { var delta = new List<string>(); Compare(item, candidate, path, delta); return delta.Count == 0; });
                    if (index < 0) { differences.Add(path); return; }
                    unmatched.RemoveAt(index);
                }
                return;
            }
            for (var i = 0; i < array.Count; i++) Compare(array[i], remoteArray[i], path + "[" + i + "]", differences);
        }
        else if (!JsonNode.DeepEquals(expected, actual)) differences.Add(path);
    }

    private static async Task<string?> FindExistingAsync(ElevenLabsClient client, ElevenLabsProvisionOperation operation, CancellationToken ct)
    {
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var response = operation.Resource switch
            {
                "tool" => await client.ListToolsAsync(operation.Name, cursor, ct),
                "agent" => await client.ListAgentsAsync(operation.Name, cursor, ct),
                _ => await client.ListKnowledgeAsync(operation.Name, cursor, ct)
            };
            var collection = response[operation.Resource == "tool" ? "tools" : operation.Resource == "agent" ? "agents" : "documents"] as JsonArray;
            if (collection is not null)
            {
                var found = collection.OfType<JsonObject>().Where(x => (operation.Resource == "tool" ? x["tool_config"]?["name"] : x["name"])?.GetValue<string>() == operation.Name).ToArray();
                if (found.Length > 1) throw new InvalidDataException("Multiple named resources found; reconcile provider resources before provisioning.");
                if (found.Length == 1) return found[0][operation.Resource == "agent" ? "agent_id" : "id"]!.GetValue<string>();
            }
            if (response["has_more"]?.GetValue<bool>() != true) return null;
            cursor = response["next_cursor"]?.GetValue<string>() ?? throw new InvalidDataException("Provider pagination cursor missing.");
        }
        throw new InvalidDataException("Provider resource discovery exceeded its bounded page budget.");
    }

    private static string FindAssets(string contentRoot)
    {
        foreach (var root in new[] { contentRoot, Path.Combine(contentRoot, "products", "civicops"), AppContext.BaseDirectory })
        { var path = Path.Combine(root, "elevenlabs"); if (Directory.Exists(Path.Combine(path, "agents"))) return path; }
        throw new DirectoryNotFoundException("ElevenLabs agent assets were not found.");
    }
    private static string Configured(IConfiguration configuration, string key, string fallback) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? fallback : configuration[key]!.Trim();
    private static void RedactReferences(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var property in obj.ToArray())
                if (property.Key is "secret_id" or "phone_number") obj[property.Key] = "[redacted-workspace-reference-or-contact]";
                else RedactReferences(property.Value);
        else if (node is JsonArray array) foreach (var item in array) RedactReferences(item);
    }
}
