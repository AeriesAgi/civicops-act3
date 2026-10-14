using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CivicOps.Models;

namespace CivicOps.Services;

// These adapters normalize transient provider input and build requests. They never send requests,
// fetch media, retain raw webhook bodies, approve a case, or dispatch a responder.
public sealed record ChannelAdapterSettings(
    string IngressSecret = "", string NativeSecret = "", string ApiKey = "", string AccountId = "",
    string Sender = "", string PhoneNumberId = "", string VerifyToken = "", string AgentId = "",
    string PhoneProvider = "twilio");

public static class ChannelAdapters
{
    public static IInboundChannel CreateInbound(string provider, IConfiguration configuration, bool demo) =>
        provider switch
        {
            "twilio-sms" => new TwilioSmsChannel(Settings(provider, configuration, demo)),
            "twilio-voice" => new TwilioVoiceChannel(Settings(provider, configuration, demo)),
            "clickatell" or "clickatell-sms" => new ClickatellSmsChannel(Settings("clickatell", configuration, demo)),
            "whatsapp" => new WhatsAppCloudChannel(Settings(provider, configuration, demo)),
            "elevenlabs" => new ElevenLabsInboundChannel(Settings(provider, configuration, demo)),
            "sip" => new SipInboundChannel(Settings(provider, configuration, demo)),
            _ => throw new ChannelRequestException(400, "Unknown inbound provider.")
        };

    public static IOutboundChannel CreateOutbound(string provider, IConfiguration configuration, bool demo)
    {
        if (demo) throw new ChannelRequestException(503, "Live outbound adapters are disabled in EXPLICIT DEMO.");
        return provider switch
        {
            "twilio-sms" => new TwilioSmsChannel(Settings(provider, configuration, false)),
            "clickatell" or "clickatell-sms" => new ClickatellSmsChannel(Settings("clickatell", configuration, false)),
            "whatsapp" => new WhatsAppCloudChannel(Settings(provider, configuration, false)),
            "elevenlabs" => new ElevenLabsOutboundChannel(Settings(provider, configuration, false)),
            _ => throw new ChannelRequestException(503, "This provider has no enabled outbound transport.")
        };
    }

    private static ChannelAdapterSettings Settings(string provider, IConfiguration c, bool demo)
    {
        // Demo fixtures enter through the separate signed normalized simulator. Native routes fail
        // closed in demo even if real credentials happened to be exported by the host.
        if (demo) return new();
        var ingress = SecretValues.Read(c, "CIVICOPS_CHANNEL_INGRESS_SECRET") ?? "";
        return provider switch
        {
            "twilio-sms" or "twilio-voice" => new(ingress,
                SecretValues.Read(c, "CIVICOPS_TWILIO_AUTH_TOKEN") ?? "",
                AccountId: c["CIVICOPS_TWILIO_ACCOUNT_SID"] ?? "", Sender: c["CIVICOPS_TWILIO_FROM_NUMBER"] ?? ""),
            "clickatell" => new(ingress, SecretValues.Read(c, "CIVICOPS_CLICKATELL_CALLBACK_SECRET") ?? "",
                SecretValues.Read(c, "CIVICOPS_CLICKATELL_API_KEY") ?? "", Sender: c["CIVICOPS_CLICKATELL_FROM_NUMBER"] ?? ""),
            "whatsapp" => new(ingress, SecretValues.Read(c, "CIVICOPS_WHATSAPP_APP_SECRET") ?? "",
                SecretValues.Read(c, "CIVICOPS_WHATSAPP_ACCESS_TOKEN") ?? "", PhoneNumberId: c["CIVICOPS_WHATSAPP_PHONE_NUMBER_ID"] ?? "",
                VerifyToken: SecretValues.Read(c, "CIVICOPS_WHATSAPP_VERIFY_TOKEN") ?? ""),
            "elevenlabs" => new(ingress, SecretValues.Read(c, "ELEVENLABS_WEBHOOK_SECRET") ?? "",
                SecretValues.Read(c, "ELEVENLABS_API_KEY") ?? "", PhoneNumberId: c["ELEVENLABS_AGENT_PHONE_NUMBER_ID"] ?? "",
                AgentId: c["ELEVENLABS_CALLBACK_AGENT_ID"] ?? c["ELEVENLABS_AGENT_ID"] ?? "",
                PhoneProvider: c["ELEVENLABS_PHONE_PROVIDER"] ?? "twilio"),
            "sip" => new(ingress, SecretValues.Read(c, "CIVICOPS_SIP_CALLBACK_SECRET") ?? ""),
            _ => throw new ChannelRequestException(400, "Unknown provider.")
        };
    }
}

public static class ChannelSignature
{
    public const int MaximumBodyBytes = 256 * 1024;
    public const int MaximumAgeSeconds = 300;
    public const int MaximumFutureSeconds = 30;

