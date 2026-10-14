using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;

namespace CivicOps.Tests;

public sealed class ElevenLabsClientTests
{
    private static ElevenLabsOptions Live() => new() { ApiKey = "test-elevenlabs-key", ApiBase = "https://api.elevenlabs.io" };
    private static JsonObject Body() => new() { ["name"] = "Synthetic civic agent" };

    [Theory]
    [InlineData("create-agent", "POST", "/v1/convai/agents/create")]
    [InlineData("update-agent", "PATCH", "/v1/convai/agents/fake-agent")]
    [InlineData("get-agent", "GET", "/v1/convai/agents/fake-agent")]
    [InlineData("create-tool", "POST", "/v1/convai/tools")]
    [InlineData("update-tool", "PATCH", "/v1/convai/tools/fake-tool")]
    [InlineData("list-tools", "GET", "/v1/convai/tools?page_size=100&search=synthetic")]
    [InlineData("list-agents", "GET", "/v1/convai/agents?page_size=100&search=synthetic")]
    [InlineData("list-knowledge", "GET", "/v1/convai/knowledge-base?page_size=100&search=synthetic")]
    [InlineData("knowledge", "POST", "/v1/convai/knowledge-base/text")]
    [InlineData("signed-url", "GET", "/v1/convai/conversation/get-signed-url?agent_id=fake-agent")]
    [InlineData("token", "GET", "/v1/convai/conversation/token?agent_id=fake-agent")]
    [InlineData("conversation", "GET", "/v1/convai/conversations/fake-conversation")]
    [InlineData("twilio", "POST", "/v1/convai/twilio/outbound-call")]
    [InlineData("sip-trunk", "POST", "/v1/convai/sip-trunk/outbound-call")]
    public async Task UsesDocumentedPathsAndApiKeyHeader(string action, string method, string path)
    {
        var handler = new ElevenLabsFakeHandler();
        var client = new ElevenLabsClient(new HttpClient(handler), Live());
        await Invoke(client, action);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(method, request.Method);
        Assert.Equal("https://api.elevenlabs.io" + path, request.Url);
        Assert.Equal("test-elevenlabs-key", request.ApiKey);
        if (method != "GET") Assert.Equal("application/json", request.ContentType);
        else Assert.Null(request.Body);
    }

    [Theory]
    [InlineData("twilio")]
    [InlineData("sip-trunk")]
    public async Task OutboundBodyMatchesPhoneApi(string provider)
    {
        var handler = new ElevenLabsFakeHandler();
        await Invoke(new(new HttpClient(handler), Live()), provider);
        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!;
        Assert.Equal("fake-agent", body["agent_id"]!.GetValue<string>());
        Assert.Equal("fake-phone-id", body["agent_phone_number_id"]!.GetValue<string>());
        Assert.Equal("+27 00 000 0000", body["to_number"]!.GetValue<string>());
        Assert.Equal("CIV-SYNTHETIC-0001", body["conversation_initiation_client_data"]!["dynamic_variables"]!["case_reference"]!.GetValue<string>());
        Assert.Null(body["phone_number"]);
    }

