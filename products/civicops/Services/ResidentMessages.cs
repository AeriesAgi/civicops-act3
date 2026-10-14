using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

// The same packaged resources drive the browser, public status, consent receipts and safe fallback drafts.
public static class ResidentMessages
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Resources = Load();
    public static string Normalize(string? language) => language?.ToLowerInvariant() switch
    {
        "af" or "afr" => "af",
        "zu" or "zul" => "zu",
        _ => "en"
    };
    public static string Text(string key, string? language = null) =>
        Resources[Normalize(language)].TryGetValue(key, out var message) ? message : Resources["en"][key];
    public static string Status(IncidentStatus status, string? language = null, bool draft = false) =>
        Text((draft ? "comms." : "update.") + (status is IncidentStatus.New or IncidentStatus.Triaged ? IncidentStatus.PendingApproval : status), language);
    public static string Draft(string reference, IncidentStatus status, string? language) =>
        Text("comms.reference", language).Replace("{reference}", reference, StringComparison.Ordinal)
            .Replace("{update}", Status(status, language, draft: true), StringComparison.Ordinal);
    public static string? Validation(string? error, string? language) =>
        error is null || Normalize(language) == "en" ? error : Text("error.invalidReport", language);

    private static Dictionary<string, IReadOnlyDictionary<string, string>> Load()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in new[] { "en", "af", "zu" })
        {
            using var stream = typeof(ResidentMessages).Assembly.GetManifestResourceStream($"CivicOps.Locales.{language}.json")
                ?? throw new InvalidOperationException($"Missing resident language resource: {language}.");
            using var document = JsonDocument.Parse(stream);
            result[language] = document.RootElement.GetProperty("strings").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
        }
        return result;
    }
}