    // Native Twilio/Meta callbacks have no signed delivery timestamp. An authenticated ingress
    // must verify the vendor first, remove caller-supplied CivicOps headers, then add this envelope.
    // PublicUrl is server-configured (and includes the municipal route), never caller Host.
    public static bool VerifyIngress(ChannelWebhookRequest request, string? secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret) || !ValidRequest(request)) return false;
        var timestamp = Header(request, "X-CivicOps-Timestamp");
        var nonce = Header(request, "X-CivicOps-Nonce");
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
            || unix < now.ToUnixTimeSeconds() - MaximumAgeSeconds
            || unix > now.ToUnixTimeSeconds() + MaximumFutureSeconds
            || nonce.Length is < 16 or > 128
            || nonce.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))) return false;
        var supplied = Header(request, "X-CivicOps-Signature");
        return VerifyHex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(Canonical(request, timestamp, nonce))), supplied, "sha256=");
    }

    public static string SignIngress(ChannelWebhookRequest request, string secret, DateTimeOffset at, string nonce)
    {
        var timestamp = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(Canonical(request, timestamp, nonce)))).ToLowerInvariant();
    }

    private static string Canonical(ChannelWebhookRequest request, string timestamp, string nonce) =>
        timestamp + "." + nonce + "." + request.Provider + "." + request.PublicUrl + "." + request.Body;

    public static bool VerifyTwilio(ChannelWebhookRequest request, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || !ValidRequest(request)) return false;
        IReadOnlyDictionary<string, string> values;
        try { values = ChannelPayload.Form(request); } catch (ChannelRequestException) { return false; }
        var input = new StringBuilder(request.PublicUrl);
        foreach (var item in values.OrderBy(item => item.Key, StringComparer.Ordinal)) input.Append(item.Key).Append(item.Value);
        // Twilio's documented native webhook protocol mandates HMAC-SHA1; the separate ingress
        // envelope uses HMAC-SHA256. Substituting SHA256 here would reject genuine Twilio input.
#pragma warning disable CA5350
        var expected = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(input.ToString()));
#pragma warning restore CA5350
        var signature = Header(request, "X-Twilio-Signature");
        if (signature.Length != 28) return false;
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(signature)); }
        catch (FormatException) { return false; }
    }

    public static bool VerifyMeta(ChannelWebhookRequest request, string? secret) =>
        !string.IsNullOrWhiteSpace(secret) && ValidRequest(request)
        && VerifyHex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.Body)),
            Header(request, "X-Hub-Signature-256"), "sha256=");

    public static bool EqualSecret(string? expected, string? supplied) =>
        !string.IsNullOrWhiteSpace(expected) && supplied is not null && supplied.Length <= 16384
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));

    public static string Header(ChannelWebhookRequest request, string name)
    {
        var matches = request.Headers.Where(item => item.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 && matches[0].Value.Length <= 16384 ? matches[0].Value : "";
    }

    private static bool VerifyHex(ReadOnlySpan<byte> expected, string supplied, string prefix)
    {
        if (!supplied.StartsWith(prefix, StringComparison.Ordinal) || supplied.Length != prefix.Length + expected.Length * 2) return false;
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(supplied[prefix.Length..])); }
        catch (FormatException) { return false; }
    }

    private static bool ValidRequest(ChannelWebhookRequest request) =>
        request.Body is not null && Encoding.UTF8.GetByteCount(request.Body) <= MaximumBodyBytes
        && !RequestValidation.UnsafeUnicode(request.Body)
        && request.Provider.Length is > 0 and <= 40
        && request.Provider.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && request.PublicUrl.Length is > 0 and <= 2048 && !request.PublicUrl.Any(char.IsControl)
        && request.Headers.Count <= 64;
}

internal static class ChannelPayload
{
    public static void Authenticate(ChannelWebhookRequest request, string provider, ChannelAdapterSettings settings,
        DateTimeOffset now, Func<bool>? native = null)
    {
        if (request.Provider != provider || !ChannelSignature.VerifyIngress(request, settings.IngressSecret, now)
            || native is not null && !native()) throw new ChannelRequestException(401, "Invalid provider signature or signed ingress envelope.");
    }

