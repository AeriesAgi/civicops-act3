using System.Text;
using System.Text.Json;
using System.Globalization;
using CivicOps.Models;
using Microsoft.AspNetCore.Mvc;

namespace CivicOps.Services;

public static class ChannelEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static IServiceCollection AddCivicOpsChannels(this IServiceCollection services)
    {
        services.AddSingleton<ChannelOptions>(); services.AddSingleton<ChannelStore>(); services.AddSingleton<ChannelService>();
        services.AddHostedService<ChannelRetentionWorker>();
        services.AddHttpClient("civicops-channels", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
    public static IEndpointRouteBuilder MapCivicOpsChannelEndpoints(this IEndpointRouteBuilder routes)
    {
        var channels = routes.MapGroup("");
        channels.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ChannelRequestException error) { return Results.Problem(error.Message, statusCode: error.StatusCode); }
            catch (JsonException) { return Results.Problem("Invalid normalized channel payload.", statusCode: 400); }
        });
        channels.MapGet("/api/v1/channels/readiness", (ChannelService channels) => Results.Ok(channels.Readiness())).RequireRateLimiting("dispatch");
        channels.MapGet("/api/v1/channels/metrics", async (ChannelStore journal, CancellationToken ct) => Results.Ok(await journal.MetricsAsync(ct))).RequireRateLimiting("dispatch");
        channels.MapGet("/api/v1/channels/conversations", async (HttpContext http, DispatcherAccess access, ChannelService channels, CancellationToken ct) =>
            Results.Ok(await channels.ListAsync(access.Identity(http), ct))).RequireRateLimiting("dispatch");
        channels.MapGet("/api/v1/channels/conversations/{id}", async (string id, HttpContext http, DispatcherAccess access, ChannelStore journal, CancellationToken ct) =>
        {
            var conversation = await journal.GetAsync(id, access.Identity(http), ct);
            return conversation is null ? Results.NotFound() : Results.Ok(conversation);
        }).RequireRateLimiting("dispatch");
        channels.MapPost("/api/v1/channels/conversations/{id}/reply", async (string id, ChannelReplyRequest reply,
            HttpContext http, DispatcherAccess access, ChannelService service, CancellationToken ct) =>
            Results.Ok(await service.ReplyAsync(id, reply, access.Identity(http), ct))).RequireRateLimiting("dispatch");
        channels.MapGet("/api/v1/channels/templates", async (ChannelStore journal, CancellationToken ct) => Results.Ok(await journal.TemplatesAsync(ct))).RequireRateLimiting("dispatch");
        channels.MapPut("/api/admin/channel-templates/{key}/{language}", async (string key, string language, ChannelTemplateEdit edit,
            HttpContext http, DispatcherAccess access, ChannelStore journal, CancellationToken ct) =>
            Results.Ok(await journal.EditTemplateAsync(key, language, edit, access.Identity(http), ct))).RequireRateLimiting("dispatch");
        channels.MapPost("/api/admin/channel-templates/preview", async (ChannelTemplatePreview preview, ChannelStore journal, CancellationToken ct) =>
            Results.Ok(await journal.PreviewAsync(preview, ct))).RequireRateLimiting("dispatch");
        channels.MapPost("/api/v1/channels/retention/purge", async (HttpContext http, DispatcherAccess access, ChannelStore journal, CancellationToken ct) =>
            Results.Ok(await journal.PurgeAsync(access.Identity(http), ct))).RequireRateLimiting("dispatch");
        channels.MapPost("/api/v1/channels/simulated/{provider}", SimulatedAsync).RequireRateLimiting("intake");
        channels.MapPost("/api/v1/channels/demo/seed", SeedAsync).RequireRateLimiting("dispatch");
        channels.MapPost("/api/v1/channels/webhooks/{provider}", NativeAsync)
            .WithMetadata(new RequestSizeLimitAttribute(ChannelSignature.MaximumBodyBytes)).RequireRateLimiting("intake");
        channels.MapGet("/api/v1/channels/webhooks/whatsapp", Challenge).RequireRateLimiting("intake");
        return routes;
    }
    private static async Task<string> BodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        try { return await reader.ReadToEndAsync(ct); }
        catch (DecoderFallbackException) { throw new ChannelRequestException(400, "Webhook body must be valid UTF-8."); }
    }
    private static Dictionary<string, string> Headers(HttpRequest request) => request.Headers.ToDictionary(pair => pair.Key,
        pair => pair.Value.Count == 1 ? pair.Value.ToString() : "", StringComparer.OrdinalIgnoreCase);
    private static async Task<IResult> SimulatedAsync(string provider, HttpContext http, ChannelOptions options, ChannelService service, TimeProvider time, CancellationToken ct)
    {
        if (!options.DemoMode) return Results.NotFound();
        var body = await BodyAsync(http.Request, ct);
        var envelope = new ChannelWebhookRequest(provider, body, "application/json", TenantContext.CurrentId, Headers(http.Request));
        if (!ChannelSignature.VerifyIngress(envelope, ChannelOptions.SyntheticSigningKey, time.GetUtcNow())) return Results.Unauthorized();
        var message = JsonSerializer.Deserialize<InboundMessage>(body, Json) ?? throw new ChannelRequestException(400, "Normalized message required.");
        if (message.Provider != provider) throw new ChannelRequestException(400, "Provider path and message must match.");
        var results = await service.ReceiveAsync([message], IncidentStore.Hash(body), provider, http.Request.Headers["X-CivicOps-Nonce"].ToString(), true, ct);
        return Results.Ok(results.Single());
    }
    private static async Task<IResult> NativeAsync(string provider, HttpContext http, ChannelOptions options, IConfiguration configuration,
        ChannelService service, ChannelStore journal, TimeProvider time, CancellationToken ct)
    {
        if (!options.CanReceiveNative || options.DemoMode) return Results.Problem("Native channels disabled; configure an authenticated signed relay.", statusCode: 503);
        var body = await BodyAsync(http.Request, ct);
        var publicUrl = options.PublicUrl(provider, http.Request.PathBase.ToString());
        var envelope = new ChannelWebhookRequest(provider, body, http.Request.ContentType ?? "", publicUrl, Headers(http.Request));
        var messages = ChannelAdapters.CreateInbound(provider, configuration, false).VerifyAndParse(envelope, time.GetUtcNow());
        var results = await service.ReceiveAsync(messages, IncidentStore.Hash(body), provider, http.Request.Headers["X-CivicOps-Nonce"].ToString(), false, ct);
        if (provider == "twilio-voice")
        {
            var final = results.FirstOrDefault(result => result.ReferenceNumber is not null || result.Command is "HUMAN" or "CALLBACK" or "SOS" or "STOP" or "STOPALL");
            if (final is not null) return Results.Text(new System.Xml.Linq.XElement("Response", new System.Xml.Linq.XElement("Say", final.Reply)).ToString(), "application/xml");
            foreach (var result in results) await journal.RecordIvrNoticeAsync(result.ConversationId, ct);
            return Results.Text(TwilioVoiceChannel.BuildVoiceResponse(publicUrl, false, configuration["CIVICOPS_ELEVENLABS_INBOUND_SIP_URI"]), "application/xml");
        }
        return Results.Ok(new ChannelWebhookResult(true, results));
    }
    private static IResult Challenge(HttpContext http, ChannelOptions options, IConfiguration configuration)
    {
        if (!options.CanReceiveNative || options.DemoMode) return Results.Problem("Native WhatsApp verification is disabled.", statusCode: 503);
        _ = options.PublicUrl("whatsapp", http.Request.PathBase.ToString());
        var query = http.Request.Query;
        if (query.Any(pair => pair.Value.Count != 1)) return Results.BadRequest();
        var challenge = query["hub.challenge"].ToString();
        if (challenge.Length is < 1 or > 200 || !challenge.All(char.IsAsciiDigit)) return Results.BadRequest();
        var adapter = (WhatsAppCloudChannel)ChannelAdapters.CreateInbound("whatsapp", configuration, false);
        return adapter.VerifyChallenge(query["hub.mode"].ToString(), query["hub.verify_token"].ToString(), challenge)
            ? Results.Text(challenge, "text/plain") : Results.Unauthorized();
    }
    private static async Task<IResult> SeedAsync(ChannelOptions options, ChannelService service, TimeProvider time, CancellationToken ct)
    {
        if (!options.DemoMode) return Results.NotFound();
        var now = time.GetUtcNow(); var results = new List<ChannelIntakeResult>();
        foreach (var (provider, channel, language, from, text) in new[] {
            ("twilio-sms", "sms", "en", "+270000000011", "REPORT A burst water pipe is flooding the road in synthetic Ward 2."),
            ("whatsapp", "whatsapp", "zu", "+270000000022", "Amanzi ayavuza emgwaqweni. Ngicela usizo e-Synthetic Ward 3."),
            ("elevenlabs", "voice", "af", "+270000000033", "Geen water in die sintetiese oefenstraat nie. Help asseblief.") })
        {
            var message = new InboundMessage(channel, provider, from, "+270000000099", text, language, now, "demo-round11-" + provider,
                Area: "Synthetic municipal training scenario", TranscriptSummary: channel == "voice" ? text : null,
                HumanHandoffRequested: channel == "voice", CallbackRequested: channel == "voice",
                PrivacyConsentAccepted: true, PrivacyConsentVersion: PrivacyConsent.Version);
            // Stable message content/fingerprint for repeatable seed; received-at is provider metadata only.
            var stable = message with { ReceivedAt = DateTimeOffset.Parse("2026-10-09T00:00:00Z", CultureInfo.InvariantCulture) };
            results.AddRange(await service.ReceiveAsync([message], IncidentStore.Hash(JsonSerializer.Serialize(stable, Json)),
                provider, Guid.NewGuid().ToString("N"), true, ct));
        }
        return Results.Ok(new ChannelSeedResult(results.Count, true, true, results));
    }
}
