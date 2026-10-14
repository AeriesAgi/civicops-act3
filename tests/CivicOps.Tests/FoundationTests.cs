using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
namespace CivicOps.Tests;

public sealed class FoundationTests
{
    [Theory]
    [InlineData("description", 4001)] [InlineData("area", 201)] [InlineData("contactEmail", 255)]
    [InlineData("source", 41)] [InlineData("sourceLabel", 81)] [InlineData("locationProvenance", 201)]
    [InlineData("residentLanguage", 13)] [InlineData("contactPreference", 21)] [InlineData("conversationId", 129)]
    [InlineData("contactPhone", 41)] [InlineData("serviceCode", 41)]
    public async Task IntakeFieldsAreBounded(string field, int length)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var body = new Dictionary<string, object> { ["description"] = "Synthetic leak outside hall", [field] = new string('x', length) };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/incidents", body)).StatusCode);
    }
    [Theory]
    [InlineData("[]")] [InlineData("null")] [InlineData("123")] [InlineData("\"hello\"")]
    [InlineData("{\"service_code\":123}")] [InlineData("{\"service_code\":\"WATER\",\"description\":123}")]
    [InlineData("{\"service_code\":\"WATER\",\"address_string\":[]}")]
    [InlineData("{\"service_code\":\"UNKNOWN\"}")] [InlineData("{}")]
    public async Task InvalidOpen311Returns400(string json)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/open311/v2/requests", new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
    }
    [Theory]
    [InlineData("WATER", "Water and Sanitation")] [InlineData("ROADS", "Roads and Stormwater")]
    [InlineData("ELECTRICITY", "Electricity")] [InlineData("WASTE", "Waste Management")] [InlineData("GENERAL", "Public Works")]
    public async Task Open311CodesControlRoutingWhenDescriptionIsAmbiguous(string code, string department)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/open311/v2/requests", new { service_code = code, description = "Synthetic issue needs inspection" }); response.EnsureSuccessStatusCode();
        var reference = (await response.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("service_request_id").GetString();
        var incident = await client.GetFromJsonAsync<JsonElement>("/api/incidents/" + reference);
        Assert.Equal(department, incident.GetProperty("department").GetString());
    }
    [Fact]
    public async Task ServicesDiscoveryListsAllMappedCodes()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var services = await client.GetFromJsonAsync<JsonElement>("/open311/v2/services");
        Assert.Equal(ServiceCatalog.All.Length, services.GetArrayLength()); Assert.Contains(services.EnumerateArray(), x => x.GetProperty("service_code").GetString() == "ROADS");
    }
    [Fact]
    public async Task RequestBodyAndIdempotencyKeyAreBounded()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/incidents", new StringContent(new string('x', 32769), Encoding.UTF8, "application/json"))).StatusCode);
        client.DefaultRequestHeaders.Add("Idempotency-Key", new string('x', 201));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic report at the hall" })).StatusCode);
    }
    [Fact]
    public async Task DispatcherAuditUsesVerifiedIdentityInsteadOfCallerLabel()
    {
        // This assertion owns the optimistic version and principal attribution, not delivery scheduling.
        using var app = new TestApp { EnableBackgroundDelivery = false }; app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        app.Settings["CIVICOPS_DISPATCHERS"] = "Synthetic operator:" + IncidentStore.Hash("test-named-dispatcher");
        using var client = app.CreateClient();
        var created = await Create(client); client.DefaultRequestHeaders.Authorization = new("Bearer", "test-named-dispatcher");
        var response = await client.PostAsJsonAsync($"/api/incidents/{created.GetProperty("id").GetString()}/approve", new { approvedBy = "Spoofed caller", expectedVersion = 1 }); response.EnsureSuccessStatusCode();
        var incident = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident");
        Assert.Equal("Synthetic operator", incident.GetProperty("approval").GetProperty("approvedBy").GetString());
    }
    [Fact]
    public async Task UnauthenticatedRequestsCannotBurnDispatchQuotaAndIdentitiesArePartitioned()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false"; app.Settings["CIVICOPS_DISPATCH_LIMIT"] = "2";
        app.Settings["CIVICOPS_DISPATCHERS"] = "Operator A:" + IncidentStore.Hash("test-principal-a") + ";Operator B:" + IncidentStore.Hash("test-principal-b");
        using var client = app.CreateClient();
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/dispatcher")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-principal-a");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dispatcher")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dispatcher")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/dispatcher")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-principal-b");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dispatcher")).StatusCode);
    }
    [Fact]
    public async Task RejectedDispatchRequestsUseAnonymousIpQuotaWhileValidDispatchersStayAvailable()
    {
        using var app=new TestApp();app.Settings["CIVICOPS_DEMO_MODE"]="false";app.Settings["CIVICOPS_INTAKE_LIMIT"]="1";
        using var client=app.CreateClient();
        async Task<int> Send(string ip) => (await app.Server.SendAsync(context=>{context.Connection.RemoteIpAddress=IPAddress.Parse(ip);context.Request.Method="GET";context.Request.Path="/api/dispatcher";})).Response.StatusCode;
        Assert.Equal(401,await Send("192.0.2.11"));Assert.Equal(429,await Send("192.0.2.11"));Assert.Equal(401,await Send("192.0.2.12"));
        client.DefaultRequestHeaders.Authorization=new("Bearer","test-dispatch-token");
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/dispatcher")).StatusCode);
    }
    [Fact]
    public async Task AnonymousIpPartitionsCannotStarveOtherClientsOrDispatchers()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_INTAKE_LIMIT"] = "1";
        using var client = app.CreateClient();
        async Task<int> Send(string ip) => (await app.Server.SendAsync(context => { context.Connection.RemoteIpAddress = IPAddress.Parse(ip); context.Request.Method = "GET"; context.Request.Path = "/open311/v2/services"; })).Response.StatusCode;
        Assert.Equal(200, await Send("192.0.2.1")); Assert.Equal(429, await Send("192.0.2.1")); Assert.Equal(200, await Send("192.0.2.2"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dispatcher")).StatusCode);
    }
    [Fact]
    public void DemoCannotSelectSmtpAndBlankDataPathsAlign()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_DEMO_MODE"] = "true", ["CIVICOPS_EMAIL_MODE"] = "smtp", ["CIVICOPS_DATA_DIR"] = " ", ["CIVICOPS_EMAIL_SMTP_HOST"] = "smtp.example.invalid", ["CIVICOPS_EMAIL_SMTP_PORT"] = "587", ["CIVICOPS_EMAIL_SMTP_USER"] = "test-smtp-user", ["CIVICOPS_EMAIL_SMTP_PASSWORD"] = "test-smtp-password" }).Build();
        var env = new SyntheticEnvironment();
        Assert.Equal(Path.Combine(env.ContentRootPath, "Data"), DataPaths.Resolve(config, env));
        Assert.Equal("outbox", new EmailNotificationService(config, env, NullLogger<EmailNotificationService>.Instance).GetStatus().Mode);
    }
    [Fact]
    public async Task PublicLookupNeverLeaksResidentDataOrInternalNotes()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var incident = await Create(client, new { description = "Synthetic private transcript sentinel leak", contactEmail = "resident@example.invalid", area = "Private area sentinel", consentToContact = true });
        var lookup = await client.GetStringAsync("/api/incidents/" + incident.GetProperty("referenceNumber").GetString());
        foreach (var value in new[] { "resident@example.invalid", "sentinel", "rawTranscript", "contactEmail", "intakeKeyHash", "\"approval\":", "\"audit\":", "agentRuns", "contactPhone" }) Assert.DoesNotContain(value, lookup, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task IdempotencyReplaysAndConflictingBodyReturns409()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); client.DefaultRequestHeaders.Add("Idempotency-Key", "test-intake-retry");
        var first = await Create(client); var second = await Create(client); Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/incidents", new { description = "Different synthetic incident body" })).StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/api/incidents")).GetArrayLength());
    }
    internal static async Task<JsonElement> Create(HttpClient client, object? body = null)
    {
        var payload = JsonSerializer.SerializeToElement(body ?? new { description = "Synthetic burst pipe near hall", area = "Demo North" });
        var consented = payload.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        consented.TryAdd("privacyConsentAccepted", true); consented.TryAdd("privacyConsentVersion", PrivacyConsent.Version);
        var response = await client.PostAsJsonAsync("/api/incidents", consented);
        if (!response.IsSuccessStatusCode && response.Headers.TryGetValues("X-Correlation-ID", out var ids)
            && TestApp.HandledErrors.TryRemove(ids.Single(), out var diagnostic))
            throw new HttpRequestException($"Synthetic intake returned HTTP {(int)response.StatusCode}: {diagnostic}", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident");
    }
}
public sealed class SyntheticEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "CivicOps";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "civicops-environment-" + Guid.NewGuid().ToString("N"));
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
