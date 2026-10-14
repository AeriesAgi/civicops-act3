using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace CivicOps.Tests;

public sealed class CrewOidcTests
{
    [Theory]
    [InlineData("")] [InlineData("matching-azp")] [InlineData("single-audience-array")]
    public async Task NativePublicClientUsesTrustedJwksSingleUseNonceAndScopedExpiringCredential(string claimShape)
    {
        await using var issuer = await Issuer.StartAsync(); using var app = Configured();
        using var factory = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddHttpClient("crew-oidc").ConfigurePrimaryHttpMessageHandler(() => issuer.Server.CreateHandler())));
        using var client = factory.CreateClient(); var config = await client.GetFromJsonAsync<CrewAuthConfiguration>("/api/v1/crew/auth/config");
        Assert.True(config!.Enabled); Assert.Equal(Issuer.NativeClient, config.NativeClientId); Assert.DoesNotContain("secret", await client.GetStringAsync("/api/v1/crew/auth/config"), StringComparison.OrdinalIgnoreCase);
        var challenge = await CrewAppTests.Post<CrewOidcChallenge>(client, "/api/v1/crew/auth/challenge", new { });
        var request = new CrewOidcRequest(challenge.ChallengeId, issuer.Token(challenge.Nonce, claimShape));
        var issued = await CrewAppTests.Post<CrewTokenIssued>(client, "/api/v1/crew/auth/oidc", request);
        Assert.True(issued.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/crew/auth/oidc", request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", issued.Token);
        Assert.Equal("synthetic-water-01-a", (await client.GetFromJsonAsync<CrewMe>("/api/v1/crew/me"))!.WorkerId);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/workers")).StatusCode);
        Assert.True(issuer.Reads >= 2); Assert.Equal(0, issuer.Writes);
    }
    [Theory]
    [InlineData("nonce", 401)] [InlineData("signature", 401)] [InlineData("audience", 401)] [InlineData("issuer", 401)] [InlineData("expiry", 401)]
    [InlineData("multiple-audiences", 401)] [InlineData("duplicate-audience", 401)] [InlineData("wrong-azp", 401)]
    [InlineData("duplicate-azp", 401)] [InlineData("array-azp", 401)] [InlineData("duplicate-azp-property", 401)]
    [InlineData("role", 403)] [InlineData("tenant", 403)] [InlineData("crew", 403)] [InlineData("worker", 403)] [InlineData("missing-worker", 403)]
    public async Task InvalidOrUnmappedIssuerTokensNeverCreateCrewCredentials(string failure, int expected)
    {
        await using var issuer = await Issuer.StartAsync(); using var app = Configured();
        using var factory = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddHttpClient("crew-oidc").ConfigurePrimaryHttpMessageHandler(() => issuer.Server.CreateHandler())));
        using var client = factory.CreateClient(); var challenge = await CrewAppTests.Post<CrewOidcChallenge>(client, "/api/v1/crew/auth/challenge", new { });
        var request = new CrewOidcRequest(challenge.ChallengeId, issuer.Token(challenge.Nonce, failure));
        Assert.Equal((HttpStatusCode)expected, (await client.PostAsJsonAsync("/api/v1/crew/auth/oidc", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/crew/auth/oidc", request)).StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<CrewStore>().DevicesAsync(default));
        var audit = await factory.Services.GetRequiredService<IncidentStore>().AuditAsync(default);
        Assert.DoesNotContain(audit, value => value.Action == "crew.device-issued");
        Assert.Contains(audit, value => value.Action == "crew.native-sign-in-rejected");
        Assert.DoesNotContain(audit, value => value.Detail.Contains(request.IdToken, StringComparison.Ordinal) || value.Detail.Contains(challenge.Nonce, StringComparison.Ordinal));
        Assert.Equal(0, issuer.Writes);
    }
    [Fact]
    public async Task ExplicitMunicipalityMismatchIsRejectedBeforeAnyDeviceGrantAndConsumesNonce()
    {
        await using var issuer = await Issuer.StartAsync(); using var app = Configured();
        using var factory = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddHttpClient("crew-oidc").ConfigurePrimaryHttpMessageHandler(() => issuer.Server.CreateHandler())));
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<TenantStore>().SaveAsync(new("synthetic-coast", "Synthetic coast municipality", "coast",
            ["Water and Sanitation"], new(), "Synthetic native tenant isolation test"), "Synthetic supervisor", default);
        var store = factory.Services.GetRequiredService<CrewStore>(); var audit = factory.Services.GetRequiredService<IncidentStore>();
        var original = await audit.AuditAsync(default); Assert.Empty(await store.DevicesAsync(default));
        var challenge = await CrewAppTests.Post<CrewOidcChallenge>(client, "/api/v1/crew/auth/challenge", new { });
        var request = new CrewOidcRequest(challenge.ChallengeId, issuer.Token(challenge.Nonce));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/m/coast/api/v1/crew/auth/oidc", request)).StatusCode);
        Assert.Empty(await store.DevicesAsync(default)); Assert.Equal(original, await audit.AuditAsync(default));
        using (TenantContext.Use("synthetic-coast"))
        {
            Assert.Empty(await store.DevicesAsync(default));
            var denied = await audit.AuditAsync(default); Assert.Contains(denied, value => value.Action == "crew.native-sign-in-rejected");
            Assert.DoesNotContain(denied, value => value.Action == "crew.device-issued");
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/crew/auth/oidc", request)).StatusCode);
        Assert.Empty(await store.DevicesAsync(default)); Assert.Equal(0, issuer.Writes);
    }
    [Fact]
    public async Task UnconfiguredNativeOidcIsHonestlyDisabledAndNeverContactsAnIssuer()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); Assert.False((await client.GetFromJsonAsync<CrewAuthConfiguration>("/api/v1/crew/auth/config"))!.Enabled);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/crew/auth/challenge", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/v1/crew/auth/oidc", new CrewOidcRequest("synthetic-challenge", "synthetic-invalid-token-value-no-live-identity"))).StatusCode);
    }
    private static TestApp Configured()
    {
        var app = new TestApp { EnableBackgroundDelivery = false }; app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        app.Settings["CIVICOPS_OIDC_PROVIDER"] = "generic"; app.Settings["CIVICOPS_OIDC_AUTHORITY"] = Issuer.Authority;
        app.Settings["CIVICOPS_OIDC_CLIENT_ID"] = "synthetic-browser-client"; app.Settings["CIVICOPS_OIDC_CLIENT_SECRET"] = "synthetic-browser-client-secret";
        app.Settings["CIVICOPS_OIDC_ROLE_MAP"] = "CouncilCrew=crew;CouncilSupervisor=supervisor"; app.Settings["CIVICOPS_CREW_OIDC_CLIENT_ID"] = Issuer.NativeClient; return app;
    }
    private sealed class Issuer(WebApplication application) : IAsyncDisposable
    {
        internal const string Authority = "https://crew-issuer.synthetic";
        internal const string NativeClient = "synthetic-public-native-client";
        private readonly RSA _key = RSA.Create(2048);
        private readonly RSA _wrongKey = RSA.Create(2048);
        internal TestServer Server => application.GetTestServer();
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal static async Task<Issuer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders(); var app = builder.Build(); var issuer = new Issuer(app);
            app.Use(async (context, next) => { if (context.Request.Method == "GET") issuer.Reads++; else issuer.Writes++; await next(context); });
            app.MapGet("/.well-known/openid-configuration", () => new { issuer = Authority, jwks_uri = Authority + "/jwks", authorization_endpoint = Authority + "/authorize", token_endpoint = Authority + "/token" });
            app.MapGet("/jwks", () => { var key = issuer._key.ExportParameters(false); return new { keys = new[] { new { kty = "RSA", kid = "synthetic-crew-key", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) } } }; });
            await app.StartAsync(); return issuer;
        }
        internal string Token(string nonce, string failure = "")
        {
            var now = DateTime.UtcNow;
            var claims = new List<Claim> { new("sub", "synthetic-crew-subject"), new("name", "Synthetic named field worker"), new("nonce", failure == "nonce" ? "wrong-synthetic-nonce" : nonce),
                new("roles", failure == "role" ? "CouncilSupervisor" : "CouncilCrew"), new("municipality_id", failure == "tenant" ? "unknown-municipality" : "default"),
                new("crew_id", failure == "crew" ? "roads-01" : "water-01") };
            if (failure != "missing-worker") claims.Add(new("worker_id", failure == "worker" ? "synthetic-roads-01-a" : "synthetic-water-01-a"));
            var token = new JwtSecurityToken(failure == "issuer" ? "https://wrong.synthetic" : Authority,
                failure == "audience" ? "synthetic-browser-client" : NativeClient, claims, failure == "expiry" ? now.AddMinutes(-20) : now.AddMinutes(-1),
                failure == "expiry" ? now.AddMinutes(-10) : now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(failure == "signature" ? _wrongKey : _key) { KeyId = "synthetic-crew-key" }, SecurityAlgorithms.RsaSha256));
            if (failure == "single-audience-array") token.Payload["aud"] = new[] { NativeClient };
            if (failure == "multiple-audiences") token.Payload["aud"] = new[] { NativeClient, "synthetic-untrusted-client" };
            if (failure == "duplicate-audience") token.Payload["aud"] = new[] { NativeClient, NativeClient };
            if (failure == "matching-azp" || failure == "duplicate-azp-property") token.Payload["azp"] = NativeClient;
            if (failure == "wrong-azp") token.Payload["azp"] = "synthetic-untrusted-client";
            if (failure == "duplicate-azp") token.Payload["azp"] = new[] { NativeClient, NativeClient };
            if (failure == "array-azp") token.Payload["azp"] = new[] { NativeClient };
            if (failure == "duplicate-azp-property")
            {
                var raw = token.Payload.SerializeToJson().Replace("\"azp\":\"" + NativeClient + "\"", "\"azp\":\"" + NativeClient + "\",\"azp\":\"" + NativeClient + "\"", StringComparison.Ordinal);
                var input = Base64UrlEncoder.Encode(token.Header.SerializeToJson()) + "." + Base64UrlEncoder.Encode(raw);
                return input + "." + Base64UrlEncoder.Encode(_key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        public async ValueTask DisposeAsync() { await application.DisposeAsync(); _key.Dispose(); _wrongKey.Dispose(); }
    }
}
