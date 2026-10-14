using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CivicOps.Tests;

public sealed class ChannelAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
    private const string Ingress = "synthetic-test-ingress-key";
    private const string Native = "synthetic-test-provider-key";
    private const string Caller = "+270000000001";
    private const string Municipal = "+270000000002";
    private const string Account = "AC00000000000000000000000000000000";
    private static readonly ChannelAdapterSettings Settings = new(Ingress, Native, "synthetic-test-api-key",
        Account, Municipal, "100001", "synthetic-test-verify-token", "synthetic-agent");
    private const string Description = "REPORT Water is leaking from a municipal pipe in the road.";

    private static ChannelWebhookRequest Envelope(ChannelWebhookRequest request, DateTimeOffset? at = null, string nonce = "synthetic-nonce-0001")
    {
        var timestamp = at ?? Now;
        var headers = request.Headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        headers["X-CivicOps-Timestamp"] = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        headers["X-CivicOps-Nonce"] = nonce;
        headers["X-CivicOps-Signature"] = ChannelSignature.SignIngress(request, Ingress, timestamp, nonce);
        return request with { Headers = headers };
    }
    private static ChannelWebhookRequest Json(string provider, string body)
    {
        var headers = new Dictionary<string, string>();
        if (provider == "whatsapp") headers["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Native), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        if (provider == "clickatell") headers["X-CivicOps-Clickatell-Token"] = Native;
        if (provider == "sip") headers["X-CivicOps-Sip-Token"] = Native;
        if (provider == "elevenlabs") headers["ElevenLabs-Signature"] = ElevenLabsSignatureTests.Sign(body, Native, Now);
        return Envelope(new(provider, body, "application/json", Url(provider), headers));
    }
    private static string Url(string provider) => "https://municipal.example/m/demo/api/v1/channels/webhooks/" + provider;
    private static ChannelWebhookRequest Twilio(string provider, Dictionary<string, string>? extra = null)
    {
        var form = new Dictionary<string, string>
        {
            ["AccountSid"] = Account, ["From"] = Caller, ["To"] = Municipal,
            [provider == "twilio-sms" ? "MessageSid" : "CallSid"] = provider == "twilio-sms" ? "SMsynthetic0001" : "CAsynthetic0001"
        };
        if (provider == "twilio-sms") form["Body"] = Description;
        if (extra is not null) foreach (var pair in extra) form[pair.Key] = pair.Value;
        var body = string.Join('&', form.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var input = Url(provider) + string.Concat(form.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + pair.Value));
        // Synthetic fixtures exercise Twilio's mandated native signature, alongside SHA256 ingress.
#pragma warning disable CA5350
        var signature = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(Native), Encoding.UTF8.GetBytes(input)));
