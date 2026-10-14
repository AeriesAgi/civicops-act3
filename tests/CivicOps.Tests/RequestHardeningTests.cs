using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CivicOps.Services;

namespace CivicOps.Tests;

public sealed class RequestHardeningTests
{
    [Theory]
    [InlineData("{")] [InlineData("{\"description\":")] [InlineData("{\"description\":\"Synthetic issue\",\"Description\":\"Duplicate value\"}")]
    [InlineData("{\"description\":\"Synthetic issue\",\"nested\":{\"same\":1,\"same\":2}}")]
    public async Task MalformedAndAmbiguousJsonReturnProblemDetails(string json)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var response = await client.PostAsync("/api/incidents", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.DoesNotContain("StackTrace", body.GetRawText()); Assert.DoesNotContain("System.Text.Json", body.GetRawText());
    }

    [Theory]
    [InlineData("\u202esynthetic spoof")] [InlineData("synthetic\u200bhidden")] [InlineData("synthetic\u0000nul")]
    [InlineData("synthetic\u2066isolate")] [InlineData("synthetic\uFEFFhidden")]
    [InlineData("synthetic e\u0301\u0301\u0301\u0301\u0301\u0301\u0301\u0301\u0301")]
    public async Task UnicodeAbuseIsRejected(string description)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/incidents", new { description })).StatusCode);
    }

    [Fact]
    public async Task AccentsAndSupplementaryCharactersRemainSupported()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/incidents", new { description = "Sintetiese pyp by die cafe\u0301, water lek 💧", area = "  Sintetiese  cafe\u0301  " });
        response.EnsureSuccessStatusCode();
        var incident = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident");
        var dispatcher = await client.GetFromJsonAsync<JsonElement>("/api/dispatch/incidents/" + incident.GetProperty("id").GetString());
        Assert.Equal("Sintetiese café", dispatcher.GetProperty("area").GetString());
    }

    [Theory]
    [InlineData("Idempotency-Key", "synthetic\r\nInjected: value")]
    [InlineData("X-CivicOps-Conversation-Id", "synthetic\nInjected: value")]
    public async Task HeaderInjectionReturnsProblemDetails(string name, string value)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var result = await app.Server.SendAsync(context =>
        {
            context.Request.Method = "POST"; context.Request.Path = "/api/incidents";
            context.Request.ContentType = "application/json"; context.Request.Headers[name] = value;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"description\":\"Synthetic report by hall\"}"));
        });
        Assert.Equal(400, result.Response.StatusCode); Assert.Equal("application/problem+json", result.Response.ContentType);
    }

    [Fact]
    public async Task MultipleIdempotencyHeadersAreRejected()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var result = await app.Server.SendAsync(context =>
        {
            context.Request.Method = "POST"; context.Request.Path = "/api/incidents"; context.Request.ContentType = "application/json";
            context.Request.Headers["idempotency-key"] = new[] { "synthetic-one", "synthetic-two" };
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"description\":\"Synthetic report by hall\"}"));
        });
        Assert.Equal(400, result.Response.StatusCode);
    }

    [Fact]
    public async Task ChunkedOversizeAndWrongContentTypeAreBounded()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var result = await app.Server.SendAsync(context =>
        {
            context.Request.Method = "POST"; context.Request.Path = "/api/incidents"; context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 32769)));
        });
        Assert.Equal(413, result.Response.StatusCode); Assert.Equal("application/problem+json", result.Response.ContentType);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync("/api/incidents", new StringContent("{}"))).StatusCode);
    }

    [Theory]
    [InlineData("residentLanguage", "unexpected")]
    [InlineData("contactPreference", "anywhere")]
    [InlineData("contactPhone", "synthetic\r\nTo: injected")]
    public async Task IntakeChoicesAndContactHeadersUseAllowLists(string field, string value)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var request = new Dictionary<string, string> { ["description"] = "Synthetic report by hall", [field] = value };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/incidents", request)).StatusCode);
    }

    [Fact]
    public async Task ToolConversationRotationCannotBypassSharedIpBudget()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_TOOL_LIMIT"] = "2";
        using var client = app.CreateClient();
        for (var index = 0; index < 3; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/channels/elevenlabs/tools/list_service_categories") { Content = JsonContent.Create(new { }) };
            request.Headers.Add("X-CivicOps-Conversation-Id", "synthetic-rotation-" + index);
            Assert.Equal(index < 2 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, (await client.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task AuthQuotaIsPartitionedPerIp()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_AUTH_LIMIT"] = "1"; using var client = app.CreateClient();
        async Task<int> Send(string ip) => (await app.Server.SendAsync(context =>
        { context.Connection.RemoteIpAddress = IPAddress.Parse(ip); context.Request.Method = "GET"; context.Request.Path = "/auth/login"; })).Response.StatusCode;
        Assert.Equal(503, await Send("192.0.2.61")); Assert.Equal(429, await Send("192.0.2.61")); Assert.Equal(503, await Send("192.0.2.62"));
    }
}
