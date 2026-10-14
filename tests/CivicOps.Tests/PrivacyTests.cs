using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class PrivacyTests
{
    [Fact]
    public void RedactorTimeoutDiscardsTheEntireValueInsteadOfLeakingOrFailingIntake()
    {
        const string input = "Synthetic email synthetic@example.invalid; phone 0820000000";
        var result = PiiRedactor.RedactSafely(input, _ =>
            throw new System.Text.RegularExpressions.RegexMatchTimeoutException(input, "synthetic-pattern", TimeSpan.FromMilliseconds(100)));
        Assert.Equal("[TEXT REDACTED: MATCH LIMIT]", result);
        Assert.DoesNotContain("example.invalid", result);
        Assert.DoesNotContain("0820000000", result);
    }

    [Fact]
    public void RedactorFailureBoundaryPreservesUnexpectedErrors()
    {
        Assert.Throws<InvalidDataException>(() => PiiRedactor.RedactSafely("Synthetic text", _ => throw new InvalidDataException()));
    }

    [Theory]
    [InlineData("Synthetic resident email synthetic@example.invalid", "EMAIL")]
    [InlineData("Synthetic phone 082 000 0000", "PHONE")]
    [InlineData("Synthetic phone +27 (82) 000-0000", "PHONE")]
    [InlineData("Synthetic phone +44 (20) 0000-0000", "PHONE")]
    [InlineData("Synthetic phone +1 (202) 000-0000", "PHONE")]
    [InlineData("Synthetic phone 0027 82 000 0000", "PHONE")]
    [InlineData("Synthetic ID 0001010000080", "SA ID")]
    [InlineData("My name is Synthetic Person.", "NAME")]
    [InlineData("Resident name: Synthetic Person", "NAME")]
    [InlineData("Contact name: Synthetïc Person.", "NAME")]
    public void DetectablePersonalDataIsRedacted(string text, string category)
    {
        var result = PiiRedactor.Redact(text);
        Assert.Contains("[" + category + " REDACTED]", result);
        Assert.DoesNotContain("synthetic@example.invalid", result);
        Assert.DoesNotContain("0001010000080", result);
        Assert.DoesNotContain("Synthetic Person", result);
    }
    [Theory]
    [InlineData("Synthetic water leak beside demo library.")]
    [InlineData("CIV-20261008-0123ABCD")]
    [InlineData("Report received for human review.")]
    [InlineData("0821234567abcdef0123456789abcdef01")]
    [InlineData("0821234567abcdef0123456789abcdef01: received")]
    [InlineData("0001010000080abcdef0123456789abcdef")]
    public void ServiceMeaningAndReferencesArePreserved(string text) => Assert.Equal(text, PiiRedactor.Redact(text));
    [Theory]
    [InlineData(null, null)] [InlineData(false, "civicops-popia-v1")] [InlineData(true, "old-notice")]
    public void ProductionConsentRequiresCurrentExplicitAcceptance(bool? accepted, string? version) =>
        Assert.NotNull(PrivacyConsent.Validate(new("Synthetic water issue for review", PrivacyConsentAccepted: accepted, PrivacyConsentVersion: version), true));
    [Fact]
    public void ConsentReceiptRecordsPurposeVersionAndSeparateContactChoice()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new IncidentRequest("Synthetic water issue for review", ConsentToContact: false,
            PrivacyConsentAccepted: true, PrivacyConsentVersion: PrivacyConsent.Version);
        Assert.Null(PrivacyConsent.Validate(request, true));
        var receipt = PrivacyConsent.Capture(request, now);
        Assert.True(receipt.Accepted); Assert.Equal(now, receipt.RecordedAt); Assert.Equal(PrivacyConsent.Version, receipt.Version);
        Assert.Equal(PrivacyConsent.Text, receipt.Text); Assert.Equal(PrivacyConsent.Purpose, receipt.Purpose);
        Assert.Equal("no-contact-requested", receipt.ContactChoice);
    }
    [Theory]
    [InlineData("en")][InlineData("af")][InlineData("zu")]
    public async Task SharedReportLocationHasAnOptionalPurposeNoticeAndRecordedText(string language)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var notice = await client.GetFromJsonAsync<JsonElement>("/api/privacy/notice?language=" + language);
        Assert.True(notice.GetProperty("locationOptional").GetBoolean());
        Assert.Equal(PrivacyConsent.LocationNoticeVersion, notice.GetProperty("locationNoticeVersion").GetString());
        var locationText = notice.GetProperty("locationText").GetString();
        Assert.Equal(ResidentMessages.Text("privacy.locationNotice", language), locationText);
        var request = new IncidentRequest("Synthetic location-purpose test", ResidentLanguage: language,
            PrivacyConsentAccepted: true, PrivacyConsentVersion: PrivacyConsent.Version, Location: new(31.02, -29.85), ShareLocation: true);
        var receipt = PrivacyConsent.Capture(request, DateTimeOffset.UtcNow);
        Assert.Contains(PrivacyConsent.LocationNoticeVersion, receipt.Text);
        Assert.Contains(locationText!, receipt.Text);
        Assert.DoesNotContain(PrivacyConsent.LocationNoticeVersion, PrivacyConsent.Capture(request with { Location = null, ShareLocation = false }, DateTimeOffset.UtcNow).Text);
    }
    [Fact]
    public void LegacyOrSyntheticMissingConsentIsNeverClaimedExplicit()
    {
        var request = new IncidentRequest("Synthetic legacy water issue");
        Assert.Null(PrivacyConsent.Validate(request));
        Assert.False(PrivacyConsent.Capture(request, DateTimeOffset.UtcNow).Accepted);
        Assert.Contains("unverified", PrivacyConsent.Capture(request, DateTimeOffset.UtcNow).Capture);
    }
    [Fact]
    public void EveryLlmRequestRedactsNestedPersonalDataAndPreservesJsonShape()
    {
        var payload = AiReasoningClient.BuildRequest(new AiOptions(), AgentSchemas.Triage,
            new { description = "Synthetic leak; email synthetic@example.invalid; phone 0820000000; ID 0001010000080; My name is Synthetic Person.", nested = new[] { new { email = "second@example.invalid", score = .7 } } });
        var content = JsonSerializer.SerializeToElement(payload).GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.DoesNotContain("example.invalid", content); Assert.DoesNotContain("0820000000", content); Assert.DoesNotContain("0001010000080", content);
        using var json = JsonDocument.Parse(content);
        Assert.Equal(.7, json.RootElement.GetProperty("nested")[0].GetProperty("score").GetDouble());
    }
    [Fact]
    public void ScalarLlmInputReceivesTheSameRedactionPolicy()
    {
        var request = JsonSerializer.SerializeToElement(AiReasoningClient.BuildRequest(new AiOptions(), AgentSchemas.Triage,
            "Synthetic email synthetic@example.invalid; phone 0820000000"));
        var content = request.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.DoesNotContain("example.invalid", content); Assert.DoesNotContain("0820000000", content);
        Assert.Contains("EMAIL REDACTED", content); Assert.Contains("PHONE REDACTED", content);
    }
    [Theory]
    [InlineData("name")] [InlineData("fullName")] [InlineData("resident_name")] [InlineData("contactName")] [InlineData("caller_name")] [InlineData("displayName")]
    public void JsonNameFieldsAreDetectableWithoutALabelInTheirValue(string key)
    {
        var payload = new Dictionary<string, object> { ["resident"] = new Dictionary<string, string> { [key] = "Synthetic Person", ["crewId"] = "synthetic-crew" } };
        var redacted = PiiRedactor.RedactJson(JsonSerializer.SerializeToElement(payload));
        Assert.Equal("[NAME REDACTED]", redacted.GetProperty("resident").GetProperty(key).GetString());
        Assert.Equal("synthetic-crew", redacted.GetProperty("resident").GetProperty("crewId").GetString());
    }
    [Theory]
    [InlineData("phone", "0000000", "PHONE")]
    [InlineData("phoneNumber", "0000000", "PHONE")]
    [InlineData("contact_phone", "0000000", "PHONE")]
    [InlineData("mobile", "0000000", "PHONE")]
    [InlineData("email", "synthetic@local", "EMAIL")]
    [InlineData("contactEmail", "synthetic@local", "EMAIL")]
    [InlineData("identity_number", "synthetic-invalid-id", "SA ID")]
    [InlineData("saId", "synthetic-invalid-id", "SA ID")]
    public void ExplicitPersonalDataFieldsAreRedactedRegardlessOfFormatting(string key, string value, string category)
    {
        var payload = new Dictionary<string, object> { [key] = value, ["id"] = "synthetic-case-id", ["workerId"] = "synthetic-worker", ["score"] = 42 };
        var redacted = PiiRedactor.RedactJson(JsonSerializer.SerializeToElement(payload));
        Assert.Equal("[" + category + " REDACTED]", redacted.GetProperty(key).GetString());
        Assert.Equal("synthetic-case-id", redacted.GetProperty("id").GetString());
        Assert.Equal("synthetic-worker", redacted.GetProperty("workerId").GetString());
        Assert.Equal(42, redacted.GetProperty("score").GetInt32());
    }
    [Fact]
    public async Task OptionalContactsAreSeparateAndDigestBoundAfterRestart()
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync();
        var snapshot = await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "incidents.json"));
        var contacts = await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "resident-contacts.json"));
        Assert.DoesNotContain("synthetic@example.invalid", snapshot); Assert.DoesNotContain("0820000000", snapshot);
        Assert.Contains("synthetic@example.invalid", contacts);
        using var restart = fixture.NewStore(); var recovered = await restart.GetAsync(incident.Id, default);
        Assert.Equal("synthetic@example.invalid", recovered!.ContactEmail); Assert.Equal("0820000000", recovered.ContactPhone);
        Assert.True((await restart.VerifyAuditAsync(default)).Valid);
    }
    [Fact]
    public async Task ContactTamperingFailsClosedWithoutEchoingPersonalValue()
    {
        using var fixture = new PrivacyFixture(); await fixture.CreateAsync();
        var file = Path.Combine(fixture.Directory, "resident-contacts.json");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("synthetic@example.invalid", "tampered@example.invalid", StringComparison.Ordinal));
        using var restart = fixture.NewStore();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => restart.ListAsync(default));
        Assert.DoesNotContain("example.invalid", error.ToString()); Assert.Contains("authenticated case state", error.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => restart.ListAsync(default));
        await Assert.ThrowsAsync<InvalidDataException>(() => restart.GetAsync("synthetic-case", default));
    }
    [Fact]
    public async Task ContactRetentionPreservesCaseAndWritesValueFreeTombstone()
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync();
        await fixture.Store.ChangeAsync(incident.Id, incident.Version, candidate =>
        {
            candidate.Status = IncidentStatus.Closed;
            candidate.Audit.Add(new("closed", "Synthetic operator", "Closed synthetic case", DateTimeOffset.UtcNow.AddDays(-40)));
            return null;
        }, default);
        Assert.Equal(1, await fixture.Store.PurgeContactFieldsAsync(DateTimeOffset.UtcNow.AddDays(-30), default));
        var retained = await fixture.Store.GetAsync(incident.Id, default);
        Assert.NotNull(retained); Assert.Null(retained!.ContactEmail); Assert.Null(retained.ContactPhone); Assert.Null(retained.ContactRecordHash);
        Assert.Contains(await fixture.Store.AuditAsync(default), record => record.Action == "privacy.contact-purged" && !record.Detail.Contains("example.invalid", StringComparison.Ordinal));
        Assert.True((await fixture.Store.VerifyAuditAsync(default)).Valid);
        using var restart = fixture.NewStore(); Assert.Null((await restart.GetAsync(incident.Id, default))!.ContactEmail);
    }
    [Fact]
    public async Task SnapshotRetentionAlsoErasesContacts()
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync();
        await fixture.Store.ChangeAsync(incident.Id, null, candidate => { candidate.Status = IncidentStatus.Closed; candidate.Audit.Add(new("closed", "Synthetic operator", "Closed", DateTimeOffset.UtcNow.AddDays(-40))); return null; }, default);
        Assert.Equal(1, await fixture.Store.PurgeClosedAsync(DateTimeOffset.UtcNow.AddDays(-30), default));
        Assert.DoesNotContain("synthetic@example.invalid", await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "resident-contacts.json")));
        Assert.Contains(await fixture.Store.AuditAsync(default), record => record.Action == "privacy.contact-purged");
    }
    [Fact]
    public async Task RestartPrunesContactsOrphanedByInterruptedSnapshotPurge()
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync();
        var contactFile = Path.Combine(fixture.Directory, "resident-contacts.json"); var priorContacts = await File.ReadAllTextAsync(contactFile);
        await fixture.Store.ChangeAsync(incident.Id, null, candidate => { candidate.Status = IncidentStatus.Closed; candidate.Audit.Add(new("closed", "Synthetic operator", "Closed", DateTimeOffset.UtcNow.AddDays(-40))); return null; }, default);
        await fixture.Store.PurgeClosedAsync(DateTimeOffset.UtcNow.AddDays(-30), default);
        // Simulate a crash after the case snapshot was retired but before the separate contact deletion completed.
        await File.WriteAllTextAsync(contactFile, priorContacts);
        using var restart = fixture.NewStore(); Assert.Empty(await restart.ListAsync(default));
        Assert.DoesNotContain("synthetic@example.invalid", await File.ReadAllTextAsync(contactFile));
        Assert.True((await restart.VerifyAuditAsync(default)).Valid);
    }
    [Fact]
    public async Task AuditDetailsRedactNewPersonalDataWithoutRewritingPriorHistory()
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync(); var previous = await fixture.Store.AuditAsync(default);
        await fixture.Store.ChangeAsync(incident.Id, null, candidate => { candidate.Audit.Add(new("note", "Synthetic operator", "Synthetic email synthetic@example.invalid; phone 0820000000", DateTimeOffset.UtcNow)); return null; }, default);
        var audit = await fixture.Store.AuditAsync(default); Assert.Equal(previous[0], audit[0]);
        Assert.Contains("[EMAIL REDACTED]", audit[^1].Detail); Assert.DoesNotContain("0820000000", audit[^1].Detail);
        Assert.True((await fixture.Store.VerifyAuditAsync(default)).Valid);
    }
    [Fact]
    public async Task AccessRequiresVerificationThenExportsLocalCaseContactAndReceiptWithAudit()
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync();
        var request = await fixture.Privacy.CreateAsync(new(incident.ReferenceNumber, "access", "Synthetic request at the municipality desk"), "Synthetic admin", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Privacy.FulfilAsync(request.Id, new(), "Synthetic admin", default));
        await fixture.Privacy.VerifyAsync(request.Id, new("Synthetic desk identity verified; no document copy retained"), "Synthetic admin", default);
        var exported = JsonSerializer.SerializeToElement(await fixture.Privacy.FulfilAsync(request.Id, new(), "Synthetic admin", default));
        Assert.Equal("synthetic@example.invalid", exported.GetProperty("contact").GetProperty("ContactEmail").GetString());
        Assert.True(exported.GetProperty("incident").GetProperty("PrivacyConsent").GetProperty("Accepted").GetBoolean());
        Assert.Equal("fulfilled", Assert.Single(await fixture.Privacy.ListAsync(default)).Status);
        Assert.Contains(await fixture.Store.AuditAsync(default), record => record.Action == "privacy.request-fulfilled");
    }
    [Theory]
    [InlineData("access")] [InlineData("correction")] [InlineData("anonymisation")]
    public async Task VerificationNotesAreBoundedAndRedacted(string action)
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync();
        var record = await fixture.Privacy.CreateAsync(new(incident.ReferenceNumber, action, "Synthetic email synthetic@example.invalid submitted request"), "Synthetic admin", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Privacy.VerifyAsync(record.Id, new(new string('x', 1001)), "Synthetic admin", default));
        var verified = await fixture.Privacy.VerifyAsync(record.Id, new("Verified synthetic email synthetic@example.invalid; no document retained"), "Synthetic admin", default);
        Assert.DoesNotContain("example.invalid", verified.VerificationNote); Assert.Contains("[EMAIL REDACTED]", verified.RequestNote);
    }
    [Fact]
    public async Task CorrectionUsesVersionAndSeparatesContactFromNarrative()
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync();
        var record = await fixture.VerifiedAsync(incident, "correction");
        await Assert.ThrowsAsync<PrivacyConflictException>(() => fixture.Privacy.FulfilAsync(record.Id, new(ExpectedVersion: 0), "Synthetic admin", default));
        await fixture.Privacy.FulfilAsync(record.Id, new(incident.Version, "Corrected synthetic water issue; email corrected@example.invalid", "corrected@example.invalid", ConsentToContact: true), "Synthetic admin", default);
        var corrected = (await fixture.Store.GetAsync(incident.Id, default))!;
        Assert.Equal("corrected@example.invalid", corrected.ContactEmail); Assert.DoesNotContain("example.invalid", corrected.Description);
        Assert.True(corrected.NeedsHumanReview); Assert.True((await fixture.Store.VerifyAuditAsync(default)).Valid);
    }
    [Fact]
    public async Task AnonymisationClearsLocalPersonalFieldsAndPreservesAuditChain()
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync();
        var record = await fixture.VerifiedAsync(incident, "anonymisation");
        await fixture.Privacy.FulfilAsync(record.Id, new(incident.Version), "Synthetic admin", default);
        var anonymous = (await fixture.Store.GetAsync(incident.Id, default))!;
        Assert.True(anonymous.PrivacyAnonymised); Assert.Null(anonymous.ContactEmail); Assert.Null(anonymous.ContactPhone);
        Assert.Null(anonymous.RawTranscript); Assert.Null(anonymous.ConversationId); Assert.Empty(anonymous.AgentRuns);
        Assert.False(anonymous.ConsentToContact); Assert.Contains("anonymised", anonymous.Description);
        Assert.Contains(await fixture.Store.AuditAsync(default), record => record.Action == "privacy.anonymisation");
        Assert.True((await fixture.Store.VerifyAuditAsync(default)).Valid);
    }
    [Fact]
    public async Task SubjectRequestsCannotCrossMunicipalityPartitions()
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync(); var request = await fixture.VerifiedAsync(incident, "access");
        using (TenantContext.Use("synthetic-coast"))
        {
            Assert.Empty(await fixture.Privacy.ListAsync(default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Privacy.FulfilAsync(request.Id, new(), "Synthetic admin", default));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Privacy.CreateAsync(new(incident.ReferenceNumber, "access", "Synthetic cross tenant request"), "Synthetic admin", default));
        }
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ProductionResidentIntakeRejectsMissingConsentAndAcceptsCurrentReceipt(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false }; app.Settings["CIVICOPS_DEMO_MODE"] = "false";
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic water leak at demo hall" })).StatusCode);
        var response = await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic water leak at demo hall", privacyConsentAccepted = true, privacyConsentVersion = PrivacyConsent.Version });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default)).PrivacyConsent!.Accepted);
    }
    [Theory]
    [InlineData("resident")] [InlineData("dispatcher")] [InlineData("supervisor")] [InlineData("auditor")] [InlineData("crew")]
    public async Task OnlyAdministratorsCanUseSubjectRequestApi(string role)
    {
        using var app = new TestApp(); using var client = await RbacTests.RoleClient(app, role);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/privacy/requests")).StatusCode);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task AdministratorApiExportsVerifiedRequestWithNoStoreAndAudit(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var resident = app.CreateClient();
        var incident = await FoundationTests.Create(resident);
        using var admin = await RbacTests.RoleClient(app, "admin");
        var created = await admin.PostAsJsonAsync("/api/admin/privacy/requests", new { referenceNumber = incident.GetProperty("referenceNumber").GetString(), action = "access", requestNote = "Synthetic desk access request from the demo resident" });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/privacy/requests/{id}/fulfil", new { })).StatusCode);
        var verified = await admin.PostAsJsonAsync($"/api/admin/privacy/requests/{id}/verify", new { verificationNote = "Synthetic resident identity verified at the desk; no document retained" });
        verified.EnsureSuccessStatusCode();
        var export = await admin.PostAsJsonAsync($"/api/admin/privacy/requests/{id}/fulfil", new { }); export.EnsureSuccessStatusCode();
        Assert.True(export.Headers.CacheControl!.NoStore); Assert.Equal("attachment", export.Content.Headers.ContentDisposition!.DispositionType);
        var json = await export.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, json.GetProperty("requestId").GetString()); Assert.Equal("default", json.GetProperty("municipality").GetString());
        Assert.Contains(await app.Services.GetRequiredService<IncidentStore>().AuditAsync(default), record => record.Action == "privacy.request-fulfilled");
    }
    [Fact]
    public async Task UnconfiguredProductionChatStillRequiresPurposeConsent()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_DEMO_MODE"] = "false"; using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/channels/elevenlabs/simulated-chat", new { conversation_id = "synthetic-privacy-chat", turn_id = "first", description = "Synthetic water leak by demonstration hall", area = "Synthetic ward" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }
    [Fact]
    public async Task PrivacyRequestStorageTamperingFailsClosed()
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync(); await fixture.VerifiedAsync(incident, "access");
        var file = Path.Combine(fixture.Directory, "privacy-requests.json");
        var json = await File.ReadAllTextAsync(file); await File.WriteAllTextAsync(file, json.Replace("\"status\": \"verified\"", "\"status\": \"fulfilled\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Privacy.ListAsync(default));
    }
    [Theory]
    [InlineData("0820000000\r\nInjected: header")]
    [InlineData("not-a-phone")]
    public async Task CorrectionRejectsPhoneHeaderInjectionAndInvalidPhone(string phone)
    {
        using var fixture = new PrivacyFixture(); var incident = await fixture.CreateAsync(); var request = await fixture.VerifiedAsync(incident, "correction");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Privacy.FulfilAsync(request.Id, new(incident.Version, ContactPhone: phone, ConsentToContact: true), "Synthetic admin", default));
        Assert.Equal("0820000000", (await fixture.Store.GetAsync(incident.Id, default))!.ContactPhone);
    }
}

