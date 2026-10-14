using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class ElevenLabsSessionTests
{
    [Theory]
    [InlineData("{\"success\":true,\"message\":\"Synthetic Twilio accepted\",\"callSid\":\"synthetic-call\"}", HttpStatusCode.OK)]
    [InlineData("{\"success\":true,\"message\":\"Synthetic SIP accepted\",\"sip_call_id\":\"synthetic-sip\",\"conversation_id\":\"synthetic-conversation\"}", HttpStatusCode.OK)]
    [InlineData("{\"success\":true,\"conversation_id\":\"synthetic-conversation\"}", HttpStatusCode.BadGateway)]
    [InlineData("{\"success\":true,\"message\":\"Synthetic accepted\",\"conversation_id\":123}", HttpStatusCode.BadGateway)]
    public async Task CallbackAcceptsOptionalProviderCallIdentifiersAndRejectsMalformedFields(string body, HttpStatusCode expected)
    {
        using var app = new TestApp();
        app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        app.Settings["ELEVENLABS_API_KEY"] = "test-elevenlabs-key";
        app.Settings["ELEVENLABS_CALLBACK_AGENT_ID"] = "synthetic-callback-agent";
        app.Settings["ELEVENLABS_AGENT_PHONE_NUMBER_ID"] = "synthetic-phone";
        var handler = new ElevenLabsFakeHandler { Respond = _ => ElevenLabsFakeHandler.Json(body) };
        using var configured = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(sp => new ElevenLabsClient(new(handler), sp.GetRequiredService<ElevenLabsOptions>()))));
        using var client = configured.CreateClient();
        var intake = await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic pothole report near municipal park", area = "Synthetic ward", privacyConsentAccepted = true, privacyConsentVersion = PrivacyConsent.Version });
        Assert.Equal(HttpStatusCode.Created, intake.StatusCode);
        var reference = (await intake.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident").GetProperty("referenceNumber").GetString()!;
        var store = configured.Services.GetRequiredService<IncidentStore>();
        await store.ChangeAsync(reference, null, incident => { incident.CallbackConsent = true; incident.ConsentToContact = true; incident.ContactPhone = "+27 00 000 0000"; return null; }, default);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-dispatch-token");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "synthetic-callback");
        var response = await client.PostAsync("/api/incidents/" + reference + "/callback", null);
        Assert.Equal(expected, response.StatusCode);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("test-elevenlabs-key", await response.Content.ReadAsStringAsync());
        var replay = await client.PostAsync("/api/incidents/" + reference + "/callback", null);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("signed-url", "{\"signed_url\":\"wss://api.elevenlabs.io/v1/convai/conversation?token=synthetic\",\"unexpected\":\"provider-private-value\"}", "signed_url", "/v1/convai/conversation/get-signed-url?agent_id=synthetic-agent")]
    [InlineData("webrtc", "{\"token\":\"synthetic-conversation-token\",\"conversation_id\":\"synthetic-conversation-id\",\"unexpected\":\"provider-private-value\"}", "token", "/v1/convai/conversation/token?agent_id=synthetic-agent")]
    public async Task PrivateSessionUsesDocumentedCredentialsAndProjectsOnlySessionFields(string mode, string body, string credential, string path)
    {
        using var app = new TestApp();
        app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        app.Settings["ELEVENLABS_API_KEY"] = "test-elevenlabs-key";
        app.Settings["ELEVENLABS_AGENT_ID"] = "synthetic-agent";
        app.Settings["ELEVENLABS_SESSION_MODE"] = mode;
        var handler = new ElevenLabsFakeHandler { Respond = _ => ElevenLabsFakeHandler.Json(body) };
        using var configured = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(sp => new ElevenLabsClient(new(handler), sp.GetRequiredService<ElevenLabsOptions>()))));
        using var client = configured.CreateClient();
        var response = await client.GetAsync("/api/channels/elevenlabs/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal("live", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal(mode, json.RootElement.GetProperty("sessionMode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty(credential).GetString()));
        Assert.DoesNotContain("provider-private-value", text);
        Assert.DoesNotContain("test-elevenlabs-key", text);
        Assert.Equal("https://api.elevenlabs.io" + path, Assert.Single(handler.Requests).Url);
    }

    [Theory]
    [InlineData("signed-url", "{}")]
    [InlineData("signed-url", "{\"signed_url\":\"https://example.invalid\"}")]
    [InlineData("signed-url", "{\"signed_url\":123}")]
    [InlineData("webrtc", "{\"token\":\"synthetic\"}")]
    [InlineData("webrtc", "{\"token\":\"synthetic\",\"conversation_id\":123}")]
    [InlineData("webrtc", "{\"token\":\"\",\"conversation_id\":\"synthetic\"}")]
    public async Task MalformedSessionResponsesFailClosed(string mode, string body)
    {
        using var app = new TestApp();
        app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        app.Settings["ELEVENLABS_API_KEY"] = "test-elevenlabs-key";
        app.Settings["ELEVENLABS_AGENT_ID"] = "synthetic-agent";
        app.Settings["ELEVENLABS_SESSION_MODE"] = mode;
        var handler = new ElevenLabsFakeHandler { Respond = _ => ElevenLabsFakeHandler.Json(body) };
        using var configured = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(sp => new ElevenLabsClient(new(handler), sp.GetRequiredService<ElevenLabsOptions>()))));
        using var client = configured.CreateClient();
        var response = await client.GetAsync("/api/channels/elevenlabs/session");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("test-elevenlabs-key", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(401, "{\"detail\":{\"status\":\"quota_exceeded\",\"message\":\"This request exceeds your quota.\"}}")]
    [InlineData(429, "{}")]
    public async Task ProviderQuotaRejectionFallsBackToLabelledSimulation(int status, string body)
    {
        using var app = new TestApp();
        app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        app.Settings["ELEVENLABS_API_KEY"] = "test-elevenlabs-key";
        app.Settings["ELEVENLABS_AGENT_ID"] = "synthetic-agent";
        app.Settings["ELEVENLABS_SESSION_MODE"] = "signed-url";
        var handler = new ElevenLabsFakeHandler { Respond = _ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") } };
        using var configured = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(sp => new ElevenLabsClient(new(handler), sp.GetRequiredService<ElevenLabsOptions>()))));
        using var client = configured.CreateClient();
        var response = await client.GetAsync("/api/channels/elevenlabs/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"mode\":\"simulated\"", text);
        Assert.Contains("Spend cap reached. Simulation mode", text);
        Assert.DoesNotContain("quota", text);
    }

    [Fact]
    public async Task DemoSessionCannotContactProviderEvenWithConfiguredKey()
    {
        using var app = new TestApp();
        app.Settings["ELEVENLABS_API_KEY"] = "test-elevenlabs-key";
        app.Settings["ELEVENLABS_AGENT_ID"] = "synthetic-agent";
        app.Settings["ELEVENLABS_SESSION_MODE"] = "webrtc";
        var handler = new ElevenLabsFakeHandler { Respond = _ => throw new InvalidOperationException("No network in demo") };
        using var configured = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(sp => new ElevenLabsClient(new(handler), sp.GetRequiredService<ElevenLabsOptions>()))));
        using var client = configured.CreateClient();
        var response = await client.GetAsync("/api/channels/elevenlabs/session");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("simulated", await response.Content.ReadAsStringAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task VoiceSessionCapFallsBackToLabelledSimulatedAgent()
    {
        using var app = new TestApp();
        app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        app.Settings["ELEVENLABS_API_KEY"] = "test-elevenlabs-key";
        app.Settings["ELEVENLABS_AGENT_ID"] = "synthetic-agent";
        app.Settings["ELEVENLABS_SESSION_MODE"] = "signed-url";
        app.Settings["CIVICOPS_VOICE_DAILY_SESSION_CAP"] = "1";
        var handler = new ElevenLabsFakeHandler { Respond = _ => ElevenLabsFakeHandler.Json("{\"signed_url\":\"wss://api.elevenlabs.io/v1/convai/conversation?token=synthetic\"}") };
        using var configured = app.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(sp => new ElevenLabsClient(new(handler), sp.GetRequiredService<ElevenLabsOptions>()))));
        using var client = configured.CreateClient();
        var first = await client.GetFromJsonAsync<JsonElement>("/api/channels/elevenlabs/session");
        Assert.Equal("live", first.GetProperty("mode").GetString());
        var second = await client.GetFromJsonAsync<JsonElement>("/api/channels/elevenlabs/session");
        Assert.Equal("simulated", second.GetProperty("mode").GetString());
        Assert.Equal("voice_cap_reached", second.GetProperty("reason").GetString());
        Assert.Equal("Spend cap reached. Simulation mode", second.GetProperty("label").GetString());
        var caps = await client.GetFromJsonAsync<JsonElement>("/api/spend-caps");
        Assert.True(caps.GetProperty("voiceReached").GetBoolean());
        Assert.Equal("Spend cap reached. Simulation mode", caps.GetProperty("label").GetString());
        Assert.Single(handler.Requests);
    }
}