    public static IReadOnlyDictionary<string, string> Form(ChannelWebhookRequest request)
    {
        if (!request.ContentType.Split(';', 2)[0].Trim().Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            throw new ChannelRequestException(400, "Send a form-encoded provider webhook.");
        if (Encoding.UTF8.GetByteCount(request.Body) > ChannelSignature.MaximumBodyBytes)
            throw new ChannelRequestException(400, "Webhook body exceeds its bound.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in request.Body.Split('&'))
        {
            if (part.Length == 0) continue;
            var split = part.Split('=', 2);
            foreach (var encoded in split)
                for (var i = 0; i < encoded.Length; i++)
                    if (encoded[i] == '%' && (i + 2 >= encoded.Length || !Uri.IsHexDigit(encoded[i + 1]) || !Uri.IsHexDigit(encoded[i + 2])))
                        throw new ChannelRequestException(400, "Invalid provider form encoding.");
            var key = WebUtility.UrlDecode(split[0]);
            var value = WebUtility.UrlDecode(split.Length == 2 ? split[1] : "");
            if (values.Count >= 64 || RequestValidation.Text(key, "field", 128, 1) is not null
                || RequestValidation.Text(value, "value", 16384) is not null || key.Contains('\uFFFD') || value.Contains('\uFFFD')
                || !values.TryAdd(key, value)) throw new ChannelRequestException(400, "Invalid or duplicate provider form field.");
        }
        return values;
    }

    public static JsonDocument Json(ChannelWebhookRequest request)
    {
        if (!request.ContentType.Split(';', 2)[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            throw new ChannelRequestException(400, "Send a JSON provider webhook.");
        JsonDocument document;
        try { document = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new ChannelRequestException(400, "Invalid provider JSON."); }
        try { ValidateJson(document.RootElement); }
        catch { document.Dispose(); throw; }
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        { document.Dispose(); throw new ChannelRequestException(400, "Provider webhook must be an object."); }
        return document;
    }

    private static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (names.Count >= 512 || !names.Add(property.Name) || RequestValidation.Text(property.Name, "field", 128, 1) is not null)
                    throw new ChannelRequestException(400, "Invalid or duplicate provider JSON field.");
                ValidateJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() > 500) throw new ChannelRequestException(400, "Provider array exceeds its bound.");
            foreach (var value in element.EnumerateArray()) ValidateJson(value);
        }
    }

    public static string Required(JsonElement element, string name, int maximum = 200) =>
        Bounded(Text(element, name), name, maximum, 1);
    public static string Bounded(string? value, string field, int maximum, int minimum = 0)
    {
        if (RequestValidation.Text(value, field, maximum, minimum) is not null)
            throw new ChannelRequestException(400, "Invalid or oversized " + field + ".");
        return value ?? "";
    }
    public static string Identifier(string? value, string field, int maximum = 200)
    {
        var identifier = Bounded(value, field, maximum, 1);
        if (identifier.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.' or ':' or '@' or '+' or '/' or '=')))
            throw new ChannelRequestException(400, "Invalid provider identifier.");
        return identifier;
    }
    public static string MediaReference(string provider, string identifier, int? index = null)
    {
        // Only opaque identifiers leave the adapter. Unusual provider encoding becomes a
        // stable correlation hash; it never becomes a fetchable URL or a stored media blob.
        var safe = identifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? identifier
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier))).ToLowerInvariant();
        return provider + "." + safe + (index is null ? "" : "." + index.Value.ToString(CultureInfo.InvariantCulture));
    }
    public static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ChannelRequestException(400, name + " must be text.");
        return value.GetString();
    }
    public static string FormValue(IReadOnlyDictionary<string, string> values, string name, int maximum, bool required = true) =>
        Bounded(values.GetValueOrDefault(name), name, maximum, required ? 1 : 0);
    public static JsonElement Object(JsonElement element, string name, bool required = true)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
        {
            if (value.ValueKind == JsonValueKind.Object) return value;
            if (required || value.ValueKind != JsonValueKind.Null) throw new ChannelRequestException(400, name + " must be an object.");
        }
        if (!required) return default;
        throw new ChannelRequestException(400, name + " must be an object.");
    }
    public static JsonElement Array(JsonElement element, string name, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum)
            throw new ChannelRequestException(400, "Invalid or oversized " + name + ".");
        return value;
    }
    public static bool? Boolean(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            throw new ChannelRequestException(400, name + " must be a boolean.");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean();
    }
    public static string Phone(string? number)
    {
        var value = Bounded(number, "phone address", 16, 7);
        var digits = value.StartsWith('+') ? value[1..] : value;
        if (digits.Length is < 7 or > 15 || digits.Any(c => !char.IsAsciiDigit(c)))
            throw new ChannelRequestException(400, "Provider phone address must use international digits.");
        return "+" + digits;
    }
    public static string Language(string? declared, string text)
    {
        if (declared is "en" or "af" or "zu") return declared;
        var words = (" " + text.ToLowerInvariant() + " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(word => word is "amanzi" or "ugesi" or "umgwaqo" or "siza" or "yeka")) return "zu";
        if (words.Any(word => word is "asseblief" or "straat" or "waterlek" or "krag" or "staak")) return "af";
        return "en"; // A guess, never an identity or confidence score.
    }
    public static HttpRequestMessage JsonRequest(string uri, JsonObject body) =>
        new(HttpMethod.Post, uri) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    public static void Outbound(OutboundMessage message, string provider, string channel)
    {
        if (message.Provider != provider || message.Channel != channel || message.TemplateVersion < 1
            || RequestValidation.Text(message.Text, "text", 4000, 1) is not null
            || message.Language is not ("en" or "af" or "zu"))
            throw new ChannelRequestException(400, "Invalid outbound channel, language, template or text.");
        Phone(message.To);
    }
    public static void Configured(params string[] values)
    {
        if (values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 16384 || value.Any(char.IsControl)))
            throw new ChannelRequestException(503, "Provider transport is not configured or its credentials are invalid.");
    }
}

