using System.Net;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class IncidentConcurrencyTests
{
    private const string Actor = "Synthetic authorised supervisor";

    public static IEnumerable<object[]> DeliveryInterleavings()
    {
        foreach (var backend in StoreBackends.Available())
        foreach (var workflow in new[] { "approve", "assign", "status" })
        foreach (var explicitVersion in new[] { false, true })
            yield return [backend[0], workflow, explicitVersion];
    }

    [Theory]
    [MemberData(nameof(DeliveryInterleavings))]
    public async Task DeliveryDuringCommsDraftPreservesDecisionAuditAndStrictCallerVersion(string backend, string workflow, bool explicitVersion)
    {
        using var selected = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false };
        app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        using var client = app.CreateClient();
        var baseline = await PrepareAsync(app, workflow);
        var store = app.Services.GetRequiredService<IncidentStore>();
        var queue = app.Services.GetRequiredService<IntegrationOutbox>();
        Incident? delivered = null;
        var deliveryCount = 0;
        using var handler = new InterleavingHandler(async ct =>
        {
            deliveryCount = await queue.ProcessDueAsync(ct);
            delivered = await store.GetAsync(baseline.Id, ct);
        });
        using var http = new HttpClient(handler);
        using var coordinator = Coordinator(app, http);

        var result = await DecideAsync(coordinator, baseline, workflow, explicitVersion);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(workflow switch { "approve" => 1, "assign" => 2, _ => 3 }, deliveryCount);
        Assert.NotNull(delivered);
        Assert.Equal(baseline.Version + deliveryCount, delivered.Version);
        Assert.NotNull(delivered.ZendeskTicketId);
        Assert.Equal("simulated", (await queue.GetStatusAsync(default)).Mode);
        var saved = await store.GetAsync(baseline.Id, default);
        Assert.NotNull(saved);
        Assert.Equal(delivered.ZendeskTicketId, saved.ZendeskTicketId);
        Assert.Equal(delivered.ZendeskTicketUrl, saved.ZendeskTicketUrl);
        Assert.Equal(delivered.Audit, saved.Audit.Take(delivered.Audit.Count));

        if (explicitVersion)
        {
            Assert.True(result.Conflict);
            Assert.NotNull(result.Error);
            Assert.Equal(baseline.Status, saved.Status);
            Assert.Equal(delivered.Version, saved.Version);
            Assert.Equal(baseline.AgentRuns.Count, saved.AgentRuns.Count);
            Assert.Equal(baseline.CommsDrafts.Count, saved.CommsDrafts.Count);
            Assert.Equal(deliveryCount, (await queue.ListAsync(default)).Count);
        }
        else
        {
            Assert.False(result.Conflict);
            Assert.Null(result.Error);
            Assert.Equal(workflow switch { "approve" => IncidentStatus.Approved, "assign" => IncidentStatus.Assigned, _ => IncidentStatus.InProgress }, saved.Status);
            Assert.Equal(delivered.Version + 1, saved.Version);
            Assert.Equal(baseline.AgentRuns.Count + 1, saved.AgentRuns.Count);
            Assert.Equal(baseline.CommsDrafts.Count + 1, saved.CommsDrafts.Count);
            Assert.Equal(Actor, saved.Audit.Last().Actor);
            Assert.Equal(workflow switch { "approve" => "approved", "assign" => "assigned", _ => "status" }, saved.Audit.Last().Action);
            Assert.Equal(deliveryCount + 1, (await queue.ListAsync(default)).Count);
            if (workflow == "approve") Assert.Equal(Actor, saved.Approval!.ApprovedBy);
            if (workflow == "assign") Assert.Equal("water-01", saved.AssignedCrewId);
        }
        Assert.True((await store.VerifyAuditAsync(default)).Valid);
    }

    public static IEnumerable<object[]> ConflictingFields()
    {
        foreach (var backend in StoreBackends.Available())
        foreach (var field in new[] { "status", "review", "approval", "assignment", "consent", "contactEmail", "contactPhone", "location", "transcript", "lastZendeskEventId", "deliveryActor", "missingDeliveryAudit", "extraDeliveryAudit" })
            yield return [backend[0], field];
    }

    [Theory]
    [MemberData(nameof(ConflictingFields))]
    public async Task ConcurrentHumanSafetyPrivacyOrLocationChangesAreNeverRebased(string backend, string field)
    {
        using var selected = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false };
        app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        using var client = app.CreateClient();
        var baseline = await PrepareAsync(app, "status");
        var store = app.Services.GetRequiredService<IncidentStore>();
        Incident? concurrent = null;
        using var handler = new InterleavingHandler(async ct =>
        {
            var changed = await store.ChangeAsync(baseline.Id, null, incident =>
            {
                incident.ZendeskTicketId = 471100;
                incident.ZendeskTicketUrl = "https://synthetic.example.invalid/tickets/471100";
                if (field != "missingDeliveryAudit")
                    incident.Audit.Add(new("zendesk-delivered", field == "deliveryActor" ? "Another synthetic actor" : "Zendesk integration", "Synthetic interleaving guard exercise.", DateTimeOffset.UtcNow));
                if (field == "extraDeliveryAudit")
                    incident.Audit.Add(new("zendesk-delivered", "Zendesk integration", "A second event without a separate version increment.", DateTimeOffset.UtcNow));
                switch (field)
                {
                    case "status": incident.Status = IncidentStatus.Closed; break;
                    case "review": incident.NeedsHumanReview = true; break;
                    case "approval": incident.Approval = new("Another synthetic supervisor", "A new human decision.", DateTimeOffset.UtcNow); break;
                    case "assignment": incident.AssignedCrewId = "roads-01"; break;
                    case "consent": incident.ConsentToContact = false; incident.ContactEmail = null; break;
                    case "contactEmail": incident.ContactEmail = "another-resident@example.invalid"; break;
                    case "contactPhone": incident.ContactPhone = "+27000000000"; break;
                    case "location": incident.Location = new(28.047, -26.205); break;
                    case "transcript": incident.TranscriptSummary = "Synthetic newly reviewed resident information."; break;
                    case "lastZendeskEventId": incident.LastZendeskEventId = "synthetic-new-webhook-event"; break;
                }
                return null;
            }, ct);
            Assert.Null(changed.Error);
            concurrent = changed.Incident;
        });
        using var http = new HttpClient(handler);
        using var coordinator = Coordinator(app, http);

        var result = await DecideAsync(coordinator, baseline, "status", explicitVersion: false);

        Assert.Equal(1, handler.Calls);
        Assert.True(result.Conflict);
        Assert.NotNull(result.Error);
        Assert.NotNull(concurrent);
        var saved = await store.GetAsync(baseline.Id, default);
        Assert.NotNull(saved);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(concurrent), System.Text.Json.JsonSerializer.Serialize(saved));
        Assert.Equal(baseline.AgentRuns.Count, saved.AgentRuns.Count);
        Assert.Equal(baseline.CommsDrafts.Count, saved.CommsDrafts.Count);
        Assert.DoesNotContain(saved.Audit, entry => entry.Action == "status" && entry.Actor == Actor);
        Assert.True((await store.VerifyAuditAsync(default)).Valid);
    }

    public static IEnumerable<object[]> EligibilityInterleavings()
    {
        foreach (var backend in StoreBackends.Available())
        foreach (var explicitVersion in new[] { false, true })
        foreach (var newReview in new[] { false, true })
            yield return [backend[0], explicitVersion, newReview];
    }

    [Theory]
    [MemberData(nameof(EligibilityInterleavings))]
    public async Task AssignmentKeepsPreEligibilitySnapshotAndCallerVersionGuard(string backend, bool explicitVersion, bool newReview)
    {
        using var selected = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false };
        app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        using var client = app.CreateClient();
        var baseline = await PrepareAsync(app, "assign");
        var store = app.Services.GetRequiredService<IncidentStore>();
        var queue = app.Services.GetRequiredService<IntegrationOutbox>();
        Incident? concurrent = null;
        var clock = new EligibilityInterleavingClock(() =>
        {
            Assert.Equal(2, queue.ProcessDueAsync(default).GetAwaiter().GetResult());
            if (newReview)
                Assert.Null(store.ChangeAsync(baseline.Id, null, incident =>
                {
                    incident.NeedsHumanReview = true;
                    incident.Audit.Add(new("resident-additional-info", "Another synthetic authorised human", "New information requires review before dispatch.", DateTimeOffset.UtcNow));
                    return null;
                }, default).GetAwaiter().GetResult().Error);
            concurrent = store.GetAsync(baseline.Id, default).GetAwaiter().GetResult();
        });
        var eligibility = new CrewEligibilityService(app.Services.GetRequiredService<WorkforceStore>(),
            app.Services.GetRequiredService<WorkOrderCatalog>(), clock, app.Services.GetRequiredService<IConfiguration>());
        using var handler = new InterleavingHandler(_ => Task.CompletedTask);
        using var http = new HttpClient(handler);
        using var coordinator = Coordinator(app, http, eligibility);

        var result = await DecideAsync(coordinator, baseline, "assign", explicitVersion);

        Assert.Equal(1, clock.Interleavings);
        Assert.NotNull(concurrent);
        var saved = await store.GetAsync(baseline.Id, default);
        Assert.NotNull(saved);
        Assert.Equal(concurrent.ZendeskTicketId, saved.ZendeskTicketId);
        Assert.Equal(concurrent.Audit, saved.Audit.Take(concurrent.Audit.Count));
        if (explicitVersion || newReview)
        {
            Assert.True(result.Conflict);
            Assert.Equal(0, handler.Calls);
            Assert.Equal(IncidentStatus.Approved, saved.Status);
            Assert.Null(saved.AssignedCrewId);
            Assert.Equal(concurrent.Version, saved.Version);
        }
        else
        {
            Assert.Null(result.Error);
            Assert.False(result.Conflict);
            Assert.Equal(1, handler.Calls);
            Assert.Equal(IncidentStatus.Assigned, saved.Status);
            Assert.Equal("water-01", saved.AssignedCrewId);
            Assert.Equal(concurrent.Version + 1, saved.Version);
            Assert.Equal(Actor, saved.Audit.Last().Actor);
        }
        Assert.True((await store.VerifyAuditAsync(default)).Valid);
    }

    private static async Task<Incident> PrepareAsync(TestApp app, string workflow)
    {
        var coordinator = app.Services.GetRequiredService<IncidentCoordinator>();
        var result = await coordinator.CreateAsync(new("Synthetic water pipe leak beside the hall", "Synthetic ward", "resident@example.invalid",
            ConsentToContact: true, PrivacyConsentAccepted: true, PrivacyConsentVersion: PrivacyConsent.Version,
            Location: new(28.046, -26.204), ShareLocation: true), null, default);
        Assert.Null(result.Error);
        if (workflow != "approve") Assert.Null((await coordinator.ApproveAsync(result.Incident.Id, new("Ignored synthetic caller"), Actor, default)).Error);
        if (workflow == "status") Assert.Null((await coordinator.AssignAsync(result.Incident.Id, new("water-01", "Ignored synthetic caller"), Actor, default)).Error);
        return (await app.Services.GetRequiredService<IncidentStore>().GetAsync(result.Incident.Id, default))!;
    }

    private static Task<ChangeResult> DecideAsync(IncidentCoordinator coordinator, Incident baseline, string workflow, bool explicitVersion)
    {
        int? version = explicitVersion ? baseline.Version : null;
        return workflow switch
        {
            "approve" => coordinator.ApproveAsync(baseline.Id, new("Ignored synthetic caller", ExpectedVersion: version), Actor, default),
            "assign" => coordinator.AssignAsync(baseline.Id, new("water-01", "Ignored synthetic caller", ExpectedVersion: version), Actor, default),
            _ => coordinator.StatusAsync(baseline.Id, new("InProgress", ApprovedBy: "Ignored synthetic caller", ExpectedVersion: version), Actor, default)
        };
    }

    private static IncidentCoordinator Coordinator(TestApp app, HttpClient http, CrewEligibilityService? assessedEligibility = null)
    {
        var services = app.Services;
        var time = services.GetRequiredService<TimeProvider>();
        var classifier = services.GetRequiredService<DeterministicClassifier>();
        var eligibility = assessedEligibility ?? services.GetRequiredService<CrewEligibilityService>();
        var ai = new AiReasoningClient(http, new AiOptions { BaseUrl = "http://synthetic-ai.example.invalid", MaxAttempts = 1 }, time);
        return new(services.GetRequiredService<IncidentStore>(), classifier, new(ai, classifier, time, eligibility),
            services.GetRequiredService<IntegrationOutbox>(), services.GetRequiredService<EmailNotificationService>(), time, eligibility,
            services.GetRequiredService<TenantStore>());
    }

    // The provider request never leaves this in-memory handler. Delivery/mutation completes
    // exactly after the human action was prepared and before the store commits it.
    private sealed class InterleavingHandler(Func<CancellationToken, Task> interleave) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await interleave(cancellationToken);
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{}") };
        }
    }

    private sealed class EligibilityInterleavingClock(Action interleave) : TimeProvider
    {
        public int Interleavings { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            if (Interleavings == 0)
            {
                Interleavings++;
                interleave();
            }
            return DateTimeOffset.UtcNow;
        }
    }
}