    [Fact]
    public async Task KnowledgeTextUsesTextAndNameFields()
    {
        var handler = new ElevenLabsFakeHandler();
        await Invoke(new(new HttpClient(handler), Live()), "knowledge");
        var body = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!;
        Assert.Equal("Synthetic KB", body["name"]!.GetValue<string>());
        Assert.Equal("Synthetic knowledge only.", body["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task PathAndQueryIdentifiersAreEscaped()
    {
        var handler = new ElevenLabsFakeHandler();
        var client = new ElevenLabsClient(new HttpClient(handler), Live());
        await client.GetSignedUrlAsync("fake&admin=true");
        Assert.Contains("agent_id=fake%26admin%3Dtrue", handler.Requests[0].Url);
        await client.ListToolsAsync("fake & name", "cursor?injected=true");
        Assert.Contains("cursor=cursor%3Finjected%3Dtrue", handler.Requests[1].Url);
    }

    [Theory]
    [InlineData("create-agent")]
    [InlineData("create-tool")]
    [InlineData("knowledge")]
    [InlineData("signed-url")]
    [InlineData("token")]
    [InlineData("twilio")]
    public async Task MissingKeyIsDeterministicAndMakesNoRequest(string action)
    {
        var handler = new ElevenLabsFakeHandler { Respond = _ => throw new InvalidOperationException("No network expected") };
        var client = new ElevenLabsClient(new HttpClient(handler), new());
        var first = await Invoke(client, action);
        var second = await Invoke(client, action);
        Assert.Equal(first.ToJsonString(), second.ToJsonString());
        Assert.Equal("simulated", first["mode"]!.GetValue<string>());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidPhoneProviderIsRejectedBeforeTransport()
    {
        var handler = new ElevenLabsFakeHandler();
        await Assert.ThrowsAsync<ArgumentException>(() => new ElevenLabsClient(new(handler), Live()).OutboundCallAsync("fake", "fake", "+27 00 000 0000", new(), "invalid"));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"not an object\"")]
    public async Task NonObjectResponseFailsClosed(string response)
    {
        var handler = new ElevenLabsFakeHandler { Respond = _ => ElevenLabsFakeHandler.Json(response) };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ElevenLabsClient(new(handler), Live()).GetAgentAsync("fake-agent"));
    }

    [Fact]
    public async Task ErrorDoesNotExposeProviderBodyOrCredentials()
    {
        var handler = new ElevenLabsFakeHandler { Respond = _ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("private transcript test-elevenlabs-key") } };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new ElevenLabsClient(new(handler), Live()).GetAgentAsync("fake"));
        Assert.Equal("ElevenLabs returned HTTP 401.", error.Message);
    }
    [Theory]
    [InlineData("https://test-user:test-secret@example.invalid")]
    [InlineData("https://example.invalid?api_key=test-secret")]
    [InlineData("https://example.invalid#test-secret")]
    [InlineData("file:///synthetic-api")]
    [InlineData("not-a-url")]
    public async Task InvalidApiBaseIsRejectedBeforeTransport(string apiBase)
    {
        var handler = new ElevenLabsFakeHandler();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ElevenLabsClient(new(handler), new() { ApiKey = "test-elevenlabs-key", ApiBase = apiBase }).GetAgentAsync("fake"));
        Assert.Empty(handler.Requests); Assert.DoesNotContain("test-secret", error.Message);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ResponseSizeBudgetAppliesWithAndWithoutContentLength(bool knownLength)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', 1024 * 1024) + "\"}");
        var handler = new ElevenLabsFakeHandler { Respond = _ => new(HttpStatusCode.OK)
        { Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new UnseekableStream(bytes)) } };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ElevenLabsClient(new(handler), Live()).GetAgentAsync("fake"));
        Assert.Equal("ElevenLabs response exceeded its size budget.", error.Message);
    }
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")]
    public void BlankConfigurationUsesPublicApiAndSimulation(string? key)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ELEVENLABS_API_KEY"] = key, ["ELEVENLABS_API_BASE"] = " " }).Build();
        var options = ElevenLabsOptions.From(config);
        Assert.True(options.Simulated); Assert.Equal("https://api.elevenlabs.io", options.ApiBase);
        Assert.False(options.CallbacksEnabled);
    }

    private static Task<JsonObject> Invoke(ElevenLabsClient client, string action) => action switch
    {
        "create-agent" => client.CreateAgentAsync(Body()), "update-agent" => client.UpdateAgentAsync("fake-agent", Body()),
        "get-agent" => client.GetAgentAsync("fake-agent"), "create-tool" => client.CreateToolAsync(new() { ["tool_config"] = Body() }),
        "update-tool" => client.UpdateToolAsync("fake-tool", new() { ["tool_config"] = Body() }),
        "list-tools" => client.ListToolsAsync("synthetic"), "list-agents" => client.ListAgentsAsync("synthetic"), "list-knowledge" => client.ListKnowledgeAsync("synthetic"),
        "knowledge" => client.CreateKnowledgeTextAsync("Synthetic KB", "Synthetic knowledge only."),
        "signed-url" => client.GetSignedUrlAsync("fake-agent"), "token" => client.GetConversationTokenAsync("fake-agent"),
        "conversation" => client.GetConversationAsync("fake-conversation"),
        _ => client.OutboundCallAsync("fake-agent", "fake-phone-id", "+27 00 000 0000", new() { ["case_reference"] = "CIV-SYNTHETIC-0001" }, action)
    };
}

internal sealed class ElevenLabsFakeHandler : HttpMessageHandler
{
    public List<(string Method, string Url, string? ApiKey, string? Body, string? ContentType)> Requests { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; init; } = _ => Json("{\"agent_id\":\"fake-agent\",\"id\":\"fake-resource\",\"tools\":[],\"agents\":[],\"documents\":[],\"has_more\":false}");
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method.Method, request.RequestUri!.ToString(), request.Headers.TryGetValues("xi-api-key", out var values) ? values.Single() : null,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), request.Content?.Headers.ContentType?.MediaType));
        return Respond(request);
    }
    public static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
}
