using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class TenantEndpointTests
{
    private const string Coast = "/m/coast";
    [Theory]
    [InlineData("GET", "/api/incidents/{id}")]
    [InlineData("GET", "/api/dispatch/incidents/{id}")]
    [InlineData("GET", "/api/incidents/{id}/crew-eligibility")]
    [InlineData("POST", "/api/incidents/{id}/approve")]
    [InlineData("POST", "/api/incidents/{id}/assign")]
    [InlineData("POST", "/api/incidents/{id}/work-order-type")]
    [InlineData("POST", "/api/incidents/{id}/comms/foreign-draft/approve")]
    [InlineData("POST", "/api/incidents/{id}/callback")]
    [InlineData("PATCH", "/api/incidents/{id}/status")]
    public async Task EveryIncidentReadWritePathRejectsForeignMunicipality(string method, string template)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; app.Settings["ELEVENLABS_AGENT_PHONE_NUMBER_ID"] = "synthetic-number-id"; using var client = app.CreateClient();
        var created = await FoundationTests.Create(client);
        var id = created.GetProperty("id").GetString()!;
        var store = app.Services.GetRequiredService<IncidentStore>();
        var before = await store.GetAsync(id, CancellationToken.None);
        object body = template switch
        {
            var path when path.EndsWith("/assign", StringComparison.Ordinal) => new { crewId = "water-01", approvedBy = "Synthetic", note = "Synthetic test" },
            var path when path.EndsWith("/status", StringComparison.Ordinal) => new { status = "InProgress", approvedBy = "Synthetic" },
            var path when path.EndsWith("/work-order-type", StringComparison.Ordinal) => new { workOrderTypeCode = "ROADS.POTHOLE_REPAIR", note = "Synthetic override" },
            _ => new { approvedBy = "Synthetic", note = "Synthetic test" }
        };
        using var message = new HttpRequestMessage(new HttpMethod(method), Coast + template.Replace("{id}", id));
        if (method != "GET") message.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var after = await store.GetAsync(id, CancellationToken.None);
        Assert.Equal(before!.Version, after!.Version); Assert.Equal(before.Area, after.Area);
    }

    [Theory]
    [InlineData("lookup_case_status")][InlineData("add_case_information")][InlineData("request_human_handoff")]
    public async Task EveryResidentVoiceToolLookupOrMutationRejectsForeignMunicipality(string tool)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var report = await client.PostAsJsonAsync("/api/channels/elevenlabs/tools/report_incident", new
        { conversation_id = "synthetic-shared-conversation", turn_id = "one", description = "Synthetic pipe leak by the municipal hall", area = "Fictitious ward" });
        report.EnsureSuccessStatusCode();
        var reference = (await report.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("referenceNumber").GetString();
        var result = await client.PostAsJsonAsync(Coast + "/api/channels/elevenlabs/tools/" + tool, new
        { conversation_id = "synthetic-shared-conversation", turn_id = "two", reference_number = reference, information = "Synthetic foreign change", reason = "Synthetic escalation" });
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        var stored = await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference!, CancellationToken.None);
        Assert.DoesNotContain(stored!.Audit, entry => entry.Detail.Contains("Synthetic foreign change", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("web")][InlineData("open311")][InlineData("voice")]
    public async Task EveryResidentIntakeChannelUsesTrustedPathAndIgnoresBodyTenant(string channel)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        HttpResponseMessage response;
        string reference;
        if (channel == "open311")
        {
            response = await client.PostAsJsonAsync(Coast + "/open311/v2/requests", new
            { service_code = "WATER", description = "Synthetic coastal pipe leak", address_string = "Fictitious coastal ward", tenantId = "default" });
            response.EnsureSuccessStatusCode();
            reference = (await response.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("service_request_id").GetString()!;
        }
        else if (channel == "voice")
        {
            response = await client.PostAsJsonAsync(Coast + "/api/channels/elevenlabs/tools/report_incident", new
            { conversation_id = "synthetic-coastal-conversation", turn_id = "one", description = "Synthetic coastal pipe leak", area = "Fictitious coastal ward", tenantId = "default" });
            response.EnsureSuccessStatusCode();
            reference = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("referenceNumber").GetString()!;
        }
        else
        {
            response = await client.PostAsJsonAsync(Coast + "/api/incidents", new
            { description = "Synthetic coastal pipe leak", area = "Fictitious coastal ward", tenantId = "default" });
            response.EnsureSuccessStatusCode();
            reference = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident").GetProperty("referenceNumber").GetString()!;
        }
        response.Dispose();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/incidents/" + reference)).StatusCode);
        (await client.GetAsync(Coast + "/api/incidents/" + reference)).EnsureSuccessStatusCode();
        using var lease = TenantContext.Use("synthetic-coast");
        var incident = await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, CancellationToken.None);
        Assert.Equal("synthetic-coast", incident!.TenantId);
    }

    [Theory]
    [InlineData("/api/incidents")][InlineData("/api/audit")][InlineData("/api/training/audit")]
    [InlineData("/api/channels/elevenlabs/alerts")]
    public async Task CollectionsAndAuditDoNotDiscloseForeignRecords(string endpoint)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var created = await FoundationTests.Create(client); var reference = created.GetProperty("referenceNumber").GetString()!;
        var body = await client.GetStringAsync(Coast + endpoint);
        Assert.DoesNotContain(reference, body);
        var export = await client.GetStringAsync(Coast + "/api/evidence/export");
        Assert.DoesNotContain(reference, export);
        var outbox = await client.GetStringAsync(Coast + "/api/integrations/outbox");
        Assert.DoesNotContain(reference, outbox);
    }

    [Fact]
    public async Task AuthenticatedTenantCannotBeChangedByResidentPath()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-dispatch-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Coast + "/api/incidents")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TrainingAccess.DemoToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Coast + "/api/v1/training/work-order-types")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/m/unknown/api/incidents")).StatusCode);
    }

    [Fact]
    public async Task SignedProviderWebhooksCannotAttachForeignCaseOrConversation()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var report = await client.PostAsJsonAsync("/api/channels/elevenlabs/tools/report_incident", new
        { conversation_id = "synthetic-webhook-conversation", turn_id = "one", description = "Synthetic inland pipe leak", area = "Fictitious inland ward" });
        report.EnsureSuccessStatusCode();
        var reference = (await report.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("referenceNumber").GetString()!;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { eventId = "synthetic-foreign-event", caseReference = reference, comment = "Synthetic foreign webhook", source = "synthetic" }));
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var timestampBytes = Encoding.UTF8.GetBytes(timestamp);
        var signed = timestampBytes.Concat(bytes).ToArray();
        using var zendesk = new HttpRequestMessage(HttpMethod.Post, Coast + "/api/channels/zendesk/webhook") { Content = new ByteArrayContent(bytes) };
        zendesk.Content.Headers.ContentType = new("application/json");
        zendesk.Headers.Add("X-Zendesk-Webhook-Signature-Timestamp", timestamp);
        zendesk.Headers.Add("X-Zendesk-Webhook-Signature", Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-webhook-secret"), signed)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(zendesk)).StatusCode);
        var raw = JsonSerializer.Serialize(new { type = "post_call_transcription", data = new
        { conversation_id = "synthetic-webhook-conversation", analysis = new { transcript_summary = "Synthetic foreign transcript" } } });
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var eleven = new HttpRequestMessage(HttpMethod.Post, Coast + "/api/channels/elevenlabs/post-call") { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        eleven.Headers.Add("ElevenLabs-Signature", "t=" + seconds + ",v0=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-webhook-secret"), Encoding.UTF8.GetBytes(seconds + "." + raw))).ToLowerInvariant());
        (await client.SendAsync(eleven)).EnsureSuccessStatusCode();
        var stored = await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, CancellationToken.None);
        Assert.Null(stored!.TranscriptSummary); Assert.Null(stored.LastZendeskEventId);
        Assert.DoesNotContain(stored.Audit, entry => entry.Detail.Contains("Synthetic foreign webhook", StringComparison.Ordinal));
        using var lease = TenantContext.Use("synthetic-coast");
        var receipts = await app.Services.GetRequiredService<ElevenLabsChannelState>().ReadAsync(CancellationToken.None);
        Assert.Single(receipts); Assert.Null(receipts[0].ReferenceNumber); Assert.Equal("synthetic-coast", receipts[0].TenantId);
    }

    [Fact]
    public async Task HttpSseStreamDoesNotNotifyAnotherMunicipality()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        using var lifetime = new CancellationTokenSource();
        try
        {
            using var response = await client.GetAsync(Coast + "/api/stream", HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(lifetime.Token));
            async Task<string?> ReadLineAsync() => await reader.ReadLineAsync(lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("event: ready", await ReadLineAsync());
            Assert.Equal("data: {}", await ReadLineAsync()); await ReadLineAsync();
            var events = app.Services.GetRequiredService<CaseEventStream>();
            events.Publish("training.completed");
            using (TenantContext.Use("synthetic-coast")) events.Publish("certification.granted");
            // If the inland event leaked, it would appear first in this ordered subscription.
            Assert.Equal("event: certification.granted", await ReadLineAsync());
            Assert.Equal("data: {}", await ReadLineAsync());
        }
        finally { lifetime.Cancel(); }
    }
    [Fact]
    public async Task AdminReadsAndEveryForeignMutationStayInsideSessionMunicipality()
    {
        using var app = new TestApp();
        var accounts = app.Services.GetRequiredService<ServiceAccountStore>();
        var users = app.Services.GetRequiredService<BootstrapUserStore>();
        var workforce = app.Services.GetRequiredService<WorkforceStore>();
        var account = await accounts.CreateAsync("default", new("synthetic-private-inland-account", ["incidents:read"], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic administrator");
        var user = await users.CreateAsync("default", new("synthetic-private-inland-subject", "Synthetic private inland user", ["auditor"]), "Synthetic administrator");
        var crew = await workforce.UpsertCrewAsync(new("synthetic-private-inland-crew", "Synthetic private inland crew", "Water and Sanitation", "Synthetic repairs", []), "Synthetic administrator", default);
        var worker = await workforce.UpsertWorkerAsync(new("synthetic-private-inland-worker", "Synthetic private inland worker", crew.Id, [], []), "Synthetic administrator", default);
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await LoginAdmin(client, "synthetic-coast");
        foreach (var endpoint in new[] { "/api/admin/overview", "/api/admin/crews", "/api/admin/workers", "/api/admin/users", "/api/admin/service-accounts" })
            Assert.DoesNotContain("synthetic-private-inland", await client.GetStringAsync(endpoint), StringComparison.OrdinalIgnoreCase);
        foreach (var route in new[] { $"/api/admin/service-accounts/{account.Account.Id}/rotate", $"/api/admin/service-accounts/{account.Account.Id}/revoke",
            $"/api/admin/users/{user.User.Id}/rotate", $"/api/admin/users/{user.User.Id}/revoke" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route, new { overlapMinutes = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/admin/users/{user.User.Id}", new { displayName = "Synthetic foreign edit", roles = new[] { "admin" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/admin/crews/{crew.Id}", crew)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/admin/workers/{worker.Id}", worker)).StatusCode);
        var tenant = (await app.Services.GetRequiredService<TenantStore>().GetAsync("default"))!;
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/admin/tenants/default", tenant)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/tenants", tenant with { Id = "synthetic-forbidden-new", Slug = "synthetic-forbidden-new" })).StatusCode);
        Assert.NotNull(await accounts.AuthenticateAsync(account.Token)); Assert.True((await users.AuthenticateAsync(user.Token))!.IsInRole("auditor"));
        Assert.Equal(crew.Name, (await workforce.ListCrewsAsync(default)).Single(value => value.Id == crew.Id).Name);
        var created = await client.PostAsJsonAsync("/api/admin/service-accounts", new
        { name = "synthetic-coastal-account", scopes = new[] { "incidents:read" }, expiresAt = DateTimeOffset.UtcNow.AddDays(1), tenantId = "default" });
        created.EnsureSuccessStatusCode();
        var issued = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("synthetic-coast", issued.GetProperty("account").GetProperty("tenantId").GetString());
        Assert.DoesNotContain(issued.GetProperty("token").GetString()!, await client.GetStringAsync("/api/admin/service-accounts"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionedCatalogueEditsAndServiceTokenReadsAreTenantBound()
    {
        using var app = new TestApp(); using var resident = app.CreateClient();
        var inland = await FoundationTests.Create(resident); var reference = inland.GetProperty("referenceNumber").GetString()!;
        using var coast = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await LoginAdmin(coast, "synthetic-coast");
        var baseline = JsonNode.Parse(await coast.GetStringAsync("/api/admin/catalogue"))!;
        baseline["version"] = "1.0.1";
        var changed = await coast.PutAsJsonAsync("/api/admin/catalogue", new { expectedVersion = "1.0.0", catalogue = baseline });
        changed.EnsureSuccessStatusCode();
        Assert.Equal("1.0.1", (await coast.GetFromJsonAsync<JsonElement>("/api/admin/catalogue")).GetProperty("version").GetString());
        using var local = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await LoginAdmin(local, "default");
        Assert.Equal("1.0.0", (await local.GetFromJsonAsync<JsonElement>("/api/admin/catalogue")).GetProperty("version").GetString());
        var issued = await app.Services.GetRequiredService<ServiceAccountStore>().CreateAsync("synthetic-coast",
            new("synthetic-coastal-integration", ["incidents:read"], DateTimeOffset.UtcNow.AddDays(1)), "Synthetic coastal admin");
        using var integration = app.CreateClient(); integration.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await integration.GetAsync("/api/dispatch/incidents/" + reference)).StatusCode);
        Assert.DoesNotContain(reference, await integration.GetStringAsync("/api/incidents"));
        Assert.Equal(HttpStatusCode.Forbidden, (await integration.GetAsync("/m/inland/api/incidents")).StatusCode);
    }

    private static async Task LoginAdmin(HttpClient client, string tenant)
    {
        var status = await client.GetFromJsonAsync<JsonElement>("/auth/status");
        client.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
        (await client.PostAsJsonAsync("/auth/demo-login", new { role = "admin", tenantId = tenant })).EnsureSuccessStatusCode();
        status = await client.GetFromJsonAsync<JsonElement>("/auth/status");
        client.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        client.DefaultRequestHeaders.Add("X-CivicOps-CSRF", status.GetProperty("csrfToken").GetString());
    }

}
