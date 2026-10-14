using CivicOps.Models;

namespace CivicOps.Services;

public static class PrivacyConsent
{
    public const string Version = "civicops-popia-v1";
    public const string Purpose = "Assess, route and manage the reported municipal service issue; send service updates only when separately requested.";
    public const string Text = "I agree that my municipality may process this report to assess and manage the service issue. Contact details are optional and used for service updates only with my separate consent. I can ask the Information Officer for access, correction or anonymisation. I will avoid identity numbers and sensitive personal details.";
    public const string LocationNoticeVersion = "civicops-location-v1";
    public static string? Validate(IncidentRequest request, bool requireExplicit = false)
    {
        if (!requireExplicit && request.PrivacyConsentAccepted is null && request.PrivacyConsentVersion is null) return null;
        if (request.PrivacyConsentAccepted is not true) return ResidentMessages.Text("error.privacyConsent", request.ResidentLanguage);
        return request.PrivacyConsentVersion != Version ? ResidentMessages.Text("error.privacyVersion", request.ResidentLanguage) : null;
    }
    public static ConsentReceipt Capture(IncidentRequest request, DateTimeOffset now) => new(Version, ResidentMessages.Text("privacy.purpose", request.ResidentLanguage),
        ResidentMessages.Text("privacy.notice", request.ResidentLanguage) + (request.ShareLocation ? " " + LocationNoticeVersion + ": " + ResidentMessages.Text("privacy.locationNotice", request.ResidentLanguage) : ""),
        request.PrivacyConsentAccepted is true, now,
        request.PrivacyConsentAccepted is true ? "explicit-resident-assertion; identity unverified" : "legacy-or-synthetic-unverified; no explicit consent supplied",
        request.ConsentToContact ? "service-updates-requested" : "no-contact-requested");
}