public sealed class TwilioSmsChannel(ChannelAdapterSettings settings) : IInboundChannel, IOutboundChannel
{
    public string Provider => "twilio-sms";
    public IReadOnlyList<InboundMessage> VerifyAndParse(ChannelWebhookRequest request, DateTimeOffset now)
    {
        ChannelPayload.Configured(settings.IngressSecret, settings.NativeSecret, settings.AccountId, settings.Sender);
        ChannelPayload.Authenticate(request, Provider, settings, now, () => ChannelSignature.VerifyTwilio(request, settings.NativeSecret));
        var form = ChannelPayload.Form(request);
        var id = ChannelPayload.Identifier(form.GetValueOrDefault("MessageSid"), "MessageSid", 128);
        var from = ChannelPayload.Phone(ChannelPayload.FormValue(form, "From", 16));
        var to = ChannelPayload.Phone(ChannelPayload.FormValue(form, "To", 16));
        CheckAccount(form, to, settings);
        var text = ChannelPayload.FormValue(form, "Body", 4000, false);
        var media = new List<string>();
        if (form.TryGetValue("NumMedia", out var count))
        {
            if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total is < 0 or > 10)
                throw new ChannelRequestException(400, "Invalid media count.");
            for (var i = 0; i < total; i++)
                if (form.GetValueOrDefault("MediaContentType" + i) is "image/jpeg" or "image/png" or "application/pdf")
                    media.Add(ChannelPayload.MediaReference("twilio", id, i));
        }
        if (text.Length == 0 && media.Count == 0) throw new ChannelRequestException(400, "SMS must contain text or a supported media reference.");
        if (text.Length == 0) text = "Resident supplied a media reference for human review.";
        return [new("sms", Provider, from, to, text, ChannelPayload.Language(form.GetValueOrDefault("Language"), text), now, id, media)];
    }

    internal static void CheckAccount(IReadOnlyDictionary<string, string> form, string to, ChannelAdapterSettings settings)
    {
        if (settings.AccountId.Length > 0 && form.GetValueOrDefault("AccountSid") != settings.AccountId
            || settings.Sender.Length > 0 && ChannelPayload.Phone(settings.Sender) != to)
            throw new ChannelRequestException(401, "Webhook does not belong to the configured provider account or number.");
    }

    public HttpRequestMessage BuildRequest(OutboundMessage message, DateTimeOffset now)
    {
        ChannelPayload.Outbound(message, Provider, "sms");
        ChannelPayload.Configured(settings.AccountId, settings.NativeSecret, settings.Sender);
        if (settings.AccountId.Length != 34 || !settings.AccountId.StartsWith("AC", StringComparison.Ordinal)
            || settings.AccountId[2..].Any(c => !Uri.IsHexDigit(c))) throw new ChannelRequestException(503, "Invalid Twilio account configuration.");
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twilio.com/2010-04-01/Accounts/" + settings.AccountId + "/Messages.json")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["From"] = ChannelPayload.Phone(settings.Sender), ["To"] = ChannelPayload.Phone(message.To), ["Body"] = message.Text
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.AccountId + ":" + settings.NativeSecret)));
        return request;
    }
}

public sealed class TwilioVoiceChannel(ChannelAdapterSettings settings) : IInboundChannel
{
    public string Provider => "twilio-voice";
    public IReadOnlyList<InboundMessage> VerifyAndParse(ChannelWebhookRequest request, DateTimeOffset now)
    {
        ChannelPayload.Configured(settings.IngressSecret, settings.NativeSecret, settings.AccountId, settings.Sender);
        ChannelPayload.Authenticate(request, Provider, settings, now, () => ChannelSignature.VerifyTwilio(request, settings.NativeSecret));
        var form = ChannelPayload.Form(request);
        var conversation = ChannelPayload.Identifier(form.GetValueOrDefault("CallSid"), "CallSid", 128);
        var caller = ChannelPayload.FormValue(form, "From", 16);
        var withheld = caller is "anonymous" or "restricted" or "unavailable";
        var from = withheld ? "call:" + conversation : ChannelPayload.Phone(caller);
        var to = ChannelPayload.Phone(ChannelPayload.FormValue(form, "To", 16));
        TwilioSmsChannel.CheckAccount(form, to, settings);
        var speech = ChannelPayload.FormValue(form, "SpeechResult", 4000, false);
        var digits = ChannelPayload.FormValue(form, "Digits", 1, false);
        var text = speech.Length > 0 ? speech : digits switch
        { "2" => "STATUS", "3" => "HUMAN", "4" => "CALLBACK", "9" => "SOS", _ => "REPORT" };
        if (digits.Length > 0 && digits is not ("1" or "2" or "3" or "4" or "9"))
            throw new ChannelRequestException(400, "Unknown IVR selection.");
        var eventId = conversation + ":" + (speech.Length == 0 && digits.Length == 0 ? "incoming"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(speech + "\n" + digits)))[..24].ToLowerInvariant());
        return [new("voice", Provider, from, to, text, ChannelPayload.Language(form.GetValueOrDefault("Language"), text), now, eventId,
            ProviderConversationId: conversation, TranscriptSummary: speech.Length == 0 ? null : speech,
            HumanHandoffRequested: withheld || digits == "3" || speech.Length == 0 && digits.Length == 0, CallbackRequested: digits == "4" && !withheld)];
    }

    public static string BuildVoiceResponse(string actionUrl, bool simulated, string? sipAddress = null)
    {
        if (!Uri.TryCreate(actionUrl, UriKind.Absolute, out var action) || action.Scheme != "https" || action.UserInfo.Length > 0
            || actionUrl.Length > 2048 || actionUrl.Any(char.IsControl)) throw new ChannelRequestException(400, "IVR action requires a trusted HTTPS URL.");
        var notice = "CivicOps municipal intake. Privacy notice " + PrivacyConsent.Version
            + ". We use your report and number only to manage your case. No audio is stored by CivicOps. "
            + "Say YES to accept this processing notice before describing your report. Contact and location sharing are optional. "
            + "For immediate emergencies call 10111, 10177, or 112. A human approves all safety and dispatch decisions. ";
        var root = new XElement("Response", new XElement("Say", notice + (simulated ? "This is a simulated IVR exercise. " : "")));
        if (!simulated && !string.IsNullOrWhiteSpace(sipAddress))
        {
            if (!sipAddress.StartsWith("sip:", StringComparison.Ordinal) || sipAddress.Length is < 5 or > 200
                || sipAddress[4..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('@' or '.' or '_' or '-' or ':')))
                throw new ChannelRequestException(503, "Configured agent SIP destination is invalid.");
            root.Add(new XElement("Dial", new XElement("Sip", sipAddress)));
        }
        else
        {
            root.Add(new XElement("Gather", new XAttribute("input", "dtmf speech"), new XAttribute("numDigits", "1"),
                new XAttribute("action", actionUrl), new XAttribute("method", "POST"), new XAttribute("timeout", "7"),
                new XElement("Say", "Press 1 to report a problem. Press 2 to check a case. Press 3 for a human call-taker. "
                    + "Press 4 to request a callback. Press 9 for emergency information. Or describe the problem.")));
            root.Add(new XElement("Say", "No response received. Please contact the municipal call centre. No responder was dispatched."));
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }
}