#pragma warning restore CA5350
        return Envelope(new(provider, body, "application/x-www-form-urlencoded; charset=utf-8", Url(provider),
            new Dictionary<string, string> { ["X-Twilio-Signature"] = signature }));
    }
    private static string WhatsApp(params object[] messages)
    {
        var normalized = messages.Select(message =>
        {
            var value = JsonSerializer.SerializeToNode(message)!.AsObject();
            value["timestamp"] ??= Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            return value;
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[] { new { changes = new[] { new { field = "messages", value = new
            {
                messaging_product = "whatsapp", metadata = new { phone_number_id = "100001", display_phone_number = Municipal },
                messages = normalized
            } } } } }
        });
    }
    private static object WhatsAppText(string id = "wamid.synthetic0001", string text = Description) =>
        new { from = Caller.TrimStart('+'), id, timestamp = Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), type = "text", text = new { body = text } };
    private static string ElevenLabs(string type = "post_call_transcription") => JsonSerializer.Serialize(new
    {
        type, data = new
        {
            conversation_id = "synthetic-conversation-0001", metadata = new { phone_call = new { from_number = Caller, to_number = Municipal } },
            analysis = new { transcript_summary = "A synthetic resident reported a leaking pipe.", data_collection_results = new
            {
                description = new { value = Description }, area = new { value = "Synthetic ward" }, language = new { value = "af" },
                privacy_consent_accepted = new { value = true }, privacy_consent_version = new { value = "synthetic-notice" },
                human_handoff_requested = new { value = true }, callback_requested = new { value = true }
            } },
            transcript = new[] { new { role = "user", message = "This complete raw transcript must never become an adapter property." } }
        }
    });
    private static string Clickatell(string text = Description) => JsonSerializer.Serialize(new
    { replyMessageId = "synthetic-reply-0001", fromNumber = Caller, toNumber = Municipal, timestamp = Now.ToUnixTimeMilliseconds(), text });
    private static string Sip(string kind = "speech_summary") => JsonSerializer.Serialize(new
    {
        @event = kind, call_id = "synthetic-call-0001", from = Caller, to = Municipal, text = Description, language = "zu",
        area = "Synthetic ward", human_handoff_requested = true, callback_requested = true,
        privacy_consent_accepted = true, privacy_consent_version = "synthetic-notice"
    });
    private static IInboundChannel Adapter(string provider) => provider switch
    {
        "twilio-sms" => new TwilioSmsChannel(Settings), "twilio-voice" => new TwilioVoiceChannel(Settings),
        "clickatell" => new ClickatellSmsChannel(Settings), "whatsapp" => new WhatsAppCloudChannel(Settings),
        "elevenlabs" => new ElevenLabsInboundChannel(Settings), "sip" => new SipInboundChannel(Settings),
        _ => throw new InvalidOperationException()
    };
    private static ChannelWebhookRequest Request(string provider) => provider switch
    {
        "twilio-sms" or "twilio-voice" => Twilio(provider),
        "clickatell" => Json(provider, Clickatell()), "whatsapp" => Json(provider, WhatsApp(WhatsAppText())),
        "elevenlabs" => Json(provider, ElevenLabs()), "sip" => Json(provider, Sip()),
        _ => throw new InvalidOperationException()
    };

    [Theory]
    [InlineData(-301, false)] [InlineData(-300, true)] [InlineData(0, true)] [InlineData(30, true)] [InlineData(31, false)]
    public void SignedIngressHasBoundedFreshness(int offset, bool accepted) =>
        Assert.Equal(accepted, ChannelSignature.VerifyIngress(Envelope(Request("clickatell"), Now.AddSeconds(offset)), Ingress, Now));

    [Theory]
    [InlineData("")] [InlineData("short")] [InlineData("contains spaces in nonce")] [InlineData("contains\nnewline")]
    public void SignedIngressRejectsInvalidNonce(string nonce) =>
        Assert.False(ChannelSignature.VerifyIngress(Envelope(Request("clickatell"), nonce: nonce), Ingress, Now));

    [Fact]
    public void SignedIngressRejectsOversizedNonce() =>
        Assert.False(ChannelSignature.VerifyIngress(Envelope(Request("clickatell"), nonce: new string('n', 129)), Ingress, Now));

    [Theory]
    [InlineData("body")] [InlineData("provider")] [InlineData("tenant")] [InlineData("timestamp")] [InlineData("nonce")]
    public void SignedIngressBindsExactBodyProviderTenantUrlTimestampAndNonce(string field)
    {
        var request = Request("clickatell");
        request = field switch
        {
            "body" => request with { Body = request.Body + "\n" }, "provider" => request with { Provider = "sip" },
            "tenant" => request with { PublicUrl = request.PublicUrl.Replace("/demo/", "/other/", StringComparison.Ordinal) },
            "timestamp" => request with { Headers = request.Headers.ToDictionary(p => p.Key, p => p.Key == "X-CivicOps-Timestamp" ? "1" : p.Value) },
            _ => request with { Headers = request.Headers.ToDictionary(p => p.Key, p => p.Key == "X-CivicOps-Nonce" ? "other-synthetic-nonce" : p.Value) }
        };
        Assert.False(ChannelSignature.VerifyIngress(request, Ingress, Now));
    }

    [Theory]
    [InlineData("sha256=bad")] [InlineData("sha1=bad")] [InlineData("")] [InlineData("sha256=GG00000000000000000000000000000000000000000000000000000000000000")]
    public void MalformedIngressSignaturesFailClosed(string signature)
    {
        var request = Request("sip");
        Assert.False(ChannelSignature.VerifyIngress(request with
        { Headers = request.Headers.ToDictionary(p => p.Key, p => p.Key == "X-CivicOps-Signature" ? signature : p.Value) }, Ingress, Now));
    }

    [Fact]
    public void RepeatingHeaderNamesWithDifferentCaseFailsClosed()
    {
        var request = Request("sip");
        var headers = request.Headers.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        headers["x-civicops-signature"] = headers["X-CivicOps-Signature"];
        Assert.False(ChannelSignature.VerifyIngress(request with { Headers = headers }, Ingress, Now));
    }

    [Fact]
    public void TwilioSignatureMatchesOfficialPublishedExample()
    {
        var request = new ChannelWebhookRequest("twilio-voice",
            "CallSid=CA1234567890ABCDE&Caller=%2B14158675310&Digits=1234&From=%2B14158675310&To=%2B18005551212",
            "application/x-www-form-urlencoded", "https://example.com/myapp.php?foo=1&bar=2",
            new Dictionary<string, string> { ["X-Twilio-Signature"] = "L/OH5YylLD5NRKLltdqwSvS0BnU=" });
        Assert.True(ChannelSignature.VerifyTwilio(request, "12345"));
        Assert.False(ChannelSignature.VerifyTwilio(request with { PublicUrl = request.PublicUrl + "&extra=1" }, "12345"));
    }

    [Theory]
    [InlineData("twilio-sms", "sms")] [InlineData("twilio-voice", "voice")] [InlineData("clickatell", "sms")]
    [InlineData("whatsapp", "whatsapp")] [InlineData("elevenlabs", "voice")] [InlineData("sip", "voice")]
    public void EveryNativeAdapterParsesValidSignedResidentInput(string provider, string channel)
    {
        var message = Assert.Single(Adapter(provider).VerifyAndParse(Request(provider), Now));
        Assert.Equal(provider, message.Provider); Assert.Equal(channel, message.Channel); Assert.Equal(Caller, message.From);
        Assert.Equal(Now, message.ReceivedAt); Assert.NotEmpty(message.ProviderMessageId); Assert.NotEmpty(message.Text);
        Assert.DoesNotContain("complete raw transcript", JsonSerializer.Serialize(message), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("twilio-sms")] [InlineData("twilio-voice")] [InlineData("clickatell")] [InlineData("whatsapp")]
    [InlineData("elevenlabs")] [InlineData("sip")]
    public void EveryNativeAdapterRejectsChangedBodyEvenWithOldNativeHeaders(string provider)
    {
        var request = Request(provider);
        Assert.Equal(401, Assert.Throws<ChannelRequestException>(() => Adapter(provider).VerifyAndParse(request with { Body = request.Body + "\n" }, Now)).StatusCode);
    }

    [Theory]
    [InlineData("twilio-sms")] [InlineData("twilio-voice")] [InlineData("clickatell")] [InlineData("whatsapp")]
    [InlineData("elevenlabs")] [InlineData("sip")]
    public void EveryNativeAdapterRequiresSignedIngressEvenWithAuthenticNativeHeaders(string provider)
    {
        var request = Request(provider);
        var headers = request.Headers.Where(p => !p.Key.StartsWith("X-CivicOps-", StringComparison.Ordinal)
            || p.Key is "X-CivicOps-Clickatell-Token" or "X-CivicOps-Sip-Token")
            .ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(401, Assert.Throws<ChannelRequestException>(() => Adapter(provider).VerifyAndParse(request with { Headers = headers }, Now)).StatusCode);
    }

    [Theory]
    [InlineData("twilio-sms", "X-Twilio-Signature")] [InlineData("twilio-voice", "X-Twilio-Signature")]
    [InlineData("clickatell", "X-CivicOps-Clickatell-Token")] [InlineData("whatsapp", "X-Hub-Signature-256")]
    [InlineData("elevenlabs", "ElevenLabs-Signature")] [InlineData("sip", "X-CivicOps-Sip-Token")]
    public void AuthenticIngressCannotReplaceNativeAuthentication(string provider, string header)
    {
        var request = Request(provider);
        request = Envelope(request with { Headers = request.Headers.ToDictionary(p => p.Key, p => p.Key == header ? "invalid-native-signature" : p.Value) });
        Assert.Equal(401, Assert.Throws<ChannelRequestException>(() => Adapter(provider).VerifyAndParse(request, Now)).StatusCode);
    }

    [Theory]
    [InlineData("twilio-sms")] [InlineData("twilio-voice")] [InlineData("clickatell")] [InlineData("whatsapp")]
    public void NativeAndIngressSignaturesCannotReplaceConfiguredReceiverBinding(string provider)
    {
        var request = provider switch
        {
            "twilio-sms" or "twilio-voice" => Twilio(provider, new() { ["To"] = "+270000000999" }),
            "clickatell" => Json(provider, JsonSerializer.Serialize(new { replyMessageId = "synthetic-other-number", fromNumber = Caller,
                toNumber = "+270000000999", timestamp = Now.ToUnixTimeMilliseconds(), text = Description })),
            _ => Json(provider, WhatsApp(WhatsAppText()).Replace("100001", "100999", StringComparison.Ordinal))
        };
        Assert.Equal(401, Assert.Throws<ChannelRequestException>(() => Adapter(provider).VerifyAndParse(request, Now)).StatusCode);
    }

    [Theory]
    [InlineData("twilio-sms")] [InlineData("twilio-voice")] [InlineData("clickatell")] [InlineData("whatsapp")]
    public void InboundReceiverConfigurationMustBeComplete(string provider)
    {
        IInboundChannel adapter = provider switch
        {
            "twilio-sms" => new TwilioSmsChannel(Settings with { AccountId = "" }),
            "twilio-voice" => new TwilioVoiceChannel(Settings with { Sender = "" }),
            "clickatell" => new ClickatellSmsChannel(Settings with { Sender = "" }),
            _ => new WhatsAppCloudChannel(Settings with { PhoneNumberId = "" })
        };
        Assert.Equal(503, Assert.Throws<ChannelRequestException>(() => adapter.VerifyAndParse(Request(provider), Now)).StatusCode);
    }

    [Fact]
    public void SipGatewayRequiresIndependentCallerAuthenticationAndConfiguredSecret()
    {
        var request = Request("sip");
        var missing = request with { Headers = request.Headers.Where(pair => pair.Key != "X-CivicOps-Sip-Token")
            .ToDictionary(pair => pair.Key, pair => pair.Value) };
        Assert.Equal(401, Assert.Throws<ChannelRequestException>(() => Adapter("sip").VerifyAndParse(missing, Now)).StatusCode);
        Assert.Equal(503, Assert.Throws<ChannelRequestException>(() => new SipInboundChannel(Settings with { NativeSecret = "" })
            .VerifyAndParse(request, Now)).StatusCode);
    }

    [Theory]
    [InlineData("twilio-sms")] [InlineData("twilio-voice")] [InlineData("clickatell")] [InlineData("whatsapp")]
    [InlineData("elevenlabs")] [InlineData("sip")]
    public void EveryNativeAdapterRejectsOversizedNormalizedText(string provider)
    {
        var text = new string('x', 4001);
        var request = provider switch
        {
            "twilio-sms" => Twilio(provider, new() { ["Body"] = text }),
            "twilio-voice" => Twilio(provider, new() { ["SpeechResult"] = text }),
            "clickatell" => Json(provider, Clickatell(text)),
            "whatsapp" => Json(provider, WhatsApp(WhatsAppText(text: text))),
            "elevenlabs" => Json(provider, ElevenLabs().Replace(Description, text, StringComparison.Ordinal)),
            _ => Json(provider, Sip().Replace(Description, text, StringComparison.Ordinal))
        };
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => Adapter(provider).VerifyAndParse(request, Now)).StatusCode);
    }

    [Theory]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":[1]}")]
    [InlineData("{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[1]}]}")]
    public void WhatsAppMalformedNestedObjectsReturnBoundedParseErrors(string body) =>
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => Adapter("whatsapp").VerifyAndParse(Json("whatsapp", body), Now)).StatusCode);

    [Fact]
    public void WhatsAppCoordinatesMustBeNumbersRatherThanCoercedText()
    {
        var body = WhatsApp(new { from = Caller.TrimStart('+'), id = "wamid.synthetic-invalid-location", type = "location",
            location = new { longitude = "28.0473", latitude = "-26.2041" } });
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => Adapter("whatsapp").VerifyAndParse(Json("whatsapp", body), Now)).StatusCode);
    }

    [Fact]
    public void DelayedNativeWhatsAppDeliveryCannotReopenAClosedResidentWindow()
    {
        var residentAt = Now.AddHours(-48);
        var body = WhatsApp(new { from = Caller.TrimStart('+'), id = "wamid.synthetic-delayed", type = "text",
            timestamp = residentAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), text = new { body = Description } });
        var inbound = Assert.Single(Adapter("whatsapp").VerifyAndParse(Json("whatsapp", body), Now));
        Assert.Equal(residentAt, inbound.ReceivedAt);
        Assert.Equal(409, Assert.Throws<ChannelRequestException>(() => new WhatsAppCloudChannel(Settings).BuildRequest(
            Outbound("whatsapp") with { LastResidentMessageAt = inbound.ReceivedAt }, Now)).StatusCode);
    }

    [Fact]
    public void TwilioAuthenticatesAllParametersIncludingFutureVendorFields()
    {
        var request = Twilio("twilio-sms", new() { ["FutureProviderField"] = "must be signed", ["Body"] = "  REPORT leaking municipal pipe  " });
        var message = Assert.Single(Adapter("twilio-sms").VerifyAndParse(request, Now));
        Assert.Equal("  REPORT leaking municipal pipe  ", message.Text);
        var changed = Envelope(request with { Body = request.Body.Replace("must%20be%20signed", "modified", StringComparison.Ordinal) });
        Assert.Equal(401, Assert.Throws<ChannelRequestException>(() => Adapter("twilio-sms").VerifyAndParse(changed, Now)).StatusCode);
    }

    [Theory]
    [InlineData("&From=%2B270000000001")] [InlineData("&Bad=%GG")] [InlineData("&Bad=%FF")]
    public void TwilioRejectsAmbiguousOrInvalidFormEncoding(string suffix)
    {
        var request = Twilio("twilio-sms");
        Assert.False(ChannelSignature.VerifyTwilio(request with { Body = request.Body + suffix }, Native));
    }

    [Fact]
    public void TwilioOpaqueMediaReferencesContainNoProviderFetchUrl()
    {
        var request = Twilio("twilio-sms", new() { ["NumMedia"] = "2", ["MediaContentType0"] = "image/jpeg",
            ["MediaUrl0"] = "https://media.example/private", ["MediaContentType1"] = "video/mp4", ["MediaUrl1"] = "https://media.example/video" });
        var message = Assert.Single(Adapter("twilio-sms").VerifyAndParse(request, Now));
        Assert.Equal(["twilio.SMsynthetic0001.0"], message.MediaReferences);
        Assert.DoesNotContain("https://media.example", JsonSerializer.Serialize(message), StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedTwilioIdentifiersDoNotOverflowTheStoredMediaReferenceBound()
    {
        var request = Twilio("twilio-sms", new() { ["MessageSid"] = new string('x', 128), ["NumMedia"] = "1",
            ["MediaContentType0"] = "image/jpeg" });
        var message = Assert.Single(Adapter("twilio-sms").VerifyAndParse(request, Now));
        Assert.True(Assert.Single(message.MediaReferences!).Length <= 200);
        ChannelStore.Validate(message);
    }

    [Fact]
    public void WhatsAppOpaqueMediaReferencesMeetTheDurableStoreGuard()
    {
        var body = WhatsApp(new { from = Caller.TrimStart('+'), id = "wamid.synthetic-media", type = "image",
            image = new { id = "synthetic/opaque=media", caption = Description } });
        var message = Assert.Single(Adapter("whatsapp").VerifyAndParse(Json("whatsapp", body), Now));
        var reference = Assert.Single(message.MediaReferences!);
        Assert.StartsWith("whatsapp.", reference, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic/opaque=media", reference, StringComparison.Ordinal);
        ChannelStore.Validate(message);
    }

    [Theory]
    [InlineData("1", "REPORT", false, false)] [InlineData("2", "STATUS", false, false)] [InlineData("3", "HUMAN", true, false)]
    [InlineData("4", "CALLBACK", false, true)] [InlineData("9", "SOS", false, false)]
    public void VoiceIvrSelectionsBecomeHumanReviewedCommands(string digits, string text, bool handoff, bool callback)
    {
        var message = Assert.Single(Adapter("twilio-voice").VerifyAndParse(Twilio("twilio-voice", new() { ["Digits"] = digits }), Now));
        Assert.Equal(text, message.Text); Assert.Equal(handoff, message.HumanHandoffRequested); Assert.Equal(callback, message.CallbackRequested);
    }

    [Fact]
    public void VoiceSpeechProducesSummaryAndStablePerTurnProviderIdentity()
    {
        var request = Twilio("twilio-voice", new() { ["SpeechResult"] = Description });
        var first = Assert.Single(Adapter("twilio-voice").VerifyAndParse(request, Now));
        var retry = Assert.Single(Adapter("twilio-voice").VerifyAndParse(request, Now.AddSeconds(1)));
        var next = Assert.Single(Adapter("twilio-voice").VerifyAndParse(Twilio("twilio-voice", new() { ["SpeechResult"] = Description + " Please help." }), Now));
        Assert.Equal(Description, first.TranscriptSummary); Assert.Equal(first.ProviderMessageId, retry.ProviderMessageId);
        Assert.NotEqual(first.ProviderMessageId, next.ProviderMessageId); Assert.Equal(first.ProviderConversationId, next.ProviderConversationId);
    }

    [Fact]
    public void InitialCallExplicitlyQueuesAHumanHandoff()
    {
        var message = Assert.Single(Adapter("twilio-voice").VerifyAndParse(Twilio("twilio-voice"), Now));
        Assert.True(message.HumanHandoffRequested); Assert.Equal("REPORT", message.Text);
    }

    [Theory]
    [InlineData("anonymous")] [InlineData("restricted")] [InlineData("unavailable")]
    public void WithheldVoiceCallerUsesUnaddressableCallIdentityAndHumanQueue(string caller)
    {
        var message = Assert.Single(Adapter("twilio-voice").VerifyAndParse(
            Twilio("twilio-voice", new() { ["From"] = caller, ["Digits"] = "4" }), Now));
        Assert.Equal("call:CAsynthetic0001", message.From); Assert.True(message.HumanHandoffRequested);
        Assert.False(message.CallbackRequested);
    }

    [Theory]
    [InlineData("whatsapp")] [InlineData("clickatell")] [InlineData("sip")] [InlineData("elevenlabs")]
    public void NativeJsonRejectsDuplicateKeys(string provider)
    {
        var body = provider switch
        {
            "whatsapp" => WhatsApp(WhatsAppText()).Replace("\"type\":\"text\"", "\"type\":\"text\",\"type\":\"location\"", StringComparison.Ordinal),
            "clickatell" => Clickatell().Replace("\"text\":", "\"text\":\"wrong\",\"text\":", StringComparison.Ordinal),
            "sip" => Sip().Replace("\"event\":", "\"event\":\"call_started\",\"event\":", StringComparison.Ordinal),
            _ => ElevenLabs().Replace("\"type\":", "\"type\":\"post_call_audio\",\"type\":", StringComparison.Ordinal)
        };
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => Adapter(provider).VerifyAndParse(Json(provider, body), Now)).StatusCode);
    }

    [Theory]
    [InlineData("report_problem", "REPORT")] [InlineData("check_case", "STATUS")] [InlineData("sos_info", "SOS")]
    public void WhatsAppButtonIdsRouteTheThreeReviewedCommands(string id, string expected)
    {
        var body = WhatsApp(new { from = Caller.TrimStart('+'), id = "wamid.synthetic-buttons", type = "interactive",
            interactive = new { type = "button_reply", button_reply = new { id, title = "Synthetic label" } } });
        Assert.Equal(expected, Assert.Single(Adapter("whatsapp").VerifyAndParse(Json("whatsapp", body), Now)).Text);
    }

    [Fact]
    public void WhatsAppLocationIsVoluntaryAndWithinSouthAfricanBounds()
    {
        var body = WhatsApp(new { from = Caller.TrimStart('+'), id = "wamid.synthetic-location", type = "location",
            location = new { longitude = 28.0473, latitude = -26.2041, name = "Synthetic location" } });
        var message = Assert.Single(Adapter("whatsapp").VerifyAndParse(Json("whatsapp", body), Now));
        Assert.Equal(new GeoPoint(28.0473, -26.2041), message.Location); Assert.True(message.LocationShared);
        Assert.Null(message.PrivacyConsentAccepted);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(200, -26)] [InlineData(28, 40)]
    public void WhatsAppRejectsLocationsOutsideServiceBounds(double longitude, double latitude)
    {
        var body = WhatsApp(new { from = Caller.TrimStart('+'), id = "wamid.synthetic-location", type = "location", location = new { longitude, latitude } });
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => Adapter("whatsapp").VerifyAndParse(Json("whatsapp", body), Now)).StatusCode);
    }

    [Theory]
    [InlineData("audio")] [InlineData("video")]
    public void WhatsAppAudioAndVideoQueueAHumanWithoutFetchingOrRetainingMedia(string type)
    {
        var body = WhatsApp(new { from = Caller.TrimStart('+'), id = "wamid.synthetic-unsupported-media", type });
        var message = Assert.Single(Adapter("whatsapp").VerifyAndParse(Json("whatsapp", body), Now));
        Assert.True(message.HumanHandoffRequested); Assert.Empty(message.MediaReferences!);
        Assert.Contains("did not fetch or retain", message.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("face-match")] [InlineData("person-risk")] [InlineData("unknown")]
    public void WhatsAppCannotIntroduceUnreviewedMessageTypes(string type)
    {
        var body = WhatsApp(new { from = Caller.TrimStart('+'), id = "wamid.synthetic-unknown", type });
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => Adapter("whatsapp").VerifyAndParse(Json("whatsapp", body), Now)).StatusCode);
    }

    [Fact]
    public void WhatsAppStatusCallbacksDoNotCreateResidentsOrCases()
    {
        var body = """{"object":"whatsapp_business_account","entry":[{"changes":[{"field":"messages","value":{"metadata":{"phone_number_id":"100001"},"statuses":[{"id":"synthetic-status","status":"delivered"}]}}]}]}""";
        Assert.Empty(Adapter("whatsapp").VerifyAndParse(Json("whatsapp", body), Now));
    }

    [Theory]
    [InlineData("subscribe", "synthetic-test-verify-token", "synthetic-challenge", true)]
    [InlineData("unsubscribe", "synthetic-test-verify-token", "synthetic-challenge", false)]
    [InlineData("subscribe", "wrong", "synthetic-challenge", false)] [InlineData("subscribe", "", "synthetic-challenge", false)]
    [InlineData("subscribe", "synthetic-test-verify-token", "", false)]
    public void WhatsAppChallengeRequiresConfiguredTokenAndBoundedChallenge(string mode, string token, string challenge, bool valid) =>
        Assert.Equal(valid, new WhatsAppCloudChannel(Settings).VerifyChallenge(mode, token, challenge));

    [Fact]
    public void ElevenLabsUsesOnlyStructuredSummaryAndRecordedConsent()
    {
        var message = Assert.Single(Adapter("elevenlabs").VerifyAndParse(Json("elevenlabs", ElevenLabs()), Now));
        Assert.Equal("af", message.LanguageGuess); Assert.True(message.PrivacyConsentAccepted);
        Assert.Equal("synthetic-notice", message.PrivacyConsentVersion); Assert.True(message.CallbackRequested);
        Assert.Equal("A synthetic resident reported a leaking pipe.", message.TranscriptSummary);
        Assert.DoesNotContain("complete raw transcript", JsonSerializer.Serialize(message), StringComparison.Ordinal);
    }

    [Fact]
    public void ElevenLabsAudioEventsAreAcknowledgedWithoutAudioProperties()
    {
        Assert.Empty(Adapter("elevenlabs").VerifyAndParse(Json("elevenlabs", ElevenLabs("post_call_audio")), Now));
        Assert.DoesNotContain(typeof(InboundMessage).GetProperties(), property => property.Name.Contains("Audio", StringComparison.OrdinalIgnoreCase)
            || property.Name.Contains("RawTranscript", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ElevenLabsFailureRequiresHumanCallTaker()
    {
        var message = Assert.Single(Adapter("elevenlabs").VerifyAndParse(Json("elevenlabs", ElevenLabs("call_initiation_failure")), Now));
        Assert.True(message.HumanHandoffRequested); Assert.Contains("human call-taker", message.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("face_embedding")] [InlineData("voice_print")] [InlineData("person_risk")] [InlineData("unexpected")]
    public void SipGatewayRejectsProhibitedAndUnknownFields(string field)
    {
        var body = Sip()[..^1] + ",\"" + field + "\":\"blocked\"}";
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => Adapter("sip").VerifyAndParse(Json("sip", body), Now)).StatusCode);
    }

    [Fact]
    public void SimulatedFactoriesNeverUseNativeCredentials()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CIVICOPS_CHANNEL_INGRESS_SECRET"] = Ingress, ["CIVICOPS_TWILIO_AUTH_TOKEN"] = Native }).Build();
        Assert.Equal(503, Assert.Throws<ChannelRequestException>(() => ChannelAdapters.CreateInbound("twilio-sms", config, true)
            .VerifyAndParse(Twilio("twilio-sms"), Now)).StatusCode);
        Assert.Equal(503, Assert.Throws<ChannelRequestException>(() => ChannelAdapters.CreateOutbound("twilio-sms", config, true)).StatusCode);
    }

    [Theory]
    [InlineData("twilio-sms")] [InlineData("clickatell")] [InlineData("whatsapp")] [InlineData("elevenlabs")]
    public void OutboundBuildersFailClosedWithoutConfiguration(string provider)
    {
        IOutboundChannel adapter = provider switch
        {
            "twilio-sms" => new TwilioSmsChannel(new()), "clickatell" => new ClickatellSmsChannel(new()),
            "whatsapp" => new WhatsAppCloudChannel(new()), _ => new ElevenLabsOutboundChannel(new())
        };
        var message = Outbound(provider);
        Assert.Equal(503, Assert.Throws<ChannelRequestException>(() => adapter.BuildRequest(message, Now)).StatusCode);
    }

    private static OutboundMessage Outbound(string provider) => new(
        provider == "whatsapp" ? "whatsapp" : provider == "elevenlabs" ? "voice" : "sms", provider, Caller, Municipal,
        "Synthetic case received for human review.", "en", LastResidentMessageAt: Now.AddHours(-1));

    [Fact]
    public async Task TwilioSmsBuilderUsesOfficialHostAndConfiguredSenderOnly()
    {
        using var request = new TwilioSmsChannel(Settings).BuildRequest(Outbound("twilio-sms") with { From = "+270000000999" }, Now);
        Assert.Equal("api.twilio.com", request.RequestUri!.Host); Assert.Equal("https", request.RequestUri.Scheme);
        var body = WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync());
        Assert.Contains("From=" + Municipal, body, StringComparison.Ordinal); Assert.DoesNotContain("+270000000999", body, StringComparison.Ordinal);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task ClickatellSmsBuilderUsesOfficialRestShape()
    {
        using var request = new ClickatellSmsChannel(Settings).BuildRequest(Outbound("clickatell"), Now);
        Assert.Equal("https://platform.clickatell.com/messages", request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal(Caller.TrimStart('+'), body.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal(Municipal.TrimStart('+'), body.RootElement.GetProperty("from").GetString());
        Assert.Equal("synthetic-test-api-key", Assert.Single(request.Headers.GetValues("Authorization")));
    }

    [Theory]
    [InlineData(23, true)] [InlineData(24, false)] [InlineData(25, false)] [InlineData(-1, false)]
    public void WhatsAppFreeTextWindowIsStrictlyBounded(int ageHours, bool allowed)
    {
        var adapter = new WhatsAppCloudChannel(Settings);
        var message = Outbound("whatsapp") with { LastResidentMessageAt = Now.AddHours(-ageHours) };
        if (allowed) { using var request = adapter.BuildRequest(message, Now); Assert.Equal("graph.facebook.com", request.RequestUri!.Host); }
        else Assert.Equal(409, Assert.Throws<ChannelRequestException>(() => adapter.BuildRequest(message, Now)).StatusCode);
    }

    [Fact]
    public async Task WhatsAppOutsideWindowUsesVersionedApprovedTemplateVariables()
    {
        var message = Outbound("whatsapp") with { LastResidentMessageAt = Now.AddHours(-25), ProviderTemplateName = "civicops_case_received_v1",
            TemplateKey = "case-received", TemplateVersion = 1, Variables = new Dictionary<string, string> { ["reference"] = "CIV-SYNTHETIC" } };
        using var request = new WhatsAppCloudChannel(Settings).BuildRequest(message, Now);
        Assert.Equal("https://graph.facebook.com/v25.0/100001/messages", request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal("template", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("civicops_case_received_v1", body.RootElement.GetProperty("template").GetProperty("name").GetString());
        Assert.Equal("CIV-SYNTHETIC", body.RootElement.GetProperty("template").GetProperty("components")[0].GetProperty("parameters")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("en")] [InlineData("af")] [InlineData("zu")]
    public async Task WhatsAppInteractiveMenuHasExactlyThreeReviewedButtons(string language)
    {
        using var request = new WhatsAppCloudChannel(Settings).BuildRequest(Outbound("whatsapp") with { Interactive = true, Language = language }, Now);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var buttons = body.RootElement.GetProperty("interactive").GetProperty("action").GetProperty("buttons");
        Assert.Equal(3, buttons.GetArrayLength());
        Assert.Equal(["report_problem", "check_case", "sos_info"], buttons.EnumerateArray().Select(b => b.GetProperty("reply").GetProperty("id").GetString()));
        Assert.All(buttons.EnumerateArray(), button => Assert.InRange(button.GetProperty("reply").GetProperty("title").GetString()!.Length, 1, 20));
    }

    [Fact]
    public void WhatsAppInteractiveRepliesCannotEscapeThe24HourWindow() =>
        Assert.Equal(400, Assert.Throws<ChannelRequestException>(() => new WhatsAppCloudChannel(Settings).BuildRequest(
            Outbound("whatsapp") with { LastResidentMessageAt = Now.AddHours(-24), Interactive = true, ProviderTemplateName = "civicops_case_v1" }, Now)).StatusCode);

    [Theory]
    [InlineData("twilio")] [InlineData("sip-trunk")]
    public async Task ElevenLabsCallbackBuildsTheOfficialPhoneProviderRequest(string phoneProvider)
    {
        using var request = new ElevenLabsOutboundChannel(Settings with { PhoneProvider = phoneProvider }).BuildRequest(
            Outbound("elevenlabs") with { Variables = new Dictionary<string, string> { ["case_reference"] = "CIV-SYNTHETIC" } }, Now);
        Assert.Equal("https://api.elevenlabs.io/v1/convai/" + phoneProvider + "/outbound-call", request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal(Caller, body.RootElement.GetProperty("to_number").GetString());
        Assert.Equal("CIV-SYNTHETIC", body.RootElement.GetProperty("conversation_initiation_client_data").GetProperty("dynamic_variables").GetProperty("case_reference").GetString());
    }

    [Fact]
    public void IvrFallbackContainsPurposeNoticeEmergencyNumbersAndSimulatedLabel()
    {
        var response = XElement.Parse(TwilioVoiceChannel.BuildVoiceResponse(Url("twilio-voice"), true));
        Assert.Contains("simulated IVR", response.Value, StringComparison.Ordinal);
        Assert.Contains(PrivacyConsent.Version, response.Value, StringComparison.Ordinal);
        Assert.Contains("Say YES", response.Value, StringComparison.Ordinal);
        foreach (var number in new[] { "10111", "10177", "112" }) Assert.Contains(number, response.Value, StringComparison.Ordinal);
        Assert.Equal("dtmf speech", response.Element("Gather")!.Attribute("input")!.Value);
        Assert.Null(response.Element("Dial"));
    }

    [Fact]
    public void ConfiguredAgentSipTransferUsesOnlyExplicitValidatedDestination()
    {
        var response = XElement.Parse(TwilioVoiceChannel.BuildVoiceResponse(Url("twilio-voice"), false, "sip:synthetic-agent@agent.example"));
        Assert.Equal("sip:synthetic-agent@agent.example", response.Element("Dial")!.Element("Sip")!.Value);
        Assert.Null(response.Element("Gather"));
        Assert.Throws<ChannelRequestException>(() => TwilioVoiceChannel.BuildVoiceResponse(Url("twilio-voice"), false, "https://untrusted.example"));
    }

    private const string NativeBase = "https://municipality.example.invalid";
    private static void ConfigureNative(TestApp app)
    {
        foreach (var setting in new Dictionary<string, string>
        {
            ["CIVICOPS_DEMO_MODE"] = "false", ["CIVICOPS_CHANNEL_MODE"] = "configured",
            ["CIVICOPS_CHANNEL_INGRESS_SECRET"] = Ingress,
            ["CIVICOPS_CHANNEL_ENCRYPTION_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()),
            ["CIVICOPS_CHANNEL_WEBHOOK_BASE_URL"] = NativeBase, ["CIVICOPS_CHANNEL_SEND_ENABLED"] = "true",
            ["CIVICOPS_TWILIO_AUTH_TOKEN"] = Native, ["CIVICOPS_TWILIO_ACCOUNT_SID"] = Account,
            ["CIVICOPS_TWILIO_FROM_NUMBER"] = Municipal,
            ["CIVICOPS_CLICKATELL_CALLBACK_SECRET"] = Native, ["CIVICOPS_CLICKATELL_API_KEY"] = "synthetic-test-api-key",
            ["CIVICOPS_CLICKATELL_FROM_NUMBER"] = Municipal,
            ["CIVICOPS_SIP_CALLBACK_SECRET"] = Native,
            ["CIVICOPS_WHATSAPP_APP_SECRET"] = Native, ["CIVICOPS_WHATSAPP_ACCESS_TOKEN"] = "synthetic-test-api-key",
            ["CIVICOPS_WHATSAPP_VERIFY_TOKEN"] = "synthetic-test-verify-token", ["CIVICOPS_WHATSAPP_PHONE_NUMBER_ID"] = "100001",
            ["ELEVENLABS_WEBHOOK_SECRET"] = Native, ["ELEVENLABS_API_KEY"] = "synthetic-test-api-key",
            ["ELEVENLABS_AGENT_PHONE_NUMBER_ID"] = "100001", ["ELEVENLABS_CALLBACK_AGENT_ID"] = "synthetic-agent",
            ["CIVICOPS_ELEVENLABS_INBOUND_SIP_URI"] = ""
        }) app.Settings[setting.Key] = setting.Value;
    }
    private static ChannelWebhookRequest LiveRequest(string provider, string text = Description, string turn = "report")
    {
        var request = Request(provider);
        if (provider is "twilio-sms" or "twilio-voice")
        {
            var form = request.Body.Split('&').Select(part => part.Split('=', 2)).ToDictionary(
                part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part[1]));
            if (provider == "twilio-sms") { form["MessageSid"] = "SMnative-" + turn; form["Body"] = text; }
            else { form["CallSid"] = "CAnative-voice-0001"; if (text.Length > 0) form["SpeechResult"] = text; else form.Remove("SpeechResult"); }
            request = request with { Body = string.Join('&', form.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))) };
        }
        else
        {
            var body = JsonNode.Parse(request.Body)!.AsObject();
            if (provider == "clickatell") { body["replyMessageId"] = "native-reply-" + turn; body["text"] = text; }
            if (provider == "whatsapp")
            {
                var message = body["entry"]![0]!["changes"]![0]!["value"]!["messages"]![0]!;
                message["id"] = "wamid.native-" + turn; message["text"]!["body"] = text;
                message["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            }
            if (provider == "elevenlabs")
            {
                body["data"]!["conversation_id"] = "native-conversation-" + turn;
                body["data"]!["analysis"]!["data_collection_results"]!["description"]!["value"] = text;
                body["data"]!["analysis"]!["data_collection_results"]!["privacy_consent_version"]!["value"] = PrivacyConsent.Version;
            }
            if (provider == "sip") { body["call_id"] = "native-call-" + turn; body["text"] = text; body["privacy_consent_version"] = PrivacyConsent.Version; }
            request = request with { Body = body.ToJsonString() };
        }
        return LiveSignature(request with { PublicUrl = NativeBase + "/api/v1/channels/webhooks/" + provider });
    }
    private static ChannelWebhookRequest LiveSignature(ChannelWebhookRequest request, DateTimeOffset? ingressAt = null, string? nonce = null)
    {
        var headers = new Dictionary<string, string>();
        if (request.Provider is "twilio-sms" or "twilio-voice")
        {
            var values = request.Body.Split('&').Select(part => part.Split('=', 2)).ToDictionary(
                part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part[1]));
            var input = request.PublicUrl + string.Concat(values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + pair.Value));
#pragma warning disable CA5350 // Native Twilio fixture protocol; ingress remains SHA256.
            headers["X-Twilio-Signature"] = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(Native), Encoding.UTF8.GetBytes(input)));
#pragma warning restore CA5350
        }
        if (request.Provider == "clickatell") headers["X-CivicOps-Clickatell-Token"] = Native;
        if (request.Provider == "sip") headers["X-CivicOps-Sip-Token"] = Native;
        if (request.Provider == "whatsapp") headers["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Native), Encoding.UTF8.GetBytes(request.Body))).ToLowerInvariant();
        if (request.Provider == "elevenlabs") headers["ElevenLabs-Signature"] = ElevenLabsSignatureTests.Sign(request.Body, Native, DateTimeOffset.UtcNow);
        return Envelope(request with { Headers = headers }, ingressAt ?? DateTimeOffset.UtcNow, nonce ?? Guid.NewGuid().ToString("N"));
    }
    private static async Task<HttpResponseMessage> PostNative(HttpClient client, ChannelWebhookRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/channels/webhooks/" + request.Provider)
        { Content = new StringContent(request.Body, Encoding.UTF8) };
        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        foreach (var header in request.Headers) message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return await client.SendAsync(message);
    }
    private static async Task<ChannelConversationView> OnlyConversation(HttpClient staff)
    {
        using var list = await staff.GetAsync("/api/v1/channels/conversations");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var summary = Assert.Single((await list.Content.ReadFromJsonAsync<ChannelConversationView[]>())!);
        using var detail = await staff.GetAsync("/api/v1/channels/conversations/" + summary.Id);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        return (await detail.Content.ReadFromJsonAsync<ChannelConversationView>())!;
    }
    private static async Task SendNativeNotice(HttpClient staff, ChannelConversationView conversation)
    {
        using var templatesResponse = await staff.GetAsync("/api/v1/channels/templates");
        Assert.Equal(HttpStatusCode.OK, templatesResponse.StatusCode);
        var template = Assert.Single((await templatesResponse.Content.ReadFromJsonAsync<ChannelTemplate[]>())!,
            item => item.Key == "channel.notice" && item.Language == conversation.Language);
        using var sent = await staff.PostAsJsonAsync("/api/v1/channels/conversations/" + conversation.Id + "/reply",
            new ChannelReplyRequest(template.Key, template.Language, [], template.Version, conversation.Version,
                "Synthetic named call-taker provided the current notice.", Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("accepted-unverified", (await sent.Content.ReadFromJsonAsync<ChannelReplyResult>())!.Mode);
    }
    private sealed class CapturedTransport : HttpMessageHandler
    {
        private static readonly HashSet<string> AllowedHosts = ["api.twilio.com", "platform.clickatell.com", "graph.facebook.com", "api.elevenlabs.io"];
        public ConcurrentQueue<(string Host, string Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Contains(request.RequestUri.Host, AllowedHosts);
            Assert.Equal(HttpMethod.Post, request.Method);
            Calls.Enqueue((request.RequestUri.Host, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            // A fake handler acknowledges the synthetic request. No socket, provider, resident or
            // account is contacted; accepted does not assert delivery to a handset.
            return new(HttpStatusCode.Accepted) { Content = new StringContent("{\"accepted\":true}", Encoding.UTF8, "application/json") };
        }
    }
    private sealed class CapturedFactory(CapturedTransport transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(transport, disposeHandler: false);
    }

    [Theory]
    [InlineData("twilio-sms")] [InlineData("twilio-voice")] [InlineData("clickatell")]
    [InlineData("whatsapp")] [InlineData("elevenlabs")] [InlineData("sip")]
    public async Task NativeEndpointsVerifyReplayAndRouteThroughTheConsentAndHumanCasePipeline(string provider)
    {
        using var app = new TestApp(); ConfigureNative(app);
        using var transport = new CapturedTransport();
        using var factory = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(new CapturedFactory(transport)); }));
        using var resident = factory.CreateClient(); using var staff = factory.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-dispatch-token");
        var first = LiveRequest(provider);
        var wrongNativeHeader = provider switch
        {
            "twilio-sms" or "twilio-voice" => "X-Twilio-Signature", "clickatell" => "X-CivicOps-Clickatell-Token",
            "whatsapp" => "X-Hub-Signature-256", "elevenlabs" => "ElevenLabs-Signature", _ => "X-CivicOps-Sip-Token"
        };
        var wrong = first with { Headers = first.Headers.ToDictionary(pair => pair.Key,
            pair => pair.Key == wrongNativeHeader ? "synthetic-invalid-signature" : pair.Value) };
        using (var denied = await PostNative(resident, wrong)) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var stale = await PostNative(resident, LiveSignature(first, DateTimeOffset.UtcNow.AddSeconds(-301))))
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        using (var accepted = await PostNative(resident, first))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            if (provider == "twilio-voice")
            {
                Assert.Equal("application/xml", accepted.Content.Headers.ContentType!.MediaType);
                var ivr = XElement.Parse(await accepted.Content.ReadAsStringAsync());
                Assert.Contains(PrivacyConsent.Version, ivr.Value, StringComparison.Ordinal);
                Assert.Contains("Say YES", ivr.Value, StringComparison.Ordinal);
                Assert.NotNull(ivr.Element("Gather")); Assert.Null(ivr.Element("Dial"));
            }
        }
        var conversation = await OnlyConversation(staff);
        if (provider is "twilio-sms" or "clickatell" or "whatsapp")
        {
            Assert.False(conversation.ConsentAccepted);
            Assert.Empty(await factory.Services.GetRequiredService<IncidentStore>().ListAsync(default));
            await SendNativeNotice(staff, conversation);
            Assert.Single(transport.Calls);
        }
        if (provider is "twilio-sms" or "twilio-voice" or "clickatell" or "whatsapp")
        {
            using var consent = await PostNative(resident, LiveRequest(provider, "YES", "consent"));
            Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        }
        var incident = Assert.Single(await factory.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        Assert.Equal(IncidentStatus.PendingApproval, incident.Status); Assert.Null(incident.Approval); Assert.Null(incident.AssignedCrewId);
        Assert.True(incident.NeedsHumanReview); Assert.Equal(5, incident.AgentRuns.Count);
        Assert.Contains(incident.Audit, entry => entry.Action.StartsWith("channels.intake.", StringComparison.Ordinal));
        conversation = await OnlyConversation(staff);
        Assert.Contains(incident.ReferenceNumber, conversation.CaseReferences); Assert.True(conversation.ConsentAccepted);
        Assert.DoesNotContain(Caller, JsonSerializer.Serialize(conversation), StringComparison.Ordinal);
        if (provider is "twilio-sms" or "clickatell" or "whatsapp")
            Assert.Contains(conversation.Messages, message => message.Direction == "outbound" && message.TemplateKey == "channel.notice"
                && !string.IsNullOrWhiteSpace(message.Actor) && message.DeliveryState == "accepted-unverified");
        using (var replay = await PostNative(resident, first)) Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        using (var retry = await PostNative(resident, LiveSignature(first)))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            if (provider != "twilio-voice")
                Assert.True(Assert.Single((await retry.Content.ReadFromJsonAsync<ChannelWebhookResult>())!.Results).Replayed);
        }
        Assert.Single(await factory.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        Assert.True((await factory.Services.GetRequiredService<IncidentStore>().VerifyAuditAsync(default)).Valid);
    }

    [Fact]
    public async Task NativeWhatsAppChallengeAuthenticatesWithoutAnyProviderRequest()
    {
        using var app = new TestApp(); ConfigureNative(app);
        using var transport = new CapturedTransport();
        using var factory = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(new CapturedFactory(transport)); }));
        using var client = factory.CreateClient();
        const string query = "/api/v1/channels/webhooks/whatsapp?hub.mode=subscribe&hub.challenge=101010&hub.verify_token=";
        using var valid = await client.GetAsync(query + "synthetic-test-verify-token");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode); Assert.Equal("101010", await valid.Content.ReadAsStringAsync());
        using var invalid = await client.GetAsync(query + "synthetic-invalid-token");
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode); Assert.Empty(transport.Calls);
        using var duplicate = await client.GetAsync(query + "synthetic-test-verify-token&hub.verify_token=synthetic-test-verify-token");
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    public async Task NativeWhatsAppMessageIdReplayIgnoresChangedDeliveryWrapper()
    {
        using var app = new TestApp(); ConfigureNative(app); using var client = app.CreateClient();
        var first = LiveRequest("whatsapp", "HELP", "batch-retry");
        using (var received = await PostNative(client, first)) Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        var root = JsonNode.Parse(first.Body)!.AsObject(); root["entry"]![0]!["delivery_attempt"] = 2;
        using var retry = await PostNative(client, LiveSignature(first with { Body = root.ToJsonString() }));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True(Assert.Single((await retry.Content.ReadFromJsonAsync<ChannelWebhookResult>())!.Results).Replayed);
        Assert.Empty(await app.Services.GetRequiredService<IncidentStore>().ListAsync(default));
    }

    [Fact]
    public async Task NativeWhatsAppVoluntaryLocationUpdatesTheConsentedLinkedCaseForHumanReview()
    {
        using var app = new TestApp(); ConfigureNative(app);
        using var transport = new CapturedTransport();
        using var factory = app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(new CapturedFactory(transport)); }));
        using var resident = factory.CreateClient(); using var staff = factory.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-dispatch-token");
        var boundaries = GeoScenario.Boundaries(TenantContext.CurrentId);
        await factory.Services.GetRequiredService<GeoStore>().SetGeographiesAsync(new(0, boundaries.Municipalities,
            boundaries.Wards, "Synthetic fixture boundary approval for native intake test"), "Synthetic test supervisor", default);
        using (var report = await PostNative(resident, LiveRequest("whatsapp"))) Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        await SendNativeNotice(staff, await OnlyConversation(staff));
        using (var consent = await PostNative(resident, LiveRequest("whatsapp", "YES", "consent"))) Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
        var before = Assert.Single(await factory.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        var point = GeoScenario.Position(0.7, 0.5);
        var body = WhatsApp(new { from = Caller.TrimStart('+'), id = "wamid.native-location", type = "location",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            location = new { longitude = point.Longitude, latitude = point.Latitude, name = "Synthetic report location" } });
        var request = new ChannelWebhookRequest("whatsapp", body, "application/json",
            NativeBase + "/api/v1/channels/webhooks/whatsapp", new Dictionary<string, string>());
        using var location = await PostNative(resident, LiveSignature(request));
        Assert.Equal(HttpStatusCode.OK, location.StatusCode);
        var updated = Assert.Single(await factory.Services.GetRequiredService<IncidentStore>().ListAsync(default));
        Assert.Equal(before.Id, updated.Id); Assert.Equal(point, updated.Location);
        Assert.True(updated.LocationShared); Assert.True(updated.NeedsHumanReview);
        Assert.Null(updated.Approval); Assert.Null(updated.AssignedCrewId);
        Assert.NotNull(updated.MunicipalityId); Assert.NotNull(updated.WardId);
        Assert.Single(transport.Calls);
    }
}
