using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CivicOps.Services;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace CivicOps.Tests;

public sealed class OidcIntegrationTests
{
    [Fact]
    public async Task RealOidcHandlerExchangesPkceCodeValidatesJwksMapsRolesAndEnforcesPolicies()
    {
        await using var issuer = await FakeIssuer.StartAsync("CouncilAuditor", "default");
        using var app = new TestApp(); Configure(app);
        using var factory = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>(EnterpriseIdentity.OidcScheme, options => options.Backchannel = issuer.Client)));
        using var browser = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var login = await browser.GetAsync("/auth/login?returnUrl=/"); Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var authorization = login.Headers.Location!;
        var query = QueryHelpers.ParseQuery(authorization.Query);
        Assert.Equal("code", query["response_type"]); Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"])); Assert.False(string.IsNullOrWhiteSpace(query["nonce"]));
        var consent = await issuer.Client.GetAsync(authorization.PathAndQuery); Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        var callback = await browser.GetAsync(consent.Headers.Location!.PathAndQuery); Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var cookies = string.Join(';', callback.Headers.GetValues("Set-Cookie"));
        Assert.Contains("__Host-CivicOps-Session=", cookies, StringComparison.Ordinal);
        Assert.Contains("secure", cookies, StringComparison.OrdinalIgnoreCase); Assert.Contains("httponly", cookies, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookies, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("CouncilAuditor", cookies, StringComparison.Ordinal);
        var status = await browser.GetFromJsonAsync<JsonElement>("/auth/status"); Assert.True(status.GetProperty("authenticated").GetBoolean());
        Assert.Equal("default", status.GetProperty("tenantId").GetString()); Assert.True(status.GetProperty("oidcSession").GetBoolean());
        Assert.Equal("auditor", status.GetProperty("roles")[0].GetString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsJsonAsync("/api/incidents/unknown/approve", new { expectedVersion = 1 })).StatusCode);
        Assert.True(issuer.DiscoveryRequests > 0); Assert.True(issuer.JwksRequests > 0); Assert.Equal(1, issuer.TokenRequests); Assert.True(issuer.PkceVerified);
        browser.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        var logout = await browser.PostAsync("/auth/logout", new StringContent("")); Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.StartsWith(FakeIssuer.Authority + "/logout", logout.Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(QueryHelpers.ParseQuery(logout.Headers.Location.Query)["id_token_hint"]));
        Assert.False((await browser.GetFromJsonAsync<JsonElement>("/auth/status")).GetProperty("authenticated").GetBoolean());
    }
    [Theory] [InlineData("UnmappedRole", "default")] [InlineData("CouncilAuditor", "unknown-municipality")]
    public async Task ValidlySignedButUnmappedOrUnknownTenantIdentityCannotSignIn(string role, string tenant)
    {
        await using var issuer = await FakeIssuer.StartAsync(role, tenant); using var app = new TestApp(); Configure(app);
        using var factory = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>(EnterpriseIdentity.OidcScheme, options => options.Backchannel = issuer.Client)));
        using var browser = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var login = await browser.GetAsync("/auth/login"); var consent = await issuer.Client.GetAsync(login.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(consent.Headers.Location!.PathAndQuery)).StatusCode);
        Assert.False((await browser.GetFromJsonAsync<JsonElement>("/auth/status")).GetProperty("authenticated").GetBoolean());
    }
    [Fact] public async Task ExternalReturnUrlCannotBeUsedForLoginRedirect()
    {
        using var app = new TestApp(); Configure(app); using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/auth/login?returnUrl=https%3A%2F%2Fother.synthetic")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/auth/login?returnUrl=%2F%2Fother.synthetic")).StatusCode);
    }
    [Theory] [InlineData(true, false)] [InlineData(false, true)]
    public async Task InvalidNonceOrSignatureCannotProduceStaffSession(bool invalidNonce, bool invalidSignature)
    {
        await using var issuer = await FakeIssuer.StartAsync("CouncilAuditor", "default", invalidNonce, invalidSignature); using var app = new TestApp(); Configure(app);
        using var factory = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>(EnterpriseIdentity.OidcScheme, options => options.Backchannel = issuer.Client)));
        using var browser = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var login = await browser.GetAsync("/auth/login"); var consent = await issuer.Client.GetAsync(login.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(consent.Headers.Location!.PathAndQuery)).StatusCode);
        Assert.False((await browser.GetFromJsonAsync<JsonElement>("/auth/status")).GetProperty("authenticated").GetBoolean());
    }
    private static void Configure(TestApp app)
    {
        app.Settings["CIVICOPS_DEMO_MODE"] = "false"; app.Settings["CIVICOPS_OIDC_PROVIDER"] = "generic";
        app.Settings["CIVICOPS_OIDC_AUTHORITY"] = FakeIssuer.Authority; app.Settings["CIVICOPS_OIDC_CLIENT_ID"] = FakeIssuer.ClientId;
        app.Settings["CIVICOPS_OIDC_CLIENT_SECRET"] = "synthetic-test-client-secret"; app.Settings["CIVICOPS_OIDC_ROLE_MAP"] = "CouncilAuditor=auditor;CouncilDispatcher=dispatcher";
    }

    /// <summary>Entire issuer is an in-process TestServer. No provider network or provider write API is used.</summary>
    private sealed class FakeIssuer : IAsyncDisposable
    {
        public const string Authority = "https://issuer.synthetic";
        public const string ClientId = "synthetic-client";
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly ConcurrentDictionary<string, (string Nonce, string Challenge)> _codes = new();
        private readonly WebApplication _server;
        public HttpClient Client { get; private set; } = null!;
        public int DiscoveryRequests { get; private set; }
        public int JwksRequests { get; private set; }
        public int TokenRequests { get; private set; }
        public bool PkceVerified { get; private set; }
        private FakeIssuer(WebApplication server, string role, string tenant, bool invalidNonce, bool invalidSignature)
        {
            _server = server;
            var key = new RsaSecurityKey(_rsa) { KeyId = "synthetic-key-1" }; var parameters = _rsa.ExportParameters(false);
            server.MapGet("/.well-known/openid-configuration", () =>
            {
                DiscoveryRequests++;
                return Microsoft.AspNetCore.Http.Results.Json(new { issuer = Authority, authorization_endpoint = Authority + "/authorize", token_endpoint = Authority + "/token",
                    jwks_uri = Authority + "/jwks", end_session_endpoint = Authority + "/logout", response_types_supported = new[] { "code" },
                    subject_types_supported = new[] { "public" }, id_token_signing_alg_values_supported = new[] { "RS256" }, code_challenge_methods_supported = new[] { "S256" } });
            });
            server.MapGet("/jwks", () =>
            {
                JwksRequests++;
                return Microsoft.AspNetCore.Http.Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", kid = key.KeyId, alg = "RS256", n = WebEncoders.Base64UrlEncode(parameters.Modulus!), e = WebEncoders.Base64UrlEncode(parameters.Exponent!) } } });
            });
            server.MapGet("/authorize", (Microsoft.AspNetCore.Http.HttpRequest request) =>
            {
                var code = "synthetic-code-" + Guid.NewGuid().ToString("N"); _codes[code] = (request.Query["nonce"].ToString(), request.Query["code_challenge"].ToString());
                return Microsoft.AspNetCore.Http.Results.Redirect(QueryHelpers.AddQueryString(request.Query["redirect_uri"].ToString(), new Dictionary<string, string?> { ["code"] = code, ["state"] = request.Query["state"].ToString() }));
            });
            server.MapPost("/token", async (Microsoft.AspNetCore.Http.HttpRequest request) =>
            {
                TokenRequests++; var form = await request.ReadFormAsync();
                if (!_codes.TryRemove(form["code"].ToString(), out var saved) || form["grant_type"] != "authorization_code"
                    || WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))) != saved.Challenge)
                    return Microsoft.AspNetCore.Http.Results.BadRequest(new { error = "invalid_grant" });
                PkceVerified = true;
                var token = new JwtSecurityToken(Authority, ClientId, [new Claim("sub", "synthetic-oidc-staff-1"), new Claim("name", "Synthetic OIDC Staff"),
                    new Claim("nonce", invalidNonce ? "invalid-synthetic-nonce" : saved.Nonce), new Claim("municipality_id", tenant), new Claim("roles", role),
                    new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)],
                    DateTime.UtcNow.AddSeconds(-5), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
                var encoded = new JwtSecurityTokenHandler().WriteToken(token);
                if (invalidSignature)
                {
                    var parts = encoded.Split('.'); parts[2] = (parts[2][0] == 'A' ? "B" : "A") + parts[2][1..]; encoded = string.Join('.', parts);
                }
                return Microsoft.AspNetCore.Http.Results.Json(new { access_token = "synthetic-local-access-token", token_type = "Bearer", expires_in = 300, id_token = encoded });
            });
        }
        public static async Task<FakeIssuer> StartAsync(string role, string tenant, bool invalidNonce = false, bool invalidSignature = false)
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
            var server = builder.Build(); var issuer = new FakeIssuer(server, role, tenant, invalidNonce, invalidSignature); await server.StartAsync();
            issuer.Client = server.GetTestClient(); issuer.Client.BaseAddress = new Uri(Authority); return issuer;
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await _server.DisposeAsync(); _rsa.Dispose(); }
    }
}