public sealed class ClickatellSmsChannel(ChannelAdapterSettings settings) : IInboundChannel, IOutboundChannel
{
    public string Provider => "clickatell";
    public IReadOnlyList<InboundMessage> VerifyAndParse(ChannelWebhookRequest request, DateTimeOffset now)
    {
        ChannelPayload.Configured(settings.IngressSecret, settings.NativeSecret, settings.Sender);
        ChannelPayload.Authenticate(request, Provider, settings, now, () => ChannelSignature.EqualSecret(settings.NativeSecret,
            ChannelSignature.Header(request, "X-CivicOps-Clickatell-Token")));
        using var document = ChannelPayload.Json(request);
        var data = document.RootElement;
        var id = ChannelPayload.Identifier(ChannelPayload.Text(data, "replyMessageId"), "replyMessageId");
        var from = ChannelPayload.Phone(ChannelPayload.Required(data, "fromNumber", 16));
        var to = ChannelPayload.Phone(ChannelPayload.Required(data, "toNumber", 16));
        if (settings.Sender.Length > 0 && ChannelPayload.Phone(settings.Sender) != to)
            throw new ChannelRequestException(401, "Webhook does not belong to the configured number.");
        var text = ChannelPayload.Required(data, "text", 4000);
        return [new("sms", Provider, from, to, text, ChannelPayload.Language(ChannelPayload.Text(data, "language"), text), now, id)];
    }
    public HttpRequestMessage BuildRequest(OutboundMessage message, DateTimeOffset now)
    {
        ChannelPayload.Outbound(message, Provider, "sms");
        ChannelPayload.Configured(settings.ApiKey, settings.Sender);
        var request = ChannelPayload.JsonRequest("https://platform.clickatell.com/messages",
            new JsonObject { ["content"] = message.Text, ["to"] = new JsonArray(ChannelPayload.Phone(message.To).TrimStart('+')),
                ["from"] = ChannelPayload.Phone(settings.Sender).TrimStart('+'), ["charset"] = "UTF-8",
                ["clientMessageId"] = "civicops-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    message.To + "\n" + message.Text + "\n" + message.TemplateVersion)))[..32].ToLowerInvariant() });
        request.Headers.TryAddWithoutValidation("Authorization", settings.ApiKey);
        return request;
    }
}

public sealed class WhatsAppCloudChannel(ChannelAdapterSettings settings) : IInboundChannel, IOutboundChannel
{
    public const string GraphVersion = "v25.0";
    public string Provider => "whatsapp";
    public bool VerifyChallenge(string? mode, string? token, string? challenge) =>
        mode == "subscribe" && challenge is { Length: > 0 and <= 256 } && !challenge.Any(char.IsControl)
        && ChannelSignature.EqualSecret(settings.VerifyToken, token);

    public IReadOnlyList<InboundMessage> VerifyAndParse(ChannelWebhookRequest request, DateTimeOffset now)
    {
        ChannelPayload.Configured(settings.IngressSecret, settings.NativeSecret, settings.PhoneNumberId);
        ChannelPayload.Authenticate(request, Provider, settings, now, () => ChannelSignature.VerifyMeta(request, settings.NativeSecret));
        using var document = ChannelPayload.Json(request);
        var root = document.RootElement;
        if (ChannelPayload.Text(root, "object") != "whatsapp_business_account") throw new ChannelRequestException(400, "Unknown WhatsApp webhook object.");
        var output = new List<InboundMessage>();
        foreach (var entry in ChannelPayload.Array(root, "entry", 10).EnumerateArray())
        foreach (var change in ChannelPayload.Array(entry, "changes", 10).EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object) throw new ChannelRequestException(400, "WhatsApp change must be an object.");
            if (ChannelPayload.Text(change, "field") != "messages") continue;
            var value = ChannelPayload.Object(change, "value");
            var metadata = ChannelPayload.Object(value, "metadata");
            var phoneId = ChannelPayload.Identifier(ChannelPayload.Text(metadata, "phone_number_id"), "phone_number_id", 32);
            if (settings.PhoneNumberId.Length > 0 && phoneId != settings.PhoneNumberId)
                throw new ChannelRequestException(401, "WhatsApp webhook does not belong to the configured phone number.");
            var to = ChannelPayload.Bounded(ChannelPayload.Text(metadata, "display_phone_number") ?? phoneId, "destination", 32, 1);
            if (!value.TryGetProperty("messages", out _)) continue; // Delivery/status callbacks do not create resident reports.
            foreach (var message in ChannelPayload.Array(value, "messages", 50).EnumerateArray())
            {
                if (output.Count >= 50) throw new ChannelRequestException(400, "Too many inbound messages in one webhook.");
                var id = ChannelPayload.Identifier(ChannelPayload.Text(message, "id"), "id");
                var timestamp = ChannelPayload.Required(message, "timestamp", 16);
                if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)
                    || unix < now.AddDays(-30).ToUnixTimeSeconds() || unix > now.AddSeconds(30).ToUnixTimeSeconds())
                    throw new ChannelRequestException(400, "WhatsApp message timestamp is invalid or outside the retained intake window.");
                // The raw-body native signature covers this resident-event time. A delayed first
                // delivery must not reopen the provider's 24-hour free-text window.
                var receivedAt = DateTimeOffset.FromUnixTimeSeconds(unix);
                var from = ChannelPayload.Phone(ChannelPayload.Required(message, "from", 16));
                var type = ChannelPayload.Required(message, "type", 32);
                string text;
                string? area = null;
                GeoPoint? point = null;
                var media = new List<string>();
                var handoff = false;
                switch (type)
                {
                    case "text":
                        text = ChannelPayload.Required(ChannelPayload.Object(message, "text"), "body", 4000);
                        break;
                    case "button":
                        var button = ChannelPayload.Object(message, "button");
                        text = Command(ChannelPayload.Required(button, "payload", 200), ChannelPayload.Text(button, "text"));
                        break;
                    case "interactive":
                        var interactive = ChannelPayload.Object(message, "interactive");
                        var subtype = ChannelPayload.Required(interactive, "type", 32);
                        if (subtype is not ("button_reply" or "list_reply")) throw new ChannelRequestException(400, "Unsupported interactive response.");
                        var reply = ChannelPayload.Object(interactive, subtype);
                        text = Command(ChannelPayload.Required(reply, "id", 200), ChannelPayload.Text(reply, "title"));
                        break;
                    case "location":
                        var location = ChannelPayload.Object(message, "location");
                        if (!location.TryGetProperty("longitude", out var longitude) || longitude.ValueKind != JsonValueKind.Number || !longitude.TryGetDouble(out var lng)
                            || !location.TryGetProperty("latitude", out var latitude) || latitude.ValueKind != JsonValueKind.Number || !latitude.TryGetDouble(out var lat))
                            throw new ChannelRequestException(400, "Location requires finite longitude and latitude.");
                        point = new(lng, lat);
                        if (!GeoGeofencing.IsValidPoint(point)) throw new ChannelRequestException(400, "Location is outside municipal service bounds.");
                        area = ChannelPayload.Bounded(ChannelPayload.Text(location, "name") ?? ChannelPayload.Text(location, "address"), "area", 200);
                        text = "Resident shared a report location. Human review requires the report description.";
                        break;
                    case "image":
                    case "document":
                        var attachment = ChannelPayload.Object(message, type);
                        media.Add(ChannelPayload.MediaReference("whatsapp",
                            ChannelPayload.Identifier(ChannelPayload.Text(attachment, "id"), "media id", 128)));
                        text = ChannelPayload.Bounded(ChannelPayload.Text(attachment, "caption"), "caption", 4000);
                        if (text.Length == 0) text = "Resident supplied a media reference for human review.";
                        break;
                    case "audio":
                    case "video":
                        text = "Resident supplied unsupported media. A human call-taker is required; CivicOps did not fetch or retain the media.";
                        handoff = true;
                        break;
                    default: throw new ChannelRequestException(400, "Unsupported WhatsApp message type.");
                }
                output.Add(new("whatsapp", Provider, from, to, text, ChannelPayload.Language(null, text), receivedAt, id, media,
                    Location: point, LocationShared: point is not null, Area: area, HumanHandoffRequested: handoff));
            }
        }
        return output;
    }
    private static string Command(string id, string? title) => id.ToLowerInvariant() switch
    {
        "report_problem" or "report" => "REPORT", "check_case" or "status" => "STATUS", "sos_info" or "sos" => "SOS",
        _ => ChannelPayload.Bounded(title ?? id, "button response", 4000, 1)
    };
    public HttpRequestMessage BuildRequest(OutboundMessage message, DateTimeOffset now)
    {
        ChannelPayload.Outbound(message, Provider, "whatsapp");
        ChannelPayload.Configured(settings.ApiKey, settings.PhoneNumberId);
        if (settings.PhoneNumberId.Length > 32 || settings.PhoneNumberId.Any(c => !char.IsAsciiDigit(c)))
            throw new ChannelRequestException(503, "Invalid WhatsApp phone-number configuration.");
        var body = new JsonObject { ["messaging_product"] = "whatsapp", ["recipient_type"] = "individual",
            ["to"] = ChannelPayload.Phone(message.To).TrimStart('+') };
        var inWindow = message.LastResidentMessageAt is { } last && last <= now && now - last < TimeSpan.FromHours(24);
        if (!inWindow || !string.IsNullOrWhiteSpace(message.ProviderTemplateName))
        {
            if (message.Interactive) throw new ChannelRequestException(400, "Interactive replies require an open 24-hour resident window.");
            var name = message.ProviderTemplateName;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '_'))
                throw new ChannelRequestException(409, "Outside the 24-hour window, a provider-approved template is required.");
            var parameters = new JsonArray();
            foreach (var variable in (message.Variables ?? new Dictionary<string, string>()).OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                if (parameters.Count >= 16 || RequestValidation.Text(variable.Key, "variable", 64, 1) is not null)
                    throw new ChannelRequestException(400, "Invalid or oversized template variables.");
                parameters.Add(new JsonObject { ["type"] = "text", ["text"] = ChannelPayload.Bounded(variable.Value, "variable", 1000, 1) });
            }
            body["type"] = "template";
            body["template"] = new JsonObject { ["name"] = name, ["language"] = new JsonObject { ["code"] = message.Language },
                ["components"] = parameters.Count == 0 ? new JsonArray() : new JsonArray(
                    new JsonObject { ["type"] = "body", ["parameters"] = parameters }) };
        }
        else if (message.Interactive)
        {
            var titles = message.Language switch
            {
                "af" => new[] { "Meld 'n probleem", "Volg my saak", "SOS-inligting" },
                "zu" => new[] { "Bika inkinga", "Hlola icala lami", "Ulwazi lwe-SOS" },
                _ => new[] { "Report a problem", "Check my case", "SOS info" }
            };
            var buttons = new JsonArray();
            var ids = new[] { "report_problem", "check_case", "sos_info" };
            for (var i = 0; i < ids.Length; i++) buttons.Add(new JsonObject { ["type"] = "reply",
                ["reply"] = new JsonObject { ["id"] = ids[i], ["title"] = titles[i] } });
            body["type"] = "interactive";
            body["interactive"] = new JsonObject { ["type"] = "button", ["body"] = new JsonObject { ["text"] = message.Text },
                ["action"] = new JsonObject { ["buttons"] = buttons } };
        }
        else { body["type"] = "text"; body["text"] = new JsonObject { ["body"] = message.Text, ["preview_url"] = false }; }
        var request = ChannelPayload.JsonRequest("https://graph.facebook.com/" + GraphVersion + "/" + settings.PhoneNumberId + "/messages", body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        return request;
    }
}

