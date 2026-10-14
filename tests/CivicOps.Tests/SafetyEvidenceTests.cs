using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace CivicOps.Tests;
public sealed class SafetyEvidenceTests
{
    [Fact]
    public async Task EvidenceExportRedactsResidentContentAndProvesHumanApprovalBoundary()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        await FoundationTests.Create(client,new {description="Synthetic private incident sentinel water leak",area="Private area sentinel",contactEmail="resident@example.invalid",consentToContact=true});
        var evidence = await client.GetFromJsonAsync<JsonElement>("/api/evidence/export");
        var serialized = evidence.GetRawText();
        foreach(var value in new[]{"sentinel","resident@example.invalid","\"contactEmail\":","\"description\":","\"rawTranscript\":","\"publicUpdate\":"}) Assert.DoesNotContain(value,serialized,StringComparison.OrdinalIgnoreCase);
        Assert.True(evidence.GetProperty("verification").GetProperty("valid").GetBoolean());
        Assert.False(evidence.GetProperty("cases")[0].GetProperty("humanApprovalRecorded").GetBoolean());
        Assert.Equal(5,evidence.GetProperty("cases")[0].GetProperty("agentRuns").GetArrayLength());
    }
    [Fact]
    public async Task UnconsentedContactsAreDiscardedBeforePersistence()
    {
        using var app=new TestApp();using var client=app.CreateClient();
        var created=await FoundationTests.Create(client,new {description="Synthetic pipe leak near the hall",contactEmail="resident@example.invalid",contactPhone="+27 00 000 0000",consentToContact=false});
        var stored=await app.Services.GetRequiredService<IncidentStore>().GetAsync(created.GetProperty("id").GetString()!,CancellationToken.None);
        Assert.Null(stored!.ContactEmail);Assert.Null(stored.ContactPhone);
        Assert.False(Directory.Exists(Path.Combine(app.DataDirectory,"email-outbox")));
    }
    [Fact]
    public void CaseReferenceUsesTheInjectedClockDate()
    {
        var reference=Incident.NewReference(new DateTimeOffset(2026,10,18,15,0,0,TimeSpan.Zero));
        Assert.StartsWith("CIV-20261018-",reference);
    }
    [Fact]
    public async Task EvidenceAndAuditNeedDispatcherAuthorization()
    {
        using var app=new TestApp();app.Settings["CIVICOPS_DEMO_MODE"]="false";using var client=app.CreateClient();
        foreach(var route in new[]{"/api/evidence/export","/api/audit","/api/audit/verify","/api/dispatch/incidents/missing","/api/stream"}) Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(route)).StatusCode);
    }
    [Fact]
    public async Task OldCommsDraftsAreSupersededAndCannotSendMisleadingStatus()
    {
        using var app=new TestApp();using var client=app.CreateClient();var created=await FoundationTests.Create(client);var id=created.GetProperty("id").GetString();
        var original=await client.GetFromJsonAsync<JsonElement>("/api/dispatch/incidents/"+id);var oldDraft=original.GetProperty("commsDrafts")[0].GetProperty("id").GetString();
        var approved=await client.PostAsJsonAsync($"/api/incidents/{id}/approve",new {approvedBy="Demo"});approved.EnsureSuccessStatusCode();
        var current=(await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident");
        Assert.Equal("Superseded",current.GetProperty("commsDrafts")[0].GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/incidents/{id}/comms/{oldDraft}/approve",new {})).StatusCode);
    }
    [Fact]
    public async Task OperationalResponsesDisallowCaching()
    {
        using var app=new TestApp();using var client=app.CreateClient();
        foreach(var route in new[]{"/api/ai/status","/api/channels/status","/api/incidents","/open311/v2/services"})
            Assert.Equal("no-store",(await client.GetAsync(route)).Headers.CacheControl?.ToString());
    }
    [Fact]
    public void AuditAuthenticatesTheCurrentCaseSnapshotAsWellAsTheChain()
    {
        var incident=new Incident();incident.Audit.Add(new("received","Synthetic actor","Synthetic event",DateTimeOffset.UtcNow));
        var chain=new List<AuditRecord>{AuditLog.Append([],incident,incident.Audit[0])};
        Assert.True(AuditLog.VerifySnapshot(chain,[incident]).Valid);incident.Priority="Urgent";
        Assert.True(AuditLog.Verify(chain).Valid);Assert.False(AuditLog.VerifySnapshot(chain,[incident]).Valid);
    }
    [Fact]
    public async Task RestartDetectsTamperedSnapshotAndDisablesWrites()
    {
        using var app=new TestApp();using var client=app.CreateClient();var created=await FoundationTests.Create(client);
        await app.Services.GetRequiredService<IntegrationOutbox>().ProcessDueAsync(CancellationToken.None);
        var file=Path.Combine(app.DataDirectory,"incidents.json");var saved=JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        saved["incidents"]![0]!["description"]="Tampered synthetic description";await File.WriteAllTextAsync(file,saved.ToJsonString());
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["CIVICOPS_DATA_DIR"]=app.DataDirectory}).Build();
        var restarted=new IncidentStore(config,new SyntheticEnvironment());Assert.False((await restarted.VerifyAuditAsync(CancellationToken.None)).Valid);
        await Assert.ThrowsAsync<InvalidDataException>(()=>restarted.ChangeAsync(created.GetProperty("id").GetString()!,null,incident=>{incident.PublicUpdate="Synthetic change";return null;},CancellationToken.None));
    }
}
