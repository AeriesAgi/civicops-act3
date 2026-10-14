using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed class ChannelOptions
{
    public const string SyntheticSigningKey = "synthetic-channel-signing-key";
    public bool DemoMode { get; }
    public string Mode { get; }
    public string? IngressSecret { get; }
    public string? EncryptionKey { get; }
    public string? WebhookBaseUrl { get; }
    public bool SendsEnabled { get; }
    public int RetentionDays { get; }
    public static readonly IReadOnlyDictionary<string, string> Providers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["twilio-sms"] = "sms", ["clickatell"] = "sms", ["whatsapp"] = "whatsapp",
        ["twilio-voice"] = "voice", ["sip"] = "voice", ["elevenlabs"] = "voice"
    }.AsReadOnly();
    public ChannelOptions(IConfiguration configuration)
    {
        DemoMode = bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demo) && demo;
        var mode = configuration["CIVICOPS_CHANNEL_MODE"]?.Trim().ToLowerInvariant();
        if (mode is not (null or "" or "simulated" or "disabled" or "configured"))
            throw new InvalidOperationException("CIVICOPS_CHANNEL_MODE must be simulated, disabled or configured.");
        Mode = DemoMode ? "simulated" : string.IsNullOrEmpty(mode) ? "simulated" : mode;
        IngressSecret = DemoMode ? null : SecretValues.Read(configuration, "CIVICOPS_CHANNEL_INGRESS_SECRET");
        EncryptionKey = DemoMode ? null : SecretValues.Read(configuration, "CIVICOPS_CHANNEL_ENCRYPTION_KEY");
        WebhookBaseUrl = configuration["CIVICOPS_CHANNEL_WEBHOOK_BASE_URL"]?.TrimEnd('/');
        SendsEnabled = !DemoMode && Mode == "configured" && bool.TryParse(configuration["CIVICOPS_CHANNEL_SEND_ENABLED"], out var sends) && sends;
        RetentionDays = int.TryParse(configuration["CIVICOPS_CHANNEL_RETENTION_DAYS"], out var days) ? days : 30;
        if (RetentionDays is < 1 or > 30) throw new InvalidOperationException("Channel retention must be 1–30 days.");
        if (EncryptionKey is not null)
        {
            try { if (Convert.FromBase64String(EncryptionKey).Length != 32) throw new FormatException(); }
            catch (FormatException) { throw new InvalidOperationException("Channel encryption key must encode exactly 32 bytes."); }
        }
    }
    public bool CanReceiveNative => Mode == "configured" && EncryptionKey is not null && !string.IsNullOrWhiteSpace(IngressSecret)
        && Uri.TryCreate(WebhookBaseUrl, UriKind.Absolute, out var url) && url.Scheme == "https"
        && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0;
    public string PublicUrl(string provider, string pathBase)
    {
        if (!CanReceiveNative) throw new ChannelRequestException(503, "Signed channel ingress is disabled or incomplete.");
        var url = new Uri(WebhookBaseUrl!);
        if (url.AbsolutePath.TrimEnd('/') != pathBase.TrimEnd('/'))
            throw new ChannelRequestException(403, "The configured webhook URL does not belong to this municipality path.");
        return WebhookBaseUrl + "/api/v1/channels/webhooks/" + provider;
    }
}

