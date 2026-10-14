using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class ResidentLocalizationTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("af", "af")]
    [InlineData("afr", "af")]
    [InlineData("zu", "zu")]
    [InlineData("zul", "zu")]
    [InlineData("unsupported", "en")]
    public void NormalizationPreservesSupportedResidentLanguages(string supplied, string expected) =>
        Assert.Equal(expected, AgentPipeline.NormalizeLanguage(supplied));

    [Theory]
    [InlineData("en", "Reference")]
    [InlineData("af", "Verwysing")]
    [InlineData("zu", "Inombolo yecala")]
    public async Task ReportLookupConsentAndFallbackDraftUseChosenLanguage(string language, string referenceLabel)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var request = new IncidentRequest("Synthetic stormwater drain blocked beside the fictitious training hall.",
            "Synthetic ward", ResidentLanguage: language, PrivacyConsentAccepted: true, PrivacyConsentVersion: PrivacyConsent.Version);
        var response = await client.PostAsJsonAsync("/api/incidents", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var incident = Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        Assert.Equal(language, incident.ResidentLanguage);
        Assert.Equal(ResidentMessages.Text("privacy.notice", language), incident.PrivacyConsent!.Text);
        Assert.Equal(ResidentMessages.Text("privacy.purpose", language), incident.PrivacyConsent.Purpose);
        var draft = Assert.Single(incident.CommsDrafts);
        Assert.Equal(language, draft.Language); Assert.Contains(referenceLabel, draft.Text);
        Assert.Contains(incident.ReferenceNumber, draft.Text);
        var lookup = await client.GetFromJsonAsync<JsonElement>("/api/incidents/" + incident.ReferenceNumber);
        Assert.Equal(language, lookup.GetProperty("residentLanguage").GetString());
        Assert.Equal(IncidentView.SafeUpdate(incident.Status, language), lookup.GetProperty("publicUpdate").GetString());
        Assert.False(lookup.TryGetProperty("description", out _)); Assert.False(lookup.TryGetProperty("privacyConsent", out _));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("af")]
    [InlineData("zu")]
    public async Task SimulatedChatRecordsAndRepliesInChosenLanguage(string language)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/channels/elevenlabs/simulated-chat", new
        {
            conversation_id = "synthetic-localization", turn_id = "synthetic-turn", language,
            description = "Synthetic pothole at a fictitious library.", area = "Synthetic ward",
            privacy_consent_accepted = true, privacy_consent_version = PrivacyConsent.Version
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ResidentMessages.Text("tool.reportRecorded", language), body.GetProperty("nextStep").GetString());
        Assert.Equal(language, Assert.Single(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default)).ResidentLanguage);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("af")]
    [InlineData("zu")]
    public async Task NoticeResourceMatchesTheExactCapturedConsentText(string language)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var notice = await client.GetFromJsonAsync<JsonElement>("/api/privacy/notice?language=" + language);
        Assert.Equal(language, notice.GetProperty("language").GetString());
        Assert.Equal(PrivacyConsent.Version, notice.GetProperty("version").GetString());
        Assert.Equal(ResidentMessages.Text("privacy.notice", language), notice.GetProperty("text").GetString());
        Assert.Equal(ResidentMessages.Text("privacy.purpose", language), notice.GetProperty("purpose").GetString());
    }

    [Theory]
    [InlineData("af")]
    [InlineData("zu")]
    public async Task InvalidIntakeAndMissingConsentReturnLocalizedMessagesWithoutSaving(string language)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var request = new IncidentRequest("bad", ResidentLanguage: language);
        var invalid = await client.PostAsJsonAsync("/api/incidents", request);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains(ResidentMessages.Text("error.invalidReport", language), await invalid.Content.ReadAsStringAsync());
        Assert.Equal(ResidentMessages.Text("error.privacyConsent", language), PrivacyConsent.Validate(request, requireExplicit: true));
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("af")]
    [InlineData("zu")]
    public void EveryWorkflowStatusHasABoundedLocalizedUpdateAndSafeDraft(string language)
    {
        foreach (var status in Enum.GetValues<IncidentStatus>())
        {
            var update = IncidentView.SafeUpdate(status, language);
            Assert.NotEmpty(update); Assert.DoesNotContain("update.", update);
            var draft = ResidentMessages.Draft("CIV-SYNTHETIC", status, language);
            Assert.True(AgentSchemas.Comms(language, "CIV-SYNTHETIC").Validate(AgentSchemas.Element(new
            { language, text = draft, confidence = 1, rationale = "Synthetic bounded template." })), draft);
        }
    }

    [Theory]
    [InlineData("Sizofika kusasa.")]
    [InlineData("Ithimba lizofika ngemizuzu 20.")]
    [InlineData("Siqinisekisa ukufika.")]
    public void IsiZuluModelDraftsRejectArrivalPromises(string promised)
    {
        Assert.False(AgentSchemas.Comms("zu", "CIV-SYNTHETIC").Validate(AgentSchemas.Element(new
        { language = "zu", text = "CIV-SYNTHETIC " + promised, confidence = .9, rationale = "Synthetic unsafe promise." })));
    }

    [Fact]
    public async Task ResidentResourcesHaveIdenticalKeysPlaceholdersAndHonestDraftLabels()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var english = await client.GetFromJsonAsync<JsonElement>("/locales/en.json");
        var original = english.GetProperty("strings").EnumerateObject().Where(property => !property.Name.StartsWith("staff.", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);
        Assert.True(english.GetProperty("strings").EnumerateObject().Count(property => property.Name.StartsWith("staff.", StringComparison.Ordinal)) >= 300);
        foreach (var language in new[] { "af", "zu" })
        {
            var translated = await client.GetFromJsonAsync<JsonElement>("/locales/" + language + ".json");
            Assert.Contains("machine-drafted", translated.GetProperty("translationStatus").GetString());
            var strings = translated.GetProperty("strings");
            Assert.Equal(original.Keys.Order(), strings.EnumerateObject().Select(property => property.Name).Order());
            foreach (var (key, source) in original)
            {
                var value = strings.GetProperty(key).GetString()!;
                Assert.NotEmpty(value);
                foreach (var placeholder in new[] { "{reference}", "{update}", "{contact}", "{status}", "{time}", "{channel}" })
                    Assert.Equal(source.Contains(placeholder, StringComparison.Ordinal), value.Contains(placeholder, StringComparison.Ordinal));
            }
        }
    }
}
