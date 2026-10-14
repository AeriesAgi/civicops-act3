using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Globalization;
using System.Text;
using CivicOps.Models;

namespace CivicOps.Services;

public static class RequestValidation
{
    public const int DescriptionMax = 4000;
    public const int NoteMax = 2000;
    public static string? Text(string? value, string field, int max, int min = 0) =>
        (value?.Length ?? 0) > max || (value?.Trim().Length ?? 0) < min || UnsafeUnicode(value)
            ? $"{field} must contain {min}–{max} characters." : null;
    public static string? Intake(IncidentRequest r, string? key) => ResidentMessages.Validation(
        Text(r.Description, "description", DescriptionMax, 10) ?? Text(r.Area, "area", 200)
        ?? Text(r.ContactEmail, "contactEmail", 254) ?? Text(r.Source, "source", 40)
        ?? Text(r.SourceLabel, "sourceLabel", 80) ?? Text(r.LocationProvenance, "locationProvenance", 200)
        ?? Text(r.ResidentLanguage, "residentLanguage", 12) ?? Text(r.ContactPreference, "contactPreference", 20)
        ?? Text(r.ConversationId, "conversationId", 128) ?? Text(r.ContactPhone, "contactPhone", 40)
        ?? Text(r.ServiceCode, "serviceCode", 40) ?? Text(key, "Idempotency-Key", 200)
        ?? (r.Location is not null && (!r.ShareLocation || !GeoGeofencing.IsValidPoint(r.Location))
            ? "Location requires explicit sharing and finite coordinates inside the South African service bounds." : null)
        ?? (r.ShareLocation && r.Location is null ? "A shared location must provide both longitude and latitude." : null)
        ?? (r.Location is not null && (r.PrivacyConsentAccepted is not true || r.PrivacyConsentVersion != PrivacyConsent.Version)
            ? "Sharing a report location requires explicit acceptance of the current purpose notice." : null)
        ?? (key?.Any(char.IsControl) == true ? "Invalid Idempotency-Key." : null)
        ?? (!string.IsNullOrWhiteSpace(r.ResidentLanguage) && !AgentSchemas.Languages.Contains(r.ResidentLanguage, StringComparer.Ordinal) ? "Unsupported residentLanguage." : null)
        ?? (!string.IsNullOrWhiteSpace(r.ContactPreference) && !AgentSchemas.ContactPreferences.Contains(r.ContactPreference, StringComparer.Ordinal) ? "Unsupported contactPreference." : null)
        ?? (!string.IsNullOrWhiteSpace(r.ContactPhone) && (r.ContactPhone.Any(character => !char.IsAsciiDigit(character) && character is not ('+' or '-' or ' ' or '(' or ')'))
            || r.ContactPhone.Count(char.IsAsciiDigit) is < 7 or > 15) ? "Invalid contactPhone." : null)
        ?? (!string.IsNullOrWhiteSpace(r.ServiceCode) && ServiceCatalog.Find(r.ServiceCode) is null ? "Unknown serviceCode; choose a catalogue work-order or service alias." : null)
        ?? (!string.IsNullOrWhiteSpace(r.ContactEmail) && (!new EmailAddressAttribute().IsValid(r.ContactEmail) || r.ContactEmail.Contains('\r') || r.ContactEmail.Contains('\n')) ? "Invalid contactEmail." : null), r.ResidentLanguage);
    public static string? Action(string? actor, string? note, int? version) =>
        Text(actor, "approvedBy", 80) ?? Text(note, "note", NoteMax)
        ?? (version is <= 0 ? "expectedVersion must be positive." : null);
    public static string NormalizeArea(string area) => string.Join(' ', area.Normalize(NormalizationForm.FormC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool UnsafeUnicode(string? value)
    {
        if (value is null) return false;
        var marks = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (char.IsSurrogate(character))
            {
                if (!char.IsHighSurrogate(character) || i + 1 == value.Length || !char.IsLowSurrogate(value[++i])) return true;
                marks = 0; continue;
            }
            var category = char.GetUnicodeCategory(character);
            if (category == UnicodeCategory.Format || char.IsControl(character) && character is not ('\r' or '\n' or '\t')) return true;
            marks = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark ? marks + 1 : 0;
            if (marks > 8) return true;
        }
        return false;
    }

    public static bool Read(JsonElement payload, string[] names, out string? text, out string? error)
    {
        text = null; error = null;
        foreach (var name in names)
        {
            if (!payload.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.Null) return true;
            if (value.ValueKind != JsonValueKind.String) { error = $"{name} must be a string."; return false; }
            text = value.GetString(); return true;
        }
        return true;
    }
}

public sealed record ServiceCategory(string ServiceCode, string ServiceName, string Department, string WorkOrderTypeCode);
public static class ServiceCatalog
{
    // Legacy broad Open311 codes remain aliases; precise codes share the versioned catalogue.
    public static ServiceCategory[] All => WorkOrderCatalog.Default.WorkOrderTypes
        .GroupBy(type => type.ServiceCode).Select(group => new ServiceCategory(group.Key,
            group.First().Category, group.First().Department, group.First().Code))
        .Concat(WorkOrderCatalog.Default.WorkOrderTypes.Select(type => new ServiceCategory(type.Code,
            type.Category, type.Department, type.Code))).ToArray();
    public static ServiceCategory? Find(string? code) => All.FirstOrDefault(x => x.ServiceCode.Equals(code, StringComparison.OrdinalIgnoreCase));
}