public static class ChannelCommands
{
    public static string Canonical(string text)
    {
        var upper = string.Join(' ', text.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return upper switch
        {
            "UNSUBSCRIBE" or "HOU OP" or "STAAK" or "TEKEN UIT" or "YIMA" or "MISA" or "YEKA" or "UNGANGITHUMELI" => "STOP",
            "BEGIN" or "HERVAT" or "QALA" => "START",
            "SOS" or "HELP ME" or "HULP MY" or "NGISIZE" or "NGICELA USIZO" or "SIZA" => "SOS",
            "HULP" or "USIZO" => "HELP",
            "JA" or "YEBO" or "AGREE" => "YES",
            "RAPPORTEER" or "BIKA" => "REPORT",
            "MENS" or "UMUNTU" => "HUMAN",
            _ => upper
        };
    }
    public static string Language(string? value, string text = "")
    {
        if (value is "en" or "af" or "zu") return value;
        var lower = text.ToLowerInvariant();
        return lower.Contains("amanzi", StringComparison.Ordinal) || lower.Contains("ngicela", StringComparison.Ordinal) ? "zu"
            : lower.Contains("asseblief", StringComparison.Ordinal) || lower.Contains("straat", StringComparison.Ordinal) ? "af" : "en";
    }
    public static string Help(string language) => language switch
    {
        "af" => "CivicOps: REPORT om te rapporteer, STATUS <verwysing>, HELP, STOP / HOU OP, START / HERVAT. SOS: 10111 / 10177 / 112. 'n Mens besluit oor optrede.",
        "zu" => "CivicOps: REPORT ukubika, STATUS <ireferensi>, HELP / USIZO, STOP / YIMA, START / QALA. SOS: 10111 / 10177 / 112. Umuntu ugunyaza izenzo.",
        _ => "CivicOps: REPORT a problem, STATUS <reference>, HELP, STOP / UNSUBSCRIBE / STOPALL, START. SOS: 10111 / 10177 / 112. A human approves actions."
    };
    public static string Notice(string language) => language switch
    {
        "af" => "CivicOps privaatheid (" + PrivacyConsent.Version + "): jou munisipaliteit gebruik jou verslag om die diensprobleem te bestuur. Kontak en ligging is opsioneel. Antwoord YES / JA om in te stem; STOP / HOU OP stop boodskappe. Vermy sensitiewe persoonlike besonderhede. Vra die Inligtingsbeampte oor toegang of regstelling.",
        "zu" => "Ubumfihlo be-CivicOps (" + PrivacyConsent.Version + "): umasipala usebenzisa umbiko wakho ukuphatha inkinga yesevisi. Imininingwane yokuxhumana nendawo iyazikhethela. Phendula YES / YEBO ukuze uvume; STOP / YIMA ukumisa imiyalezo. Gwema imininingwane ebucayi. Cela i-Information Officer ukufinyelela noma ukulungisa.",
        _ => "CivicOps privacy (" + PrivacyConsent.Version + "): your municipality uses your report to assess and manage the service issue. Contact and report location are optional; no continuous tracking. Reply YES to agree, STOP to stop messages. Avoid sensitive personal details. Ask the Information Officer for access/correction. Replies are reviewed by municipal staff."
    };
}

public static partial class ChannelTemplates
{
    private static readonly string[] Languages = ["en", "af", "zu"];
    public static List<ChannelTemplate> Defaults()
    {
        var templates = new List<ChannelTemplate>();
        foreach (var language in Languages)
        {
            void Add(string key, string text) => templates.Add(new(key, language, 1, text, null, ReviewState(language)));
            Add("channel.notice", ChannelCommands.Notice(language));
            Add("case.received", language switch { "af" => "CivicOps het {reference} ontvang. Dit wag op menslike hersiening; geen aankomstyd word belowe nie.", "zu" => "I-CivicOps ithole {reference}. Ilinde ukubuyekezwa umuntu; asithembisi isikhathi sokufika.", _ => "CivicOps received {reference}. Awaiting human review; no arrival time is promised." });
            Add("case.update", language switch { "af" => "CivicOps {reference}: {update}", "zu" => "CivicOps {reference}: {update}", _ => "CivicOps {reference}: {update}" });
            Add("case.resolved", language switch { "af" => "CivicOps {reference} is as opgelos aangeteken. Kontak die munisipaliteit as die probleem voortduur.", "zu" => "CivicOps {reference} ibhalwe njengexazululiwe. Xhumana nomasipala uma inkinga iqhubeka.", _ => "CivicOps {reference} is recorded as resolved. Contact the municipality if the issue continues." });
            Add("case.request-info", language switch { "af" => "CivicOps {reference}: deel asseblief {request}. Ligging is opsioneel; vermy sensitiewe besonderhede.", "zu" => "CivicOps {reference}: sicela wabelane ngo-{request}. Indawo iyazikhethela; gwema imininingwane ebucayi.", _ => "CivicOps {reference}: please share {request}. Location is optional; avoid sensitive personal details." });
        }
        return templates;
    }
    public static string ReviewState(string language) => language == "en" ? "municipal and provider approval pending" : "starter translation — owner review pending";
    public static string Render(ChannelTemplate template, IReadOnlyDictionary<string, string> variables)
    {
        if (variables is null || variables.Count > 3 || variables.Keys.Any(key => key is not ("reference" or "update" or "request"))
            || variables.Values.Any(value => RequestValidation.Text(value, "template variable", 1000, 1) is not null))
            throw new ChannelRequestException(400, "Only reference, update and request variables are supported.");
        var text = template.Text;
        foreach (var key in Placeholder().Matches(template.Text).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal))
        {
            if (!variables.TryGetValue(key, out var value) || RequestValidation.Text(value, key, key == "reference" ? 80 : 1000, 1) is not null)
                throw new ChannelRequestException(400, "A bounded value is required for template variable " + key + ".");
            text = text.Replace("{" + key + "}", PiiRedactor.Redact(value), StringComparison.Ordinal);
        }
        if (text.Length > 4000) throw new ChannelRequestException(400, "Rendered message exceeds 4000 characters.");
        return text;
    }
    public static Dictionary<string, string> UsedVariables(ChannelTemplate template, IReadOnlyDictionary<string, string> variables) =>
        Placeholder().Matches(template.Text).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)
            .ToDictionary(key => key, key => variables[key], StringComparer.Ordinal);
    public static void Validate(string text)
    {
        if (RequestValidation.Text(text, "template text", 3000, 10) is { } error) throw new ChannelRequestException(400, error);
        if (Placeholder().Matches(text).Any(match => match.Groups[1].Value is not ("reference" or "update" or "request"))
            || Placeholder().Replace(text, "").Contains('{', StringComparison.Ordinal) || Placeholder().Replace(text, "").Contains('}', StringComparison.Ordinal))
            throw new ChannelRequestException(400, "Templates allow only {reference}, {update} and {request} placeholders.");
    }
    [GeneratedRegex(@"\{([a-zA-Z][a-zA-Z0-9_-]*)\}", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Placeholder();
}

public static class ChannelContactCrypto
{
    public static string Normalize(string contact)
    {
        if (string.IsNullOrWhiteSpace(contact))
            throw new ChannelRequestException(400, "A bounded phone number or unaddressable call/web identity is required.");
        if (contact.StartsWith("whatsapp:", StringComparison.Ordinal)) contact = contact[9..];
        if (contact.All(char.IsAsciiDigit) && contact.Length is >= 8 and <= 15) contact = "+" + contact;
        if (contact.StartsWith('+') && contact.Length is >= 9 and <= 16 && contact.AsSpan(1).ToArray().All(char.IsAsciiDigit)) return contact;
        if ((contact.StartsWith("call:", StringComparison.Ordinal) || contact.StartsWith("web:", StringComparison.Ordinal))
            && contact.Length is >= 6 and <= 200 && contact.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '_' or '-' or '.')) return contact;
        throw new ChannelRequestException(400, "A bounded phone number or unaddressable call/web identity is required.");
    }
    public static string Hash(byte[] key, string contact, string tenant) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("civicops-contact:" + tenant + ":" + contact)));
    public static string Protect(byte[] key, string value, string tenant)
    {
        var plaintext = Encoding.UTF8.GetBytes(value); var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(tenant));
        return Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
    }
    public static string Unprotect(byte[] key, string value, string tenant)
    {
        var bytes = Convert.FromBase64String(value); if (bytes.Length < 28) throw new InvalidDataException("Invalid encrypted channel contact.");
        var plaintext = new byte[bytes.Length - 28]; using var aes = new AesGcm(key, 16);
        aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext, Encoding.UTF8.GetBytes(tenant));
        return Encoding.UTF8.GetString(plaintext);
    }
    public static string Label(string normalized) => normalized.StartsWith('+')
        ? "Resident ••••" + normalized[^4..] : normalized.StartsWith("call:", StringComparison.Ordinal) ? "Unverified call" : "Unverified web report";
}