internal sealed class PrivacyFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "privacy-tests-" + Guid.NewGuid().ToString("N"));
    private IConfiguration Configuration { get; }
    public IncidentStore Store { get; }
    public PrivacyService Privacy { get; }
    public PrivacyFixture()
    {
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_DATA_DIR"] = Directory, ["CIVICOPS_DEMO_MODE"] = "true" }).Build();
        Store = NewStore(); Privacy = new(Configuration, new SyntheticEnvironment(), Store, TimeProvider.System);
    }
    public IncidentStore NewStore() => new(Configuration, new SyntheticEnvironment());
    public async Task<Incident> CreateAsync() => (await Store.CreateAsync(new Incident
    {
        Description = "Synthetic water leak at the demonstration hall", RawDescription = "Synthetic water leak at the demonstration hall", Area = "Synthetic ward", NormalizedArea = "Synthetic ward",
        ContactEmail = "synthetic@example.invalid", ContactPhone = "0820000000", ConsentToContact = true, RawTranscript = "Synthetic transcript", ConversationId = "synthetic-conversation",
        PrivacyConsent = PrivacyConsent.Capture(new("Synthetic water leak at demo hall", PrivacyConsentAccepted: true, PrivacyConsentVersion: PrivacyConsent.Version), DateTimeOffset.UtcNow)
    }, null, new(), default)).Incident;
    public async Task<DataSubjectRequest> VerifiedAsync(Incident incident, string action)
    {
        var record = await Privacy.CreateAsync(new(incident.ReferenceNumber, action, "Synthetic subject request at service desk"), "Synthetic admin", default);
        return await Privacy.VerifyAsync(record.Id, new("Synthetic identity verified at service desk; no ID copy retained"), "Synthetic admin", default);
    }
    public void Dispose() { Privacy.Dispose(); Store.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
}
