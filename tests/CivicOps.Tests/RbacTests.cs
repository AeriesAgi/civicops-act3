using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using CivicOps.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CivicOps.Tests;

public sealed class RbacTests
{
    [Fact]
    public void PublishedPermissionTableMatchesTheOnlyCodeMatrix()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var rows = File.ReadAllLines(Path.Combine(root, "docs/RBAC.md")).Where(line => line.StartsWith("| ", StringComparison.Ordinal))
            .Select(line => line.Split('|', StringSplitOptions.TrimEntries)).Where(cells => cells.Length == 11).ToArray();
        foreach (var (policy, roles) in PermissionMatrix.Permissions)
        {
            var row = Assert.Single(rows, row => row[1] == policy);
            for (var index = 0; index < PermissionMatrix.Roles.Length; index++)
                Assert.Equal(roles.Contains(PermissionMatrix.Roles[index]) ? "✓" : "—", row[index + 2]);
        }
    }
    [Fact]
    public async Task EveryMappedEndpointHasOneExplicitPolicyAndEveryRoleMatchesTheMatrix()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        var authorization = app.Services.GetRequiredService<IAuthorizationService>();
        var seen = new HashSet<string>();
        Assert.True(endpoints.Length >= 70);
        foreach (var endpoint in endpoints)
        {
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());
            var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy;
            Assert.False(string.IsNullOrWhiteSpace(policy));
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                var key = method + " " + endpoint.RoutePattern.RawText!.TrimEnd('/'); seen.Add(key);
                Assert.Equal(PermissionMatrix.Endpoints[key], policy);
                foreach (var role in PermissionMatrix.Roles)
                {
                    var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Name, "Synthetic role " + role), new(ClaimTypes.Role, role), new("civicops:tenant", "default")], "civicops-session"));
                    var allowed = (await authorization.AuthorizeAsync(principal, endpoint, policy!)).Succeeded;
                    Assert.Equal(PermissionMatrix.AllowsRole(policy!, role), allowed);
                }
            }
        }
        Assert.Equal(PermissionMatrix.Endpoints.Keys.Order(), seen.Order());
        var fallback = await app.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();
        Assert.NotNull(fallback);
        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(), null, fallback)).Succeeded);
    }

    [Theory]
    [InlineData("resident", "/api/incidents")]
    [InlineData("agent", "/api/audit")]
    [InlineData("dispatcher", "/api/audit")]
    [InlineData("crew", "/api/metrics")]
    [InlineData("auditor", "/api/admin/overview")]
    [InlineData("supervisor", "/api/admin/users")]
    [InlineData("training-client", "/api/incidents")]
    public async Task RestrictedRoleCannotReadProtectedResources(string role, string route)
    {
        using var app = new TestApp(); using var client = await RoleClient(app, role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(route)).StatusCode);
    }

    [Theory]
    [InlineData("resident")][InlineData("agent")][InlineData("crew")][InlineData("auditor")][InlineData("training-client")]
    public async Task NonDispatchRolesCannotApproveOrAssign(string role)
    {
        using var app = new TestApp(); using var client = await RoleClient(app, role);
        foreach (var action in new[] { "approve", "assign", "work-order-type", "comms/fake/approve", "callback" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/incidents/fake/" + action, new { expectedVersion = 1, crewId = "water-crew", note = "Synthetic review", workOrderTypeCode = "WATER.LEAK" })).StatusCode);
    }

    [Fact]
    public async Task DispatcherNeedsSupervisorForHighHazardAndAllOverrides()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var intake = app.CreateClient();
        var response = await intake.PostAsJsonAsync("/api/incidents", new { description = "Synthetic exposed live wire sparks near a flooded electricity box.", area = "Synthetic high hazard area" });
        response.EnsureSuccessStatusCode(); using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = payload.RootElement.GetProperty("incident").GetProperty("id").GetString();
        using var dispatcher = await RoleClient(app, "dispatcher");
        foreach (var action in new[] { "approve", "assign", "work-order-type" })
            Assert.Equal(HttpStatusCode.Forbidden, (await dispatcher.PostAsJsonAsync($"/api/incidents/{id}/{action}", new { crewId = "electricity-crew", expectedVersion = 1, note = "Synthetic review", workOrderTypeCode = "ELECTRICITY.OUTAGE" })).StatusCode);
        using var supervisor = await RoleClient(app, "supervisor");
        Assert.Equal(HttpStatusCode.OK, (await supervisor.PostAsJsonAsync($"/api/incidents/{id}/approve", new { expectedVersion = 1, note = "Synthetic supervisor hazard review" })).StatusCode);
    }

    [Fact]
    public async Task AuditorHasReadAndExportButNoWritesIncludingResidentIntake()
    {
        using var app = new TestApp(); using var client = await RoleClient(app, "auditor");
        foreach (var route in new[] { "/api/audit", "/api/audit/verify", "/api/evidence/export", "/api/training/audit", "/api/incidents" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic pipe report" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/retention/purge", null)).StatusCode);
    }

    [Fact]
    public async Task CrewCanOnlyReadAndUpdateWorkAssignedToItsCrew()
    {
        using var app = new TestApp(); using var seed = app.CreateClient();
        var store = app.Services.GetRequiredService<IncidentStore>();
        var first = await seed.PostAsJsonAsync("/api/incidents", new { description = "Synthetic water pipe leak assigned to water crew.", area = "Synthetic crew zone A" });
        first.EnsureSuccessStatusCode(); using var data = JsonDocument.Parse(await first.Content.ReadAsStringAsync()); var id = data.RootElement.GetProperty("incident").GetProperty("id").GetString()!;
        await store.ChangeAsync(id, null, incident => { incident.AssignedCrewId = "water-01"; incident.Status = CivicOps.Models.IncidentStatus.Assigned; incident.NeedsHumanReview = false; incident.Approval = new("Synthetic supervisor", "Synthetic crew preparation", DateTimeOffset.UtcNow); return null; }, default);
        var second = await seed.PostAsJsonAsync("/api/incidents", new { description = "Synthetic road pothole assigned to another crew.", area = "Synthetic crew zone B" });
        second.EnsureSuccessStatusCode(); using var other = JsonDocument.Parse(await second.Content.ReadAsStringAsync()); var otherId = other.RootElement.GetProperty("incident").GetProperty("id").GetString();
        using var crew = await RoleClient(app, "crew", "water-01");
        var listed = await crew.GetFromJsonAsync<JsonElement>("/api/incidents"); Assert.Single(listed.EnumerateArray());
        Assert.Equal(id, listed[0].GetProperty("id").GetString());
        foreach (var path in new[] { "/api/incidents/", "/api/dispatch/incidents/" }) Assert.Equal(HttpStatusCode.NotFound, (await crew.GetAsync(path + otherId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await crew.PatchAsJsonAsync($"/api/incidents/{otherId}/status", new { status = "InProgress" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await crew.PatchAsJsonAsync($"/api/incidents/{id}/status", new { status = "InProgress" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await crew.PatchAsJsonAsync($"/api/incidents/{id}/status", new { status = "Closed" })).StatusCode);
        Assert.Single((await crew.GetFromJsonAsync<JsonElement>("/api/crews")).EnumerateArray());
    }

    [Fact]
    public async Task CookieMutationsRequireCsrfButServiceBearerDoesNot()
    {
        using var app = new TestApp(); using var admin = await RoleClient(app, "admin");
        admin.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/users", new { subject = "synthetic", displayName = "Synthetic user", roles = new[] { "auditor" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task MunicipalityRatePartitionsCannotConsumeAnotherMunicipalityQuota()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_INTAKE_LIMIT"] = "1"; using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/open311/v2/services")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/m/coast/open311/v2/services")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/open311/v2/services")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/m/coast/open311/v2/services")).StatusCode);
    }

    [Fact]
    public void AuditorReadPrivilegeDoesNotExpandACombinedCrewRolesWriteAssignment()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.Role, "crew"), new(ClaimTypes.Role, "auditor"), new("civicops:crew", "water-01")], "civicops-session"));
        var otherWork = new CivicOps.Models.Incident { AssignedCrewId = "roads-01" };
        Assert.True(EnterpriseAccessMiddleware.AssignedTo(principal, otherWork));
        Assert.False(EnterpriseAccessMiddleware.AssignedTo(principal, otherWork, writing: true));
        Assert.True(EnterpriseAccessMiddleware.CrewWritesRestricted(principal));
        Assert.True(EnterpriseAccessMiddleware.AssignedTo(principal, new CivicOps.Models.Incident { AssignedCrewId = "water-01" }, writing: true));
    }

    internal static async Task<HttpClient> RoleClient(TestApp app, string role, string? crew = null, string tenant = "default")
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var status = await client.GetFromJsonAsync<JsonElement>("/auth/status");
        client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        var login = await client.PostAsJsonAsync("/auth/demo-login", new { role, tenantId = tenant, crewId = crew }); login.EnsureSuccessStatusCode();
        status = await client.GetFromJsonAsync<JsonElement>("/auth/status");
        client.DefaultRequestHeaders.Remove("X-CivicOps-CSRF"); client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        return client;
    }
}
