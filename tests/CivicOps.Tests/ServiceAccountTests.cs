using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;

namespace CivicOps.Tests;

public sealed class ServiceAccountTests
{
    [Fact] public async Task CredentialExpiryAndOverlapUseServerTimeAndCannotExtendPreviousExpiry()
    {
        using var app = new TestApp(); var clock = new MutableClock(DateTimeOffset.UtcNow);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_DATA_DIR"] = app.DataDirectory }).Build();
        var store = new ServiceAccountStore(configuration, app.Services.GetRequiredService<IWebHostEnvironment>(), clock, app.Services.GetRequiredService<IncidentStore>());
        var issued = await store.CreateAsync("default", new("synthetic-expiring", ["tools:invoke"], clock.GetUtcNow().AddMinutes(1)), "Synthetic admin");
        var rotated = await store.RotateAsync("default", issued.Account.Id, new(60, clock.GetUtcNow().AddMinutes(10)), "Synthetic admin"); Assert.NotNull(rotated);
        Assert.Equal(issued.Account.ExpiresAt, rotated.Account.PreviousTokenValidUntil);
        clock.Advance(TimeSpan.FromMinutes(2)); Assert.Null(await store.AuthenticateAsync(issued.Token)); Assert.NotNull(await store.AuthenticateAsync(rotated.Token));
        clock.Advance(TimeSpan.FromMinutes(9)); Assert.Null(await store.AuthenticateAsync(rotated.Token));
    }
    [Theory] [InlineData(-1)] [InlineData(61)]
    public async Task InvalidRotationOverlapIsRejected(int minutes)
    {
        using var app = new TestApp(); var store = app.Services.GetRequiredService<ServiceAccountStore>();
        var issued = await store.CreateAsync("default", new("synthetic-tool", ["tools:invoke"], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic admin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RotateAsync("default", issued.Account.Id, new(minutes), "Synthetic admin"));
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task TokensAreShownOnceHashedAtRestAndTenantBound(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); var store = app.Services.GetRequiredService<ServiceAccountStore>();
        var issued = await store.CreateAsync("default", new("synthetic-tool", ["tools:invoke"], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic admin");
        var principal = await store.AuthenticateAsync(issued.Token); Assert.NotNull(principal);
        Assert.True(principal.IsInRole("agent")); Assert.False(principal.IsInRole("admin"));
        Assert.Equal("default", principal.FindFirst("civicops:tenant")?.Value);
        Assert.True(principal.HasClaim("civicops:scope", "tools:invoke"));
        Assert.Empty(await store.ListAsync("synthetic-coast")); Assert.Single(await store.ListAsync("default"));
        var file = (await app.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(app.DataDirectory, "service-accounts.json"), CancellationToken.None))!;
        Assert.DoesNotContain(issued.Token, file, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issued.Token))), file, StringComparison.Ordinal);
        Assert.Null(await store.AuthenticateAsync("wrong-synthetic-token"));
        var audit = await app.Services.GetRequiredService<IncidentStore>().AuditAsync(default);
        Assert.Contains(audit, record => record.Action == "service-account.created");
        Assert.True(AuditLog.Verify(audit).Valid);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task RotationHonorsOverlapThenRevocationDisablesBothTokens(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); var store = app.Services.GetRequiredService<ServiceAccountStore>();
        var first = await store.CreateAsync("default", new("synthetic-training", ["training:read"], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic admin");
        var rotated = await store.RotateAsync("default", first.Account.Id, new(5), "Synthetic admin"); Assert.NotNull(rotated);
        Assert.NotEqual(first.Token, rotated.Token); Assert.NotNull(await store.AuthenticateAsync(first.Token)); Assert.NotNull(await store.AuthenticateAsync(rotated.Token));
        var rotatedAgain = await store.RotateAsync("default", first.Account.Id, new(0), "Synthetic admin"); Assert.NotNull(rotatedAgain);
        Assert.Null(await store.AuthenticateAsync(first.Token)); Assert.Null(await store.AuthenticateAsync(rotated.Token));
        Assert.NotNull(await store.AuthenticateAsync(rotatedAgain.Token));
        await store.RevokeAsync("default", first.Account.Id, "Synthetic admin"); Assert.Null(await store.AuthenticateAsync(rotatedAgain.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RotateAsync("default", first.Account.Id, new(0), "Synthetic admin"));
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task CrossMunicipalityMutationsReturnNotFoundAndKeepOriginalCredential(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); var store = app.Services.GetRequiredService<ServiceAccountStore>();
        var issued = await store.CreateAsync("default", new("synthetic-tool", ["tools:invoke"], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic admin");
        Assert.Null(await store.RotateAsync("synthetic-coast", issued.Account.Id, new(0), "Other synthetic admin"));
        Assert.Null(await store.RevokeAsync("synthetic-coast", issued.Account.Id, "Other synthetic admin"));
        Assert.NotNull(await store.AuthenticateAsync(issued.Token));
    }
    [Theory] [InlineData("admin")] [InlineData("*")] [InlineData("training:admin")] [InlineData("incidents:delete")]
    public async Task UnsupportedOrPrivilegedServiceScopesAreRejected(string scope)
    {
        using var app = new TestApp(); var store = app.Services.GetRequiredService<ServiceAccountStore>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync("default", new("synthetic-invalid", [scope], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic admin"));
    }
    [Theory] [InlineData(-1)] [InlineData(367)]
    public async Task InvalidExpiryIsRejected(int days)
    {
        using var app = new TestApp(); var store = app.Services.GetRequiredService<ServiceAccountStore>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync("default", new("synthetic-invalid", ["tools:invoke"], DateTimeOffset.UtcNow.AddDays(days)), "Synthetic admin"));
    }
    [Fact] public async Task PersistedCredentialAndAuditTamperingFailsClosedOnRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "identity-tamper-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var app = new TestApp { DataDirectory = path, KeepData = true })
                await app.Services.GetRequiredService<ServiceAccountStore>().CreateAsync("default", new("synthetic-tool", ["tools:invoke"], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic admin");
            var file = Path.Combine(path, "service-accounts.json"); var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
            snapshot["accounts"]![0]!["view"]!["scopes"] = new JsonArray("incidents:write"); await File.WriteAllTextAsync(file, snapshot.ToJsonString());
            using var restarted = new TestApp { DataDirectory = path, KeepData = true };
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.Services.GetRequiredService<ServiceAccountStore>().ListAsync("default"));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task BootstrapUsersAreHashedTenantBoundRoleMutableAndRevocable(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); var users = app.Services.GetRequiredService<BootstrapUserStore>();
        var issued = await users.CreateAsync("default", new("synthetic-operator", "Synthetic Operator", ["dispatcher"]), "Synthetic admin");
        Assert.True((await users.AuthenticateAsync(issued.Token))!.IsInRole("dispatcher"));
        Assert.Null(await users.UpdateAsync("synthetic-coast", issued.User.Id, new("Other name", ["admin"]), "Other admin"));
        Assert.Null(await users.RevokeAsync("synthetic-coast", issued.User.Id, "Other admin"));
        await users.UpdateAsync("default", issued.User.Id, new("Synthetic Operator", ["auditor"]), "Synthetic admin");
        var principal = await users.AuthenticateAsync(issued.Token); Assert.True(principal!.IsInRole("auditor")); Assert.False(principal.IsInRole("dispatcher"));
        var rotated = await users.RotateAsync("default", issued.User.Id, "Synthetic admin"); Assert.NotNull(rotated);
        Assert.Null(await users.AuthenticateAsync(issued.Token)); Assert.NotNull(await users.AuthenticateAsync(rotated.Token));
        Assert.DoesNotContain(rotated.Token, (await app.Services.GetRequiredService<DurableStorage>().ReadAsync(Path.Combine(app.DataDirectory, "bootstrap-users.json"), CancellationToken.None))!, StringComparison.Ordinal);
        await users.RevokeAsync("default", issued.User.Id, "Synthetic admin"); Assert.Null(await users.AuthenticateAsync(rotated.Token));
    }
    [Theory] [InlineData("not-a-sha256")] [InlineData("aaaaaaaa")]
    public void BootstrapConfigurationRequiresHash(string value)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => { BootstrapUserStore.ValidateConfiguredAdmin(configuration); });
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task BootstrapLoginRequiresCsrfIssuesSecureCookieAndEnforcesReadOnlyAuditor(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); var issued = await app.Services.GetRequiredService<BootstrapUserStore>()
            .CreateAsync("default", new("synthetic-auditor", "Synthetic Auditor", ["auditor"]), "Synthetic admin");
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/bootstrap-login", new { token = issued.Token })).StatusCode);
        var status = await client.GetFromJsonAsync<JsonElement>("/auth/status"); client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        var login = await client.PostAsJsonAsync("/auth/bootstrap-login", new { token = issued.Token }); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = string.Join(";", login.Headers.GetValues("Set-Cookie")); Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase); Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Synthetic Auditor", cookie, StringComparison.Ordinal); Assert.DoesNotContain(issued.Token, cookie, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/incidents/unknown/approve", new { expectedVersion = 1 })).StatusCode);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ConfiguredBootstrapAdminCanEstablishFirstLocalAdministratorWithoutSso(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        var token = "synthetic-bootstrap-admin-token-for-local-test";
        app.Settings["CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var status = await client.GetFromJsonAsync<JsonElement>("/auth/status"); client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/bootstrap-login", new { token })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/users")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        status = await client.GetFromJsonAsync<JsonElement>("/auth/status"); client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/auth/demo-login", new { role = "admin" })).StatusCode);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ExistingBootstrapSessionTracksRolesAndIsInvalidatedByRotationAndRevocation(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false"; var users = app.Services.GetRequiredService<BootstrapUserStore>();
        var issued = await users.CreateAsync("default", new("synthetic-operator", "Synthetic Operator", ["dispatcher"]), "Synthetic admin");
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var status = await client.GetFromJsonAsync<JsonElement>("/auth/status"); client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/bootstrap-login", new { token = issued.Token })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/incidents")).StatusCode);
        await users.UpdateAsync("default", issued.User.Id, new("Synthetic Operator", ["resident"]), "Synthetic admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/incidents")).StatusCode);
        var rotated = await users.RotateAsync("default", issued.User.Id, "Synthetic admin"); Assert.NotNull(rotated);
        status = await client.GetFromJsonAsync<JsonElement>("/auth/status"); Assert.False(status.GetProperty("authenticated").GetBoolean());
        client.DefaultRequestHeaders.Remove("X-CivicOps-CSRF"); client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/bootstrap-login", new { token = rotated.Token })).StatusCode);
        await users.RevokeAsync("default", issued.User.Id, "Synthetic admin");
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/auth/status")).GetProperty("authenticated").GetBoolean());
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task AdminIdentityReadAndWriteRoutesCannotAccessAnotherMunicipalityOrAcceptBodyTenant(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); var accounts = app.Services.GetRequiredService<ServiceAccountStore>(); var users = app.Services.GetRequiredService<BootstrapUserStore>();
        var account = await accounts.CreateAsync("default", new("default-synthetic-tool", ["tools:invoke"], DateTimeOffset.UtcNow.AddDays(1)), "Default synthetic admin");
        var user = await users.CreateAsync("default", new("default-synthetic-staff", "Default Synthetic Staff", ["dispatcher"]), "Default synthetic admin");
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var status = await client.GetFromJsonAsync<JsonElement>("/auth/status"); client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/demo-login", new { role = "admin", tenantId = "synthetic-coast" })).StatusCode);
        status = await client.GetFromJsonAsync<JsonElement>("/auth/status"); client.DefaultRequestHeaders.Remove("X-CivicOps-CSRF"); client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/admin/service-accounts")).EnumerateArray());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/admin/users")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/admin/service-accounts/{account.Account.Id}/rotate", new { overlapMinutes = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/admin/service-accounts/{account.Account.Id}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/admin/users/{user.User.Id}", new { displayName = "Other synthetic name", roles = new[] { "admin" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/admin/users/{user.User.Id}/rotate", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/admin/users/{user.User.Id}/revoke", new { })).StatusCode);
        var create = await client.PostAsJsonAsync("/api/admin/service-accounts", new { name = "coast-synthetic-tool", scopes = new[] { "tools:invoke" }, expiresAt = DateTimeOffset.UtcNow.AddDays(1), tenantId = "default" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.Equal("synthetic-coast", (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("account").GetProperty("tenantId").GetString());
        Assert.NotNull(await accounts.AuthenticateAsync(account.Token)); Assert.NotNull(await users.AuthenticateAsync(user.Token));
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ScopedServiceAccountBearerUsesMatrixWithoutAcquiringAdminOrWriteAccess(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false"; var store = app.Services.GetRequiredService<ServiceAccountStore>();
        var issued = await store.CreateAsync("default", new("synthetic-integration-reader", ["incidents:read"], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic admin");
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", issued.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/incidents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic incident write attempt" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/service-accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/training/skills")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/channels/elevenlabs/tools/list_service_categories", new { })).StatusCode);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ServiceAccountToolsKeepBoundedPerConversationQuotasForSharedProviderIps(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false"; app.Settings["CIVICOPS_INTAKE_LIMIT"] = "1";
        var issued = await app.Services.GetRequiredService<ServiceAccountStore>().CreateAsync("default", new("synthetic-provider-tools", ["tools:invoke"], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic admin");
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", issued.Token);
        client.DefaultRequestHeaders.Add("X-CivicOps-Conversation-Id", "first-synthetic-conversation");
        const string route = "/api/channels/elevenlabs/tools/list_service_categories";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CivicOps-Conversation-Id"); client.DefaultRequestHeaders.Add("X-CivicOps-Conversation-Id", "second-synthetic-conversation");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(route, new { })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CivicOps-Conversation-Id"); client.DefaultRequestHeaders.Add("X-CivicOps-Conversation-Id", new string('x', 129));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { })).StatusCode);
    }
    private sealed class MutableClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