public sealed class ElevenLabsInboundChannel(ChannelAdapterSettings settings) : IInboundChannel
{
    public string Provider => "elevenlabs";
    public IReadOnlyList<InboundMessage> VerifyAndParse(ChannelWebhookRequest request, DateTimeOffset now)
    {
        ChannelPayload.Configured(settings.IngressSecret, settings.NativeSecret);
        ChannelPayload.Authenticate(request, Provider, settings, now, () => ElevenLabsSignature.Verify(Encoding.UTF8.GetBytes(request.Body),
            ChannelSignature.Header(request, "ElevenLabs-Signature"), settings.NativeSecret, now));
        using var document = ChannelPayload.Json(request);
        var root = document.RootElement;
        var type = ChannelPayload.Required(root, "type", 64);
        if (type is not ("post_call_transcription" or "post_call_audio" or "call_initiation_failure"))
            throw new ChannelRequestException(400, "Unknown ElevenLabs post-call event.");
        var data = ChannelPayload.Object(root, "data");
        var conversation = ChannelPayload.Identifier(ChannelPayload.Text(data, "conversation_id"), "conversation_id", 128);
        if (type == "post_call_audio") return []; // Verify and acknowledge without retaining or decoding audio.
        var metadata = ChannelPayload.Object(data, "metadata", false);
        var phone = ChannelPayload.Object(metadata, "phone_call", false);
        var initiation = ChannelPayload.Object(data, "conversation_initiation_client_data", false);
        var variables = ChannelPayload.Object(initiation, "dynamic_variables", false);
        var caller = ChannelPayload.Text(phone, "from_number") ?? ChannelPayload.Text(variables, "system__caller_id")
            ?? ChannelPayload.Text(variables, "contact_phone");
        var from = caller is null ? "call:" + conversation : ChannelPayload.Phone(caller);
        var to = ChannelPayload.Bounded(ChannelPayload.Text(phone, "to_number") ?? settings.PhoneNumberId ?? "", "destination", 128);
        var analysis = ChannelPayload.Object(data, "analysis", false);
        var collected = ChannelPayload.Object(analysis, "data_collection_results", false);
        var summary = ChannelPayload.Bounded(ChannelPayload.Text(analysis, "transcript_summary"), "transcript summary", 4000);
        var description = CollectedText(collected, "description") ?? CollectedText(collected, "what") ?? summary;
        var handoff = CollectedBoolean(collected, "human_handoff_requested") == true;
        if (type == "call_initiation_failure")
        { description = "Call initiation failed; a human call-taker must review the callback request."; summary = description; handoff = true; }
        if (string.IsNullOrWhiteSpace(description))
        { description = "Voice conversation ended without a structured report; human call-taker review required."; handoff = true; }
        description = ChannelPayload.Bounded(description, "description", 4000, 1);
        return [new("voice", Provider, from, to, description, ChannelPayload.Language(CollectedText(collected, "language"), description),
            now, type + ":" + conversation, ProviderConversationId: conversation, Area: CollectedText(collected, "area"),
            TranscriptSummary: summary.Length == 0 ? null : summary, HumanHandoffRequested: handoff,
            CallbackRequested: CollectedBoolean(collected, "callback_requested") == true,
            PrivacyConsentAccepted: CollectedBoolean(collected, "privacy_consent_accepted"),
            PrivacyConsentVersion: CollectedText(collected, "privacy_consent_version"))];
    }
    private static string? CollectedText(JsonElement collection, string name)
    {
        if (collection.ValueKind != JsonValueKind.Object || !collection.TryGetProperty(name, out var field)) return null;
        return field.ValueKind == JsonValueKind.String ? ChannelPayload.Bounded(field.GetString(), name, name == "description" || name == "what" ? 4000 : 200)
            : ChannelPayload.Bounded(ChannelPayload.Text(field, "value"), name, name == "description" || name == "what" ? 4000 : 200);
    }
    private static bool? CollectedBoolean(JsonElement collection, string name)
    {
        if (collection.ValueKind != JsonValueKind.Object || !collection.TryGetProperty(name, out var field)) return null;
        if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("value", out var value)) field = value;
        return field.ValueKind switch
        { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null,
            _ => throw new ChannelRequestException(400, name + " must be a boolean.") };
    }
}

