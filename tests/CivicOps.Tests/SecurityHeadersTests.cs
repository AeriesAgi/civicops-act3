using System.Net;
using System.Net.Http.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.AspNetCore.Http;

namespace CivicOps.Tests;

public sealed class SecurityHeadersTests
{
    [Theory]
    [InlineData("/crew/")]
    [InlineData("/crew/index.html")]
    [InlineData("/m/coast/crew/")]
    public async Task CrewShellHasMatchingNonceAndNarrowDevicePermissions(string path)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        using var response = await client.GetAsync(path); response.EnsureSuccessStatusCode();
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        var start = policy.IndexOf("'nonce-", StringComparison.Ordinal) + 7;
        var nonce = policy[start..policy.IndexOf('\'', start)];
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("src=\"/crew/crew.js\" nonce=\"" + nonce + "\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("CIVICOPS_SCRIPT_NONCE", html, StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("img-src 'self' blob:", policy, StringComparison.Ordinal);
        var permissions = Assert.Single(response.Headers.GetValues("Permissions-Policy"));
        Assert.Contains("camera=(self), geolocation=(self)", permissions, StringComparison.Ordinal);
        Assert.Contains("microphone=()", permissions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/crew-other/")]
    [InlineData("/api/v1/crew/locations")]
    public async Task OtherPathsKeepCameraAndLocationDisabled(string path)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        using var response = await client.GetAsync(path);
        var permissions = Assert.Single(response.Headers.GetValues("Permissions-Policy"));
        Assert.Contains("camera=(), geolocation=()", permissions, StringComparison.Ordinal);
        Assert.DoesNotContain("img-src 'self' blob:", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HtmlShellUsesFreshMatchingScriptNoncesIncludingMunicipalityPaths()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var nonces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in new[] { "/", "/index.html", "/m/coast/", "/m/coast/index.html" })
        {
            using var response = await client.GetAsync(path); response.EnsureSuccessStatusCode();
            var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
            var start = policy.IndexOf("'nonce-", StringComparison.Ordinal) + 7;
            var nonce = policy[start..policy.IndexOf('\'', start)];
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("src=\"/app.js\" nonce=\"" + nonce + "\"", html, StringComparison.Ordinal);
            Assert.Contains("src=\"/admin.js\" nonce=\"" + nonce + "\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("CIVICOPS_SCRIPT_NONCE", html, StringComparison.Ordinal);
            Assert.True(nonces.Add(nonce)); Assert.Equal(24, Convert.FromBase64String(nonce).Length);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/app.css")]
    [InlineData("/offline.html")]
    [InlineData("/healthz")]
    [InlineData("/api/incidents/unknown-synthetic-case")]
    [InlineData("/not-a-route")]
    public async Task StaticApiAndErrorRepliesCarryStrictSecurityHeaders(string path)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        using var response = await client.GetAsync(path);
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'nonce-", policy, StringComparison.Ordinal);
        Assert.Contains("style-src 'self';", policy, StringComparison.Ordinal);
        Assert.Contains("script-src-attr 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("style-src-attr 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("elevenlabs", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("livekit", policy, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("microphone=()", Assert.Single(response.Headers.GetValues("Permissions-Policy")), StringComparison.Ordinal);
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Opener-Policy")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task AuthenticationAndRateLimitRejectionsRetainSecurityHeaders()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false"; app.Settings["CIVICOPS_INTAKE_LIMIT"] = "1";
        using var client = app.CreateClient();
        using var unauthorized = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.True(unauthorized.Headers.Contains("Content-Security-Policy"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/open311/v2/services")).StatusCode);
        using var limited = await client.GetAsync("/open311/v2/services");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("https://api.elevenlabs.io", "global")]
    [InlineData("https://api.us.elevenlabs.io", "us")]
    [InlineData("https://api.eu.residency.elevenlabs.io", "eu-residency")]
    [InlineData("https://api.in.residency.elevenlabs.io", "in-residency")]
    public async Task PublicWidgetSessionUsesTheConfiguredProviderRegionWithoutProviderCalls(string api, string region)
    {
        using var app = new TestApp();
        app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        app.Settings["ELEVENLABS_API_KEY"] = "synthetic-public-region-key";
        app.Settings["ELEVENLABS_AGENT_ID"] = "synthetic-public-region-agent";
        app.Settings["ELEVENLABS_SESSION_MODE"] = "public";
        app.Settings["ELEVENLABS_API_BASE"] = api;
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/api/channels/elevenlabs/session"); response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(region, body.GetProperty("serverLocation").GetString());
        Assert.Contains(api, Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-public-region-key", body.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "https", false)]
    [InlineData(true, "http", false)]
    [InlineData(true, "https", true)]
    public void HstsRequiresProductionHttps(bool production, string scheme, bool expected)
    {
        var http = new DefaultHttpContext(); http.Request.Scheme = scheme;
        SecurityHeaders.Apply(http, production, new());
        Assert.Equal(expected, http.Response.Headers.ContainsKey("Strict-Transport-Security"));
        if (expected) Assert.Equal("max-age=31536000; includeSubDomains", http.Response.Headers["Strict-Transport-Security"].ToString());
    }

    [Theory]
    [InlineData("public", "https://unpkg.com/@elevenlabs/convai-widget-embed@0.19.0/dist/index.js")]
    [InlineData("signed-url", "https://cdn.jsdelivr.net/npm/@elevenlabs/client@1.27.0/dist/lib.iife.js")]
    [InlineData("webrtc", "https://cdn.jsdelivr.net/npm/@elevenlabs/client@1.27.0/dist/lib.iife.js")]
    public void ConfiguredVoiceOnlyPermitsTheSelectedProviderChannel(string mode, string expectedScript)
    {
        var options = new ElevenLabsOptions { ApiKey = "synthetic-test-provider-key", AgentId = "synthetic-agent", SessionMode = mode };
        var http = new DefaultHttpContext(); SecurityHeaders.Apply(http, false, options);
        var policy = http.Response.Headers["Content-Security-Policy"].ToString();
        Assert.Contains(expectedScript, policy, StringComparison.Ordinal);
        Assert.Contains("https://api.elevenlabs.io wss://api.elevenlabs.io", policy, StringComparison.Ordinal);
        Assert.Equal(mode is "public" or "webrtc", policy.Contains("*.livekit.cloud", StringComparison.Ordinal));
        Assert.Equal(mode == "public", policy.Contains("unpkg.com", StringComparison.Ordinal));
        Assert.Equal(mode != "public", policy.Contains("@elevenlabs/client", StringComparison.Ordinal));
        Assert.Contains("microphone=(self)", http.Response.Headers["Permissions-Policy"].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(options.ApiKey, policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "synthetic-test-provider-key", "synthetic-agent")]
    [InlineData(false, "", "synthetic-agent")]
    [InlineData(false, "synthetic-test-provider-key", "")]
    public void DemoMissingKeyOrMissingAgentNeverEnableExternalOrigins(bool demo, string key, string agent)
    {
        var options = new ElevenLabsOptions { DemoMode = demo, ApiKey = key, AgentId = agent };
        Assert.DoesNotContain("https:", SecurityHeaders.ContentSecurityPolicy(options, "synthetic-csp-nonce"), StringComparison.Ordinal);
        var http = new DefaultHttpContext(); SecurityHeaders.Apply(http, false, options);
        Assert.Contains("microphone=()", http.Response.Headers["Permissions-Policy"].ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.elevenlabs.io; https://attacker.invalid")]
    [InlineData("https://user:synthetic-sentinel@api.elevenlabs.io")]
    [InlineData("https://attacker.invalid")]
    [InlineData("https://api.elevenlabs.io.attacker.invalid")]
    [InlineData("https://api.elevenlabs.io:444")]
    [InlineData("https://api.elevenlabs.io?key=synthetic-sentinel")]
    public void UntrustedApiConfigurationCannotInjectCspOriginsOrCredentials(string api)
    {
        var options = new ElevenLabsOptions { ApiKey = "synthetic-test-provider-key", AgentId = "synthetic-agent", ApiBase = api };
        var policy = SecurityHeaders.ContentSecurityPolicy(options, "synthetic-csp-nonce");
        Assert.DoesNotContain("attacker", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("sentinel", policy, StringComparison.Ordinal);
        Assert.DoesNotContain(":444", policy, StringComparison.Ordinal);
    }
}
