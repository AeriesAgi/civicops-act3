using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class ZendeskEndpointTests
{
    [Fact]
    public async Task ValidWebhookRecordsReplyAndNeverApprovesOrAssigns()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var reference = await CreateCaseAsync(client);
        var before = await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default);
        using var response = await client.SendAsync(Signed(new { eventId = "fake-zendesk-event", caseReference = reference, status = "solved", comment = "Dispatcher reply in Zendesk." }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default);
        Assert.Equal(before!.Status, after!.Status); Assert.Null(after.Approval); Assert.Null(after.AssignedCrewId);
        Assert.True(after.NeedsHumanReview); Assert.Equal("fake-zendesk-event", after.LastZendeskEventId);
        Assert.Contains(after.Audit, x => x.Action == "zendesk-webhook" && x.Actor == "Verified case record update");
        Assert.True((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
    }

    [Fact]
    public async Task ReplayIsIdempotentEvenAfterAnotherEvent()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var reference = await CreateCaseAsync(client);
        var payload = new { eventId = "fake-event-one", caseReference = reference, comment = "First reply." };
        using var first = await client.SendAsync(Signed(payload)); first.EnsureSuccessStatusCode();
        using var second = await client.SendAsync(Signed(new { eventId = "fake-event-two", caseReference = reference, comment = "Second reply." })); second.EnsureSuccessStatusCode();
        using var replay = await client.SendAsync(Signed(payload)); replay.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("replayed").GetBoolean());
        var incident = await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default);
        Assert.Equal(2, incident!.Audit.Count(x => x.Action == "zendesk-webhook"));
    }

    [Fact]
    public async Task DelimiterBearingEventIdsCannotSuppressAnotherEvent()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var reference = await CreateCaseAsync(client);
        using var first = await client.SendAsync(Signed(new { eventId = "fake-event:extended", caseReference = reference })); first.EnsureSuccessStatusCode();
        using var second = await client.SendAsync(Signed(new { eventId = "fake-event", caseReference = reference })); second.EnsureSuccessStatusCode();
        var incident = await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default);
        Assert.Equal(2, incident!.Audit.Count(x => x.Action == "zendesk-webhook"));
    }

    [Fact]
    public async Task CivicoOpsOriginCommentLoopIsIgnored()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var reference = await CreateCaseAsync(client);
        using var response = await client.SendAsync(Signed(new { eventId = "fake-origin-event", caseReference = reference, comment = "Update [CivicOps event fake-outbox-event]" }));
        response.EnsureSuccessStatusCode();
        var incident = await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default);
        Assert.DoesNotContain(incident!.Audit, x => x.Action == "zendesk-webhook");
    }

    [Fact]
    public async Task WrongSignatureCannotRecordCaseAudit()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var reference = await CreateCaseAsync(client);
        using var request = Signed(new { eventId = "fake-bad-event", caseReference = reference });
        request.Headers.Remove("X-Zendesk-Webhook-Signature"); request.Headers.Add("X-Zendesk-Webhook-Signature", Convert.ToBase64String(new byte[32]));
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var incident = await app.Services.GetRequiredService<IncidentStore>().GetAsync(reference, default);
        Assert.DoesNotContain(incident!.Audit, x => x.Action == "zendesk-webhook");
    }

    [Fact]
    public async Task MissingSecretReturns503EvenInDemo()
    {
        using var app = new TestApp(); app.Settings["ZENDESK_WEBHOOK_SECRET"] = ""; using var client = app.CreateClient();
        using var response = await client.SendAsync(Signed(new { eventId = "fake-event", caseReference = "CIV-20261008-FAKE0001" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("{\"eventId\":1,\"caseReference\":\"fake\"}")]
    [InlineData("{\"eventId\":\"fake\"}")]
    public async Task MalformedPayloadIs400(string body)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        using var response = await client.SendAsync(SignedRaw(body)); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SignedOversizeCommentIsRejected()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        using var response = await client.SendAsync(Signed(new { eventId = "fake-event", caseReference = "CIV-20261008-FAKE0001", comment = new string('x', 4_001) }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownReferenceIs404()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        using var response = await client.SendAsync(Signed(new { eventId = "fake-event", caseReference = "CIV-20261008-FAKE0001" }));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OutboxRequiresDispatcherAuthenticationAndNeverReturnsPayloads()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false"; using var client = app.CreateClient();
        using var anonymous = await client.GetAsync("/api/integrations/outbox"); Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await CreateCaseAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "test-dispatch-token");
        using var authorized = await client.GetAsync("/api/integrations/outbox"); authorized.EnsureSuccessStatusCode();
        var body = await authorized.Content.ReadAsStringAsync();
        Assert.DoesNotContain("requestJson", body); Assert.DoesNotContain("test-dispatch-token", body);
        Assert.DoesNotContain("Synthetic pothole", body);
    }

    private static async Task<string> CreateCaseAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic pothole needing attention", area = "Synthetic ward", privacyConsentAccepted = true, privacyConsentVersion = PrivacyConsent.Version });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("incident").GetProperty("referenceNumber").GetString()!;
    }
    private static HttpRequestMessage Signed<T>(T body) => SignedRaw(JsonSerializer.Serialize(body));
    private static HttpRequestMessage SignedRaw(string body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-webhook-secret"), Encoding.UTF8.GetBytes(timestamp + body)));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/channels/zendesk/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Zendesk-Webhook-Signature", signature); request.Headers.Add("X-Zendesk-Webhook-Signature-Timestamp", timestamp);
        return request;
    }
}