public sealed class ElevenLabsOutboundChannel(ChannelAdapterSettings settings) : IOutboundChannel
{
    public string Provider => "elevenlabs";
    public HttpRequestMessage BuildRequest(OutboundMessage message, DateTimeOffset now)
    {
        ChannelPayload.Outbound(message, Provider, "voice");
        ChannelPayload.Configured(settings.ApiKey, settings.AgentId, settings.PhoneNumberId);
        if (settings.PhoneProvider is not ("twilio" or "sip-trunk"))
            throw new ChannelRequestException(503, "Invalid configured callback phone provider.");
        var variables = new JsonObject { ["public_update"] = message.Text, ["resident_language"] = message.Language };
        foreach (var item in message.Variables ?? new Dictionary<string, string>())
        {
            if (variables.Count >= 18 || item.Key is not ("case_reference" or "case_status"))
                throw new ChannelRequestException(400, "Unsupported callback variable.");
            variables[item.Key] = ChannelPayload.Bounded(item.Value, "callback variable", 200, 1);
        }
        var request = ChannelPayload.JsonRequest("https://api.elevenlabs.io/v1/convai/" + settings.PhoneProvider + "/outbound-call",
            new JsonObject { ["agent_id"] = ChannelPayload.Bounded(settings.AgentId, "agent id", 128, 1),
                ["agent_phone_number_id"] = ChannelPayload.Bounded(settings.PhoneNumberId, "phone id", 128, 1),
                ["to_number"] = ChannelPayload.Phone(message.To),
                ["conversation_initiation_client_data"] = new JsonObject { ["dynamic_variables"] = variables } });
        request.Headers.TryAddWithoutValidation("xi-api-key", settings.ApiKey);
        return request;
    }
}

