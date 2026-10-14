using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;
namespace CivicOps.Tests;

public sealed class PersistenceAuditTests
{
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task StopStartPreservesCaseReplayApprovalAssignmentAgentRunsAndAudit(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        var first = new TestApp { KeepData = true, EnableBackgroundDelivery = false };
        var directory = first.DataDirectory; string id; string reference; string[] auditHashes;
        try
        {
            using (first)
            using (var client = first.CreateClient())
            {
                client.DefaultRequestHeaders.Add("Idempotency-Key", "restart-case-key");
                var created = await FoundationTests.Create(client); id = created.GetProperty("id").GetString()!; reference = created.GetProperty("referenceNumber").GetString()!;
                await first.Services.GetRequiredService<IntegrationOutbox>().ProcessDueAsync(CancellationToken.None);
                var detail = await client.GetFromJsonAsync<JsonElement>("/api/dispatch/incidents/" + id);
                var approved = await client.PostAsJsonAsync($"/api/incidents/{id}/approve", new { approvedBy = "Ignored spoof", expectedVersion = detail.GetProperty("version").GetInt32() }); approved.EnsureSuccessStatusCode();
                var version = (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident").GetProperty("version").GetInt32();
                var assigned = await client.PostAsJsonAsync($"/api/incidents/{id}/assign", new { crewId = "water-01", approvedBy = "Ignored spoof", expectedVersion = version }); assigned.EnsureSuccessStatusCode();
                auditHashes = (await client.GetFromJsonAsync<JsonElement>("/api/audit")).EnumerateArray().Select(x => x.GetProperty("hash").GetString()!).ToArray();
            }
            using var restarted = new TestApp { DataDirectory = directory }; using var restartedClient = restarted.CreateClient();
            var saved = await restartedClient.GetFromJsonAsync<JsonElement>("/api/dispatch/incidents/" + reference);
            Assert.Equal("Assigned", saved.GetProperty("status").GetString()); Assert.Equal("water-01", saved.GetProperty("assignedCrewId").GetString());
            Assert.Equal("Demo supervisor (not signed in, unverified)", saved.GetProperty("approval").GetProperty("approvedBy").GetString());
            Assert.True(saved.GetProperty("agentRuns").GetArrayLength() >= 7);
            var audit = await restartedClient.GetFromJsonAsync<JsonElement>("/api/audit"); Assert.Equal(auditHashes, audit.EnumerateArray().Take(auditHashes.Length).Select(x => x.GetProperty("hash").GetString()!).ToArray());
            restartedClient.DefaultRequestHeaders.Add("Idempotency-Key", "restart-case-key"); var replay = await FoundationTests.Create(restartedClient);
            Assert.Equal(id, replay.GetProperty("id").GetString()); Assert.Equal("Assigned", replay.GetProperty("status").GetString());
            Assert.True((await restartedClient.GetFromJsonAsync<JsonElement>("/api/audit/verify")).GetProperty("valid").GetBoolean());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task StoreRejectsEditingEarlierAuditEvents(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = app.CreateClient(); var created = await FoundationTests.Create(client);
        var store = app.Services.GetRequiredService<IncidentStore>();
        var result = await store.ChangeAsync(created.GetProperty("id").GetString()!, null, incident => { incident.Audit[0] = incident.Audit[0] with { Actor = "Tampered" }; return null; }, CancellationToken.None);
        Assert.NotNull(result.Error); Assert.True((await store.VerifyAuditAsync(CancellationToken.None)).Valid);
    }
    [Theory]
    [InlineData("Actor")] [InlineData("Detail")] [InlineData("PreviousHash")] [InlineData("StateHash")] [InlineData("Hash")]
    public void AuditDetectsTamperingWithEveryAuthenticatedField(string field)
    {
        var caseRecord = new Incident(); var records = new List<AuditRecord>();
        records.Add(AuditLog.Append(records, caseRecord, new("received", "CivicOps", "Synthetic event", DateTimeOffset.UtcNow)));
        var value = records[0];
        records[0] = field switch { "Actor" => value with { Actor = "Altered" }, "Detail" => value with { Detail = "Altered" }, "PreviousHash" => value with { PreviousHash = "Altered" }, "StateHash" => value with { StateHash = "Altered" }, _ => value with { Hash = "Altered" } };
        Assert.False(AuditLog.Verify(records).Valid);
    }
    [Fact]
    public void AuditDetectsReorderedDeletedAndInsertedEntries()
    {
        var incident = new Incident(); var records = new List<AuditRecord>();
        for (var i = 0; i < 3; i++) records.Add(AuditLog.Append(records, incident, new("event", "CivicOps", $"Synthetic {i}", DateTimeOffset.UtcNow)));
        Assert.True(AuditLog.Verify(records).Valid);
        Assert.False(AuditLog.Verify([records[1], records[0], records[2]]).Valid);
        Assert.False(AuditLog.Verify([records[0], records[2]]).Valid);
        Assert.False(AuditLog.Verify([records[0], records[0], records[1], records[2]]).Valid);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task SignedPostCallEntireSimulatedWorkflowEndsWithValidChain(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = app.CreateClient();
        var tool = await client.PostAsJsonAsync("/api/channels/elevenlabs/tools/report_incident", new { conversation_id = "sim-e2e-conversation", turn_id = "turn-1", description = "Synthetic burst pipe at the hall", area = "Demo East", language = "af" }); tool.EnsureSuccessStatusCode();
        var result = await tool.Content.ReadFromJsonAsync<JsonElement>(); var reference = result.GetProperty("referenceNumber").GetString()!;
        await app.Services.GetRequiredService<IntegrationOutbox>().ProcessDueAsync(CancellationToken.None);
        Assert.NotEmpty(await app.Services.GetRequiredService<DurableStorage>().ListPathsAsync(Path.Combine(app.DataDirectory, "zendesk-outbox"), ".json", CancellationToken.None));
        var detail = await client.GetFromJsonAsync<JsonElement>("/api/dispatch/incidents/" + reference);
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("zendeskTicketId").ValueKind);
        var approve = await client.PostAsJsonAsync($"/api/incidents/{reference}/approve", new { approvedBy = "Demo dispatcher", expectedVersion = detail.GetProperty("version").GetInt32() }); approve.EnsureSuccessStatusCode();
        var assign = await client.PostAsJsonAsync($"/api/incidents/{reference}/assign", new { crewId = "water-01", approvedBy = "Demo dispatcher" }); assign.EnsureSuccessStatusCode();
        var status = await client.PatchAsJsonAsync($"/api/incidents/{reference}/status", new { status = "InProgress", approvedBy = "Demo dispatcher" }); status.EnsureSuccessStatusCode();
        var raw = JsonSerializer.Serialize(new { type = "post_call_transcription", data = new { conversation_id = "sim-e2e-conversation", transcript = new[] { new { role = "user", message = "Synthetic private transcript sentinel" } }, analysis = new { transcript_summary = "Synthetic pipe leak recorded" } } });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-webhook-secret"), Encoding.UTF8.GetBytes(timestamp + "." + raw))).ToLowerInvariant();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/channels/elevenlabs/post-call") { Content = new StringContent(raw, Encoding.UTF8, "application/json") }; request.Headers.Add("ElevenLabs-Signature", $"t={timestamp},v0={signature}");
        var postCall = await client.SendAsync(request); postCall.EnsureSuccessStatusCode();
        detail = await client.GetFromJsonAsync<JsonElement>("/api/dispatch/incidents/" + reference); Assert.Equal("Synthetic pipe leak recorded", detail.GetProperty("transcriptSummary").GetString());
        var publicLookup = await client.GetStringAsync("/api/incidents/" + reference); Assert.DoesNotContain("sentinel", publicLookup);
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/audit/verify")).GetProperty("valid").GetBoolean());
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task DemoSeedIsIdempotentAndContainsHazardsDuplicatesAndLowConfidence(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = app.CreateClient();
        (await client.PostAsync("/api/demo/seed", null)).EnsureSuccessStatusCode(); (await client.PostAsync("/api/demo/seed", null)).EnsureSuccessStatusCode();
        var cases = await client.GetFromJsonAsync<JsonElement>("/api/incidents"); Assert.Equal(15, cases.GetArrayLength());
        Assert.Contains(cases.EnumerateArray(), x => x.GetProperty("priority").GetString() == "Urgent");
        Assert.Contains(cases.EnumerateArray(), x => x.GetProperty("duplicateOf").ValueKind == JsonValueKind.String);
        Assert.Contains(cases.EnumerateArray(), x => x.GetProperty("agentRuns").EnumerateArray().Any(r => r.GetProperty("confidence").GetDouble() < .75));
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ConflictReturnsFreshVersionAndUnapprovedAssignmentFails(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = app.CreateClient(); var created = await FoundationTests.Create(client); var id = created.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/incidents/{id}/assign", new { crewId = "water-01", approvedBy = "Demo" })).StatusCode);
        (await client.PostAsJsonAsync($"/api/incidents/{id}/approve", new { approvedBy = "Demo" })).EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync($"/api/incidents/{id}/assign", new { crewId = "water-01", approvedBy = "Demo", expectedVersion = 1 }); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident").GetProperty("version").GetInt32() > 1);
    }
}
