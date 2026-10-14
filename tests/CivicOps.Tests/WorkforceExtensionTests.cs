using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class WorkforceExtensionTests
{
    private static WorkOrderCatalog Catalog => WorkOrderCatalog.Default;
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);
    private static CrewEligibilityService Eligibility(string policy = "warn") => new(null!, Catalog,
        TimeProvider.System, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_CERT_POLICY"] = policy }).Build());
    private static Worker Qualified(WorkOrderType type) => new("synthetic-test-a", "Synthetic test worker", "test-crew",
        type.RequiredSkills.Select(skill => new WorkerSkill(skill.Code, skill.MinimumLevel)).ToList(),
        type.RequiredCertifications.Select(code => new WorkerCertification(code, Now.AddMonths(-1), Now.AddMonths(1),
            "Synthetic supervisor", Catalog.CertificationTypes.First(cert => cert.Code == code).IssuingBasis, "synthetic-evidence")).ToList());
    private static Crew CrewFor(WorkOrderType type) => new("test-crew", "Synthetic test crew", type.Department,
        "Synthetic capability", ["synthetic-test-a"]);

    [Fact]
    public void CatalogueVersionReferencesAndServiceMappingsAreShared()
    {
        Assert.Equal("1.0.0", Catalog.Version); Assert.StartsWith("\"", Catalog.ETag); Assert.Equal(66, Catalog.ETag.Length);
        Assert.Equal(9, Catalog.WorkOrderTypes.Count);
        foreach (var type in Catalog.WorkOrderTypes)
        {
            Assert.Equal(type.Department, ServiceCatalog.Find(type.Code)!.Department);
            Assert.All(type.RequiredSkills, requirement => Assert.Contains(Catalog.Skills, skill => skill.Code == requirement.Code));
            Assert.All(type.RequiredCertifications, code => Assert.Contains(Catalog.CertificationTypes, certification => certification.Code == code));
            Assert.NotEmpty(type.Ppe); Assert.NotEmpty(type.ProcedureSteps); Assert.True(type.SlaTargetMinutes > 0);
        }
    }
    [Theory]
    [InlineData("burst pipe leaking", "WATER.BURST_MAIN")]
    [InlineData("streetlight flickering", "ELEC.STREETLIGHT_FAULT")]
    [InlineData("live wire down on road", "ELEC.LIVE_WIRE_DOWN")]
    [InlineData("pothole at hall", "ROADS.POTHOLE_REPAIR")]
    [InlineData("traffic signal fault", "ROADS.TRAFFIC_SIGNAL_FAULT")]
    [InlineData("blocked drain leaking water", "ROADS.BLOCKED_DRAIN")]
    [InlineData("illegal dumping rubbish", "WASTE.ILLEGAL_DUMPING")]
    [InlineData("flooding my home", "EMERG.FLOOD_RESPONSE")]
    [InlineData("broken park bench", "GENERAL.SERVICE_INSPECTION")]
    public void DeterministicTriageUsesCatalogueCode(string description, string expected)
    {
        var result = new DeterministicClassifier().Classify(description); Assert.Equal(expected, result.WorkOrderTypeCode);
        Assert.Equal(Catalog.Find(expected)!.Department, result.Department);
    }
    [Theory]
    [InlineData("BAD", "Roads", "Roads and Stormwater")]
    [InlineData("WATER.BURST_MAIN", "Roads", "Roads and Stormwater")]
    [InlineData("ROADS.POTHOLE_REPAIR", "Roads", "Electricity")]
    public void LlmSuggestionMustUseAnExistingCoherentCatalogueEntry(string code, string category, string department) =>
        Assert.False(AgentSchemas.Triage.Validate(AgentSchemas.Element(new { workOrderTypeCode = code, category, department,
            priority = "Medium", confidence = .9, rationale = "Synthetic model suggestion." })));
    [Fact]
    public void LlmSuggestionAcceptsValidWorkOrderType() =>
        Assert.True(AgentSchemas.Triage.Validate(AgentSchemas.Element(new { workOrderTypeCode = "ROADS.TRAFFIC_SIGNAL_FAULT",
            category = "Roads", department = "Roads and Stormwater", priority = "High", confidence = .9, rationale = "Signal fault." })));
    [Theory]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("skill")]
    [InlineData("cert")]
    [InlineData("hazard")]
    [InlineData("level")]
    [InlineData("sla")]
    [InlineData("basis")]
    [InlineData("ppe")]
    public void CatalogueValidationRejectsMalformedOrDanglingEntries(string mutation)
    {
        var types = Catalog.WorkOrderTypes.ToList(); var certs = Catalog.CertificationTypes.ToList();
        var version = Catalog.Version;
        switch (mutation)
        {
            case "version": version = "latest"; break;
            case "duplicate": types.Add(types[0]); break;
            case "skill": types[0] = types[0] with { RequiredSkills = [new("MISSING.SKILL", 3)] }; break;
            case "cert": types[0] = types[0] with { RequiredCertifications = ["MISSING.CERT"] }; break;
            case "hazard": types[0] = types[0] with { HazardClass = "unknown" }; break;
            case "level": types[0] = types[0] with { RequiredSkills = [new(types[0].RequiredSkills[0].Code, 6)] }; break;
            case "sla": types[0] = types[0] with { SlaTargetMinutes = -1 }; break;
            case "ppe": types[0] = types[0] with { Ppe = [] }; break;
            case "basis": certs[0] = certs[0] with { IssuingBasis = "self-awarded" }; break;
        }
        Assert.Throws<InvalidDataException>(() => new WorkOrderCatalog(version, types, Catalog.Skills, certs));
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public async Task JsonCatalogueLoaderRejectsDuplicateAndUnknownProperties(string mutation)
    {
        var path = Path.Combine(Path.GetTempPath(), "synthetic-catalogue-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var text = JsonSerializer.Serialize(new { version = Catalog.Version, workOrderTypes = Catalog.WorkOrderTypes,
                skills = Catalog.Skills, certificationTypes = Catalog.CertificationTypes }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            text = text.Insert(1, mutation == "duplicate" ? "\"version\":\"0.0.0\"," : "\"unexpected\":true,");
            await File.WriteAllTextAsync(path, text);
            if (mutation == "duplicate") Assert.Throws<InvalidDataException>(() => WorkOrderCatalog.Load(path));
            else Assert.Throws<JsonException>(() => WorkOrderCatalog.Load(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData("true", "warn")]
    [InlineData("false", "block")]
    public void DefaultPolicyIsWarnOnlyForExplicitDemo(string demo, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_DEMO_MODE"] = demo }).Build();
        Assert.Equal(expected, new CrewEligibilityService(null!, Catalog, TimeProvider.System, configuration).CertificationPolicy);
    }
    [Fact]
    public void FullyQualifiedCrewExplainsEachRequiredSkillAndCertification()
    {
        var type = Catalog.Find("WATER.BURST_MAIN")!;
        var result = Eligibility().Evaluate(type, CrewFor(type), [Qualified(type)], Now);
        Assert.Equal("eligible", result.Status); Assert.True(result.CanAssign);
        Assert.Contains(result.Reasons, reason => reason.Contains("WATER.PIPE_REPAIR level 3", StringComparison.Ordinal));
        Assert.Contains(result.Reasons, reason => reason.Contains("valid until", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("warn", "warned", true)]
    [InlineData("block", "blocked", false)]
    public void ExpiredCertificationsRespectPolicyAndExactExpiry(string policy, string status, bool canAssign)
    {
        var type = Catalog.Find("ELEC.LIVE_WIRE_DOWN")!; var worker = Qualified(type);
        worker.Certifications[1] = worker.Certifications[1] with { ExpiresAt = Now };
        var result = Eligibility(policy).Evaluate(type, CrewFor(type), [worker], Now);
        Assert.Equal(status, result.Status); Assert.Equal(canAssign, result.CanAssign);
        Assert.Contains(result.Reasons, reason => reason.Contains("Expired certification ELEC.AUTHORISED_PERSON", StringComparison.Ordinal));
    }
    [Fact]
    public void FutureIssuedCertificationDoesNotQualify()
    {
        var type = Catalog.Find("GENERAL.SERVICE_INSPECTION")!; var worker = Qualified(type);
        worker.Certifications[0] = worker.Certifications[0] with { IssuedAt = Now.AddMinutes(1) };
        Assert.Equal("warned", Eligibility().Evaluate(type, CrewFor(type), [worker], Now).Status);
    }
    [Theory]
    [InlineData("skill")]
    [InlineData("department")]
    [InlineData("availability")]
    [InlineData("members")]
    public void OperationalEligibilityAlwaysBlocksMissingCapability(string missing)
    {
        var type = Catalog.Find("WATER.BURST_MAIN")!; var worker = Qualified(type); var crew = CrewFor(type);
        if (missing == "skill") worker.Skills[0] = worker.Skills[0] with { Level = 1 };
        if (missing == "department") crew = crew with { Department = "Wrong department" };
        if (missing == "availability") crew = crew with { Availability = "off-shift" };
        if (missing == "members") crew = crew with { MemberIds = [] };
        var result = Eligibility().Evaluate(type, crew, [worker], Now);
        Assert.Equal("blocked", result.Status); Assert.False(result.CanAssign);
    }
    [Fact]
    public void UnknownCertificationPolicyFailsClosed() => Assert.Throws<InvalidDataException>(() => Eligibility("ignore"));

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task SeedGrantReplayAndAvailabilitySurviveRestartWithVerifiedState(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        var first = new TestApp { KeepData = true }; var directory = first.DataDirectory;
        try
        {
            WorkerCertification grant;
            using (first)
            {
                var store = first.Services.GetRequiredService<WorkforceStore>();
                await store.InitializeAsync(); Assert.Equal(12, (await store.ListWorkersAsync(CancellationToken.None)).Count);
                var now = DateTimeOffset.UtcNow;
                grant = new("SAFETY.PPE", now, now.AddMonths(12), "Synthetic trainer", "simulation", "sha256:synthetic-evidence");
                Assert.True(await store.GrantCertificationAsync("synthetic-water-01-a", grant, "synthetic-operation", CancellationToken.None));
                Assert.False(await store.GrantCertificationAsync("synthetic-water-01-a", grant, "synthetic-operation", CancellationToken.None));
                await store.SetCrewAvailabilityAsync("water-01", "off-shift", "Synthetic dispatcher", CancellationToken.None);
                Assert.True((await store.VerifyAsync(CancellationToken.None)).Valid);
            }
            using var restarted = new TestApp { DataDirectory = directory };
            var saved = restarted.Services.GetRequiredService<WorkforceStore>();
            Assert.Contains((await saved.GetWorkerAsync("synthetic-water-01-a", CancellationToken.None))!.Certifications, cert => cert == grant);
            Assert.False(await saved.GrantCertificationAsync("synthetic-water-01-a", grant, "synthetic-operation", CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => saved.GrantCertificationAsync("synthetic-water-01-a", grant with { EvidenceRef = "different" }, "synthetic-operation", CancellationToken.None));
            Assert.Equal("off-shift", (await saved.ListCrewsAsync(CancellationToken.None)).First(crew => crew.Id == "water-01").Availability);
            Assert.True((await saved.VerifyAsync(CancellationToken.None)).Valid);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task WorkforceSnapshotTamperingIsRejectedAfterRestart()
    {
        var first = new TestApp { KeepData = true }; var directory = first.DataDirectory;
        try
        {
            using (first) await first.Services.GetRequiredService<WorkforceStore>().InitializeAsync();
            var path = Path.Combine(directory, "workforce.json");
            var text = await File.ReadAllTextAsync(path); await File.WriteAllTextAsync(path, text.Replace("Synthetic Water crew 01 worker A", "Altered Water crew 01 worker A", StringComparison.Ordinal));
            using var restarted = new TestApp { DataDirectory = directory };
            await Assert.ThrowsAnyAsync<Exception>(async () => await restarted.Services.GetRequiredService<WorkforceStore>().InitializeAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task AssignmentBlockPolicyUsesLiveExpiredQualificationState(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); app.Settings["CIVICOPS_CERT_POLICY"] = "block"; using var client = app.CreateClient();
        var createdResponse = await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic live wire down on pavement", area = "Synthetic Ward", privacyConsentAccepted = true, privacyConsentVersion = PrivacyConsent.Version }); createdResponse.EnsureSuccessStatusCode();
        var created = (await createdResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("incident"); var id = created.GetProperty("id").GetString();
        Assert.Equal("ELEC.LIVE_WIRE_DOWN", created.GetProperty("workOrderTypeCode").GetString());
        (await client.PostAsJsonAsync($"/api/incidents/{id}/approve", new { approvedBy = "Demo dispatcher" })).EnsureSuccessStatusCode();
        var assignment = await client.PostAsJsonAsync($"/api/incidents/{id}/assign", new { crewId = "electricity-01", approvedBy = "Demo dispatcher" });
        Assert.Equal(HttpStatusCode.BadRequest, assignment.StatusCode); Assert.Contains("Expired certification", await assignment.Content.ReadAsStringAsync());
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task DemoWarnPolicyPermitsAssignmentAndRecordsExpiredCertificateWarning(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var created = await FoundationTests.Create(client, new { description = "Synthetic live wire down on pavement", area = "Synthetic Ward", privacyConsentAccepted = true, privacyConsentVersion = PrivacyConsent.Version });
        var id = created.GetProperty("id").GetString();
        (await client.PostAsJsonAsync($"/api/incidents/{id}/approve", new { approvedBy = "Demo dispatcher" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/incidents/{id}/assign", new { crewId = "electricity-01", approvedBy = "Demo dispatcher" })).EnsureSuccessStatusCode();
        var saved = await app.Services.GetRequiredService<IncidentStore>().GetAsync(id!, CancellationToken.None);
        Assert.Equal(IncidentStatus.Assigned, saved!.Status);
        Assert.Contains(saved.Audit, entry => entry.Action == "assigned" && entry.Detail.Contains("Eligibility: warned", StringComparison.Ordinal)
            && entry.Detail.Contains("Expired certification ELEC.AUTHORISED_PERSON", StringComparison.Ordinal));
        Assert.True((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(CancellationToken.None)).Valid);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task DispatcherOverrideChangesTypeRequiresNewApprovalAndIsAuditChained(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        // This sequential approval/override owns the case version; delivery scheduling is tested separately.
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var created = await FoundationTests.Create(client);
        var id = created.GetProperty("id").GetString()!; var coordinator = app.Services.GetRequiredService<IncidentCoordinator>();
        var invalid = await coordinator.OverrideWorkOrderTypeAsync(id, new("UNKNOWN.TYPE"), "Synthetic dispatcher", CancellationToken.None);
        Assert.NotNull(invalid.Error);
        Assert.Null((await coordinator.ApproveAsync(id, new("Ignored actor"), "Synthetic dispatcher", CancellationToken.None)).Error);
        var result = await coordinator.OverrideWorkOrderTypeAsync(id, new("ROADS.TRAFFIC_SIGNAL_FAULT", "Synthetic reassessment"), "Synthetic dispatcher", CancellationToken.None);
        Assert.Null(result.Error); Assert.Equal("ROADS.TRAFFIC_SIGNAL_FAULT", result.Incident!.WorkOrderTypeCode);
        Assert.Equal("Roads and Stormwater", result.Incident.Department); Assert.Null(result.Incident.Approval); Assert.True(result.Incident.NeedsHumanReview);
        Assert.Equal(IncidentStatus.PendingApproval, result.Incident.Status);
        Assert.Contains(result.Incident.Audit, entry => entry.Action == "work-order-type-overridden" && entry.Actor == "Synthetic dispatcher");
        Assert.True((await app.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(CancellationToken.None)).Valid);
    }
    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("ELEC.NO_SUCH_FAULT")]
    [InlineData("ROADS.BURST_MAIN")]
    public async Task PublicIntakeRejectsUnknownExplicitServiceCodeWithoutCreatingCase(string code)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic water leak beside hall", area = "Synthetic Ward", serviceCode = code });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Unknown serviceCode", await response.Content.ReadAsStringAsync());
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(CancellationToken.None));
    }
    [Theory]
    [InlineData("water", "WATER.BURST_MAIN")]
    [InlineData("ROADS", "ROADS.POTHOLE_REPAIR")]
    [InlineData("ELEC.STREETLIGHT_FAULT", "ELEC.STREETLIGHT_FAULT")]
    public async Task PublicIntakeAcceptsKnownServiceAliasesAndPreciseCatalogueCodes(string code, string expected)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var created = await FoundationTests.Create(client, new { description = "Synthetic water leak beside hall", area = "Synthetic Ward", serviceCode = code });
        Assert.Equal(expected, created.GetProperty("workOrderTypeCode").GetString());
        var type = Catalog.Find(expected)!;
        Assert.Equal(type.Category, created.GetProperty("category").GetString());
        Assert.Equal(type.Department, created.GetProperty("department").GetString());
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task OverrideClearsStaleRecommendationAndPersistsCriticalPriorityFloor(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        // Keep unrelated delivery metadata from racing the override's optimistic version check.
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient(); var created = await FoundationTests.Create(client);
        var id = created.GetProperty("id").GetString()!;
        var store = app.Services.GetRequiredService<IncidentStore>();
        var original = await store.GetAsync(id, CancellationToken.None); Assert.Equal("water-01", original!.RecommendedCrewId);
        Assert.Equal("Medium", original.Priority);
        var changed = await app.Services.GetRequiredService<IncidentCoordinator>().OverrideWorkOrderTypeAsync(id,
            new("ELEC.LIVE_WIRE_DOWN", "Synthetic hazard reassessment"), "Synthetic dispatcher", CancellationToken.None);
        Assert.Null(changed.Error);
        var saved = await store.GetAsync(id, CancellationToken.None);
        Assert.Equal("ELEC.LIVE_WIRE_DOWN", saved!.WorkOrderTypeCode); Assert.Equal("Urgent", saved.Priority);
        Assert.Equal("Electricity", saved.Department); Assert.Null(saved.RecommendedCrewId);
        Assert.Contains("qualification review", saved.RecommendedCrewReason);
        Assert.Null(saved.Approval); Assert.True(saved.NeedsHumanReview); Assert.Null(saved.AssignedCrewId);
        Assert.True((await store.VerifyAuditAsync(CancellationToken.None)).Valid);
    }

}