// A deliberately narrow owner-operated SIP gateway contract. CivicOps does not listen to SIP/RTP
// or provide a PBX, recording, trunk provisioning or outbound SIP transport.
public sealed class SipInboundChannel(ChannelAdapterSettings settings) : IInboundChannel
{
    public string Provider => "sip";
    public IReadOnlyList<InboundMessage> VerifyAndParse(ChannelWebhookRequest request, DateTimeOffset now)
    {
        ChannelPayload.Configured(settings.IngressSecret, settings.NativeSecret);
        ChannelPayload.Authenticate(request, Provider, settings, now, () => ChannelSignature.EqualSecret(settings.NativeSecret,
            ChannelSignature.Header(request, "X-CivicOps-Sip-Token")));
        using var document = ChannelPayload.Json(request);
        var root = document.RootElement;
        var allowed = new HashSet<string>(["event", "call_id", "from", "to", "text", "language", "area",
            "human_handoff_requested", "callback_requested", "privacy_consent_accepted", "privacy_consent_version"], StringComparer.Ordinal);
        if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            throw new ChannelRequestException(400, "Unknown SIP gateway field.");
        var kind = ChannelPayload.Required(root, "event", 32);
        if (kind is not ("call_started" or "speech_summary" or "call_failed")) throw new ChannelRequestException(400, "Unknown SIP gateway event.");
        var conversation = ChannelPayload.Identifier(ChannelPayload.Text(root, "call_id"), "call_id", 128);
        var from = ChannelPayload.Phone(ChannelPayload.Required(root, "from", 16));
        var to = ChannelPayload.Required(root, "to", 128);
        var text = ChannelPayload.Bounded(ChannelPayload.Text(root, "text"), "summary", 4000);
        if (text.Length == 0) text = kind == "call_failed" ? "Call gateway failed; human call-taker follow-up required." : "REPORT";
        return [new("voice", Provider, from, to, text, ChannelPayload.Language(ChannelPayload.Text(root, "language"), text), now,
            kind + ":" + conversation, ProviderConversationId: conversation, Area: ChannelPayload.Bounded(ChannelPayload.Text(root, "area"), "area", 200),
            TranscriptSummary: kind == "speech_summary" ? text : null,
            HumanHandoffRequested: kind is "call_started" or "call_failed" || ChannelPayload.Boolean(root, "human_handoff_requested") == true,
            CallbackRequested: ChannelPayload.Boolean(root, "callback_requested") == true,
            PrivacyConsentAccepted: ChannelPayload.Boolean(root, "privacy_consent_accepted"),
            PrivacyConsentVersion: ChannelPayload.Bounded(ChannelPayload.Text(root, "privacy_consent_version"), "privacy_consent_version", 80))];
    }
}
