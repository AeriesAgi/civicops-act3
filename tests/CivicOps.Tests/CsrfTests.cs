using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class CsrfTests
{
    [Theory]
    [InlineData("POST", "/api/admin/users")]
    [InlineData("PUT", "/api/admin/users/synthetic-unknown")]
    [InlineData("PATCH", "/api/incidents/synthetic-unknown/status")]
    [InlineData("POST", "/auth/logout")]
    public async Task CookieMutationsRejectMissingTokensWithSafeProblemDetails(string method, string path)
    {
        using var app = new TestApp(); using var admin = await RbacTests.RoleClient(app, "admin");
        admin.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { }) };
        using var response = await admin.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.Equal("Request verification failed", body.GetProperty("title").GetString());
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.DoesNotContain("AntiforgeryValidationException", body.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DifferentBrowserTokenDoesNotAuthorizeSessionMutation()
    {
        using var app = new TestApp(); using var admin = await RbacTests.RoleClient(app, "admin");
        using var other = await RbacTests.RoleClient(app, "admin");
        admin.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        admin.DefaultRequestHeaders.Add("X-CivicOps-CSRF", other.DefaultRequestHeaders.GetValues("X-CivicOps-CSRF").Single());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/auth/logout", null)).StatusCode);
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/auth/status")).GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task PreLoginTokenCannotAuthorizeTheNewAuthenticatedIdentity()
    {
        using var app = new TestApp(); using var client = app.CreateClient(new() { BaseAddress = new("https://localhost") });
        var status = await client.GetFromJsonAsync<JsonElement>("/auth/status");
        client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/demo-login", new { role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout", null)).StatusCode);
        status = await client.GetFromJsonAsync<JsonElement>("/auth/status");
        client.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task LoginRejectsMissingOrForgedTokensBeforeIssuingSession()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/demo-login", new { role = "admin" })).StatusCode);
        client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", "synthetic-forged-antiforgery-token");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/demo-login", new { role = "admin" })).StatusCode);
    }

    [Fact]
    public async Task ExplicitServiceBearerTakesPrecedenceOverCookieAndNeedsNoCsrf()
    {
        using var app = new TestApp(); using var admin = await RbacTests.RoleClient(app, "admin");
        var service = await app.Services.GetRequiredService<ServiceAccountStore>().CreateAsync("default",
            new("Synthetic tool service", ["tools:invoke"], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic administrator");
        admin.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", service.Token);
        using var response = await admin.PostAsJsonAsync("/api/channels/elevenlabs/tools/list_service_categories",
            new { conversation_id = "synthetic-csrf-conversation", turn_id = "synthetic-csrf-turn" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Bearer scopes still control access even when a privileged cookie is present.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/admin/users",
            new { subject = "synthetic", displayName = "Synthetic staff", roles = new[] { "auditor" } })).StatusCode);
    }
}
