using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CivicOps.Services;

/// <summary>Bounded best-effort minimisation. Names need an explicit name label; this is not entity recognition.</summary>
public static partial class PiiRedactor
{
    public static string Redact(string? text) => RedactSafely(text, RedactPatterns);

    // A bounded matcher can time out even on ordinary input when a shared host is starved.
    // Discard the entire value in that case; neither an intake failure nor unredacted output is safe.
    internal static string RedactSafely(string? text, Func<string, string> redact)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try { return redact(text); }
        catch (RegexMatchTimeoutException) { return "[TEXT REDACTED: MATCH LIMIT]"; }
    }

    private static string RedactPatterns(string text)
    {
        var safe = Email().Replace(text, "[EMAIL REDACTED]");
        safe = IdentityNumber().Replace(safe, "[SA ID REDACTED]");
        safe = InternationalPhone().Replace(safe, "[PHONE REDACTED]");
        safe = Phone().Replace(safe, "[PHONE REDACTED]");
        return Name().Replace(safe, "$1[NAME REDACTED]");
    }
    public static JsonElement RedactJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return JsonSerializer.SerializeToElement(Redact(value.GetString()));
        var node = JsonNode.Parse(value.GetRawText());
        Visit(node);
        return JsonSerializer.SerializeToElement(node);
    }
    public static string Serialize(object value) => RedactJson(JsonSerializer.SerializeToElement(value)).GetRawText();
    private static void Visit(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var item in obj.ToArray())
            {
                if (item.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                    obj[item.Key] = FieldRedaction(item.Key) is { } marker && !string.IsNullOrWhiteSpace(text) ? marker : Redact(text);
                else Visit(item.Value);
            }
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[index] = Redact(text);
                else Visit(array[index]);
            }
    }
    private static string? FieldRedaction(string key) => key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant() switch
    {
        "NAME" or "FULLNAME" or "RESIDENTNAME" or "CONTACTNAME" or "CALLERNAME" or "DISPLAYNAME" => "[NAME REDACTED]",
        "PHONE" or "PHONENUMBER" or "CONTACTPHONE" or "MOBILE" => "[PHONE REDACTED]",
        "EMAIL" or "CONTACTEMAIL" => "[EMAIL REDACTED]",
        "IDENTITYNUMBER" or "SAID" => "[SA ID REDACTED]",
        _ => null
    };
    [GeneratedRegex(@"(?<![\w.+-])[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Email();
    [GeneratedRegex(@"(?<!\w)\d{13}(?!\w)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex IdentityNumber();
    [GeneratedRegex(@"(?<!\w)(?:\+27[\s().-]*|0)[1-8](?:[\s().-]*\d){8}(?!\w)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Phone();
    [GeneratedRegex(@"(?<!\w)(?:\+[1-9]|00[1-9])(?:[\s().-]*[0-9]){6,14}(?!\w)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex InternationalPhone();
    [GeneratedRegex(@"((?:my name is|contact name\s*:|resident name\s*:|name\s*:)\s+)[\p{L}][\p{L}'-]*(?:\s+[\p{L}][\p{L}'-]*){0,3}(?=[,.;\r\n]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex Name();
}
