using System.Text.Json.Nodes;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;

namespace CivicOps.Tests;

public sealed class ElevenLabsProvisionerTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static IConfiguration Config(params (string Key, string? Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(x => x.Key, x => x.Value)).Build();
    [Fact]
    public async Task DryRunDoesNotUseNetworkAndNeverPrintsCredentials()
    {
        var handler = new ElevenLabsFakeHandler { Respond = _ => throw new InvalidOperationException("Dry run cannot call HTTP") };
        var output = new StringWriter();
        var code = await ElevenLabsProvisioner.RunAsync(["elevenlabs", "provision"], Config(("ELEVENLABS_API_KEY", "test-elevenlabs-key"),
            ("CIVICOPS_ELEVENLABS_TOOL_SECRET", "test-tool-secret"), ("ELEVENLABS_WORKSPACE_TOOL_SECRET_ID", "test-workspace-reference")), Root, output, new(handler));
        Assert.Equal(0, code); Assert.Empty(handler.Requests);
        Assert.Contains("DRY RUN", output.ToString());
        Assert.DoesNotContain("test-elevenlabs-key", output.ToString()); Assert.DoesNotContain("test-tool-secret", output.ToString());
        Assert.DoesNotContain("test-workspace-reference", output.ToString());
        Assert.Contains("[redacted-workspace-reference-or-contact]", output.ToString());
    }
    [Fact]
    public void PlanContainsFiveToolsKnowledgeAndTwoAgents()
    {
        var plan = ElevenLabsProvisioner.BuildPlan(Config(), Root);
        Assert.Equal(5, plan.Count(x => x.Resource == "tool")); Assert.Single(plan, x => x.Resource == "knowledge"); Assert.Equal(2, plan.Count(x => x.Resource == "agent"));
        Assert.All(plan.Where(x => x.Resource == "tool"), x => Assert.Contains("{{PUBLIC_BASE_URL}}/api/channels/elevenlabs/tools/", x.Body["tool_config"]!["api_schema"]!["url"]!.GetValue<string>()));
    }
    [Fact]
    public void AmdAgentUsesCustomLlmAndWorkspaceSecretReference()
    {
        var plan = ElevenLabsProvisioner.BuildPlan(Config(("CIVICOPS_LLM_PUBLIC_URL", "https://amd.example.invalid/v1/chat/completions"),
            ("CIVICOPS_LLM_MODEL", "Qwen/Qwen3-8B"), ("ELEVENLABS_WORKSPACE_LLM_SECRET_ID", "test-amd-workspace-id")), Root, true);
        Assert.All(plan.Where(x => x.Resource == "agent"), x =>
        {
            var prompt = x.Body["conversation_config"]!["agent"]!["prompt"]!;
            Assert.Equal("custom-llm", prompt["llm"]!.GetValue<string>());
            Assert.Equal("https://amd.example.invalid/v1/chat/completions", prompt["custom_llm"]!["url"]!.GetValue<string>());
            Assert.Equal("test-amd-workspace-id", prompt["custom_llm"]!["api_key"]!["secret_id"]!.GetValue<string>());
        });
    }
    [Fact]
    public void ToolsUseSystemDynamicVariablesAndWorkspaceHeader()
    {
        foreach (var tool in ElevenLabsProvisioner.BuildPlan(Config(), Root).Where(x => x.Resource == "tool"))
        {
            var api = tool.Body["tool_config"]!["api_schema"]!;
            Assert.NotNull(api["request_headers"]!["Authorization"]!["secret_id"]);
            Assert.Equal("system__conversation_id", api["request_body_schema"]!["properties"]!["conversation_id"]!["dynamic_variable"]!.GetValue<string>());
            Assert.Equal("system__caller_id", api["request_body_schema"]!["properties"]!["caller_id"]!["dynamic_variable"]!.GetValue<string>());
        }
    }
    [Fact]
    public void ResidentPresetsRetainHumanGateAndAudioPrivacy()
    {
        var agent = ElevenLabsProvisioner.BuildPlan(Config(), Root).Single(x => x.Name == "Culltron CivicOps resident line").Body;
        var prompt = agent["conversation_config"]!["agent"]!["prompt"]!;
        Assert.Contains("every dispatch needs a human", prompt["prompt"]!.GetValue<string>());
        Assert.NotNull(prompt["built_in_tools"]!["language_detection"]); Assert.NotNull(prompt["built_in_tools"]!["end_call"]);
        Assert.NotNull(agent["conversation_config"]!["language_presets"]!["af"]);
        Assert.False(agent["platform_settings"]!["privacy"]!["record_voice"]!.GetValue<bool>());
        Assert.Equal(7, agent["platform_settings"]!["privacy"]!["retention_days"]!.GetValue<int>());
    }
    [Fact]
    public async Task ApplyWithoutKeyMakesNoRequest()
    {
        var handler = new ElevenLabsFakeHandler();
        Assert.Equal(2, await ElevenLabsProvisioner.RunAsync(["elevenlabs", "provision", "--apply"], Config(), Root, new StringWriter(), new(handler)));
        Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task ApplyRequiresHttpsAndWorkspaceSecretReference()
    {
        var handler = new ElevenLabsFakeHandler();
        Assert.Equal(2, await ElevenLabsProvisioner.RunAsync(["elevenlabs", "provision", "--apply"], Config(("ELEVENLABS_API_KEY", "test-elevenlabs-key"),
            ("PUBLIC_BASE_URL", "http://example.invalid")), Root, new StringWriter(), new(handler)));
        Assert.Empty(handler.Requests);
    }
    [Fact]
    public void TransferIsOnlyAddedWhenOwnerConfiguresDestination()
    {
        var original = ElevenLabsProvisioner.BuildPlan(Config(), Root).First(x => x.Resource == "agent");
        Assert.Null(original.Body["conversation_config"]!["agent"]!["prompt"]!["built_in_tools"]!["transfer_to_number"]);
        var configured = ElevenLabsProvisioner.BuildPlan(Config(("ELEVENLABS_TRANSFER_PHONE", "+27 00 000 0000")), Root).First(x => x.Resource == "agent");
        Assert.NotNull(configured.Body["conversation_config"]!["agent"]!["prompt"]!["built_in_tools"]!["transfer_to_number"]);
    }
    [Fact]
    public async Task FakeTransportProvisioningResolvesDependenciesAndSecondRunOnlyUpdates()
    {
        var directory = Path.Combine(Path.GetTempPath(), "elevenlabs-provision-tests-" + Guid.NewGuid().ToString("N"));
        var resourceCounter = 0;
        var handler = new ElevenLabsFakeHandler
        {
            Respond = request => request.Method == HttpMethod.Get
                ? ElevenLabsFakeHandler.Json("{\"tools\":[],\"agents\":[],\"documents\":[],\"has_more\":false}")
                : ElevenLabsFakeHandler.Json($"{{\"id\":\"fake-resource-{++resourceCounter}\",\"agent_id\":\"fake-agent-{resourceCounter}\"}}")
        };
        var config = Config(("ELEVENLABS_API_KEY", "test-elevenlabs-key"), ("PUBLIC_BASE_URL", "https://civicops.example.invalid"),
            ("ELEVENLABS_WORKSPACE_TOOL_SECRET_ID", "test-workspace-reference"), ("CIVICOPS_DATA_DIR", directory));
        try
        {
            // Every HTTP request terminates in the in-memory handler; this never provisions a provider workspace.
            Assert.Equal(0, await ElevenLabsProvisioner.RunAsync(["elevenlabs", "provision", "--apply"], config, Root, new StringWriter(), new(handler)));
            Assert.Equal(8, handler.Requests.Count(x => x.Method == "POST"));
            var agentRequests = handler.Requests.Where(x => x.Url.EndsWith("/agents/create", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, agentRequests.Length);
            foreach (var request in agentRequests)
            {
                var prompt = JsonNode.Parse(request.Body!)!["conversation_config"]!["agent"]!["prompt"]!;
                Assert.Equal(5, prompt["tool_ids"]!.AsArray().Count);
                Assert.All(prompt["tool_ids"]!.AsArray(), id => Assert.StartsWith("fake-resource-", id!.GetValue<string>()));
                Assert.StartsWith("fake-resource-", prompt["knowledge_base"]![0]!["id"]!.GetValue<string>());
                Assert.DoesNotContain("{{tool:", request.Body);
            }
            var before = handler.Requests.Count;
            Assert.Equal(0, await ElevenLabsProvisioner.RunAsync(["elevenlabs", "provision", "--apply"], config, Root, new StringWriter(), new(handler)));
            var second = handler.Requests.Skip(before).ToArray();
            Assert.Equal(7, second.Length); Assert.All(second, request => Assert.Equal("PATCH", request.Method));
            Assert.DoesNotContain("test-elevenlabs-key", await File.ReadAllTextAsync(Path.Combine(directory, "elevenlabs-provision-state.json")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
