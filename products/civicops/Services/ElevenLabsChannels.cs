using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using Microsoft.AspNetCore.Mvc;

namespace CivicOps.Services;

public sealed class ElevenLabsChannelState(IConfiguration configuration, IWebHostEnvironment environment, DurableStorage? persistence = null)
{
    private readonly DurableStorage _storage = persistence ?? new DurableStorage(configuration, environment);
    public SemaphoreSlim Gate { get; } = new(1, 1);
    private string PathName => Path.Combine(TenantContext.DirectoryFor(DataPaths.Resolve(configuration, environment)), "elevenlabs-webhooks.json");
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public async Task<List<ElevenLabsReceipt>> ReadAsync(CancellationToken ct)
    {
        var persisted = await _storage.ReadAsync(PathName, ct);
        var receipts = persisted is not null ? JsonSerializer.Deserialize<List<ElevenLabsReceipt>>(persisted, _json) ?? [] : [];
        if (receipts.Any(receipt => receipt.TenantId is not null && receipt.TenantId != TenantContext.CurrentId)
            || TenantContext.CurrentId != TenantStore.DefaultTenantId && receipts.Any(receipt => receipt.TenantId is null))
            throw new InvalidDataException("Voice receipt partition municipality mismatch.");
        return receipts.Select(receipt => receipt with { TenantId = receipt.TenantId ?? TenantStore.DefaultTenantId }).ToList();
    }
    public async Task RecordAsync(ElevenLabsReceipt receipt, CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        try
        {
        var list = await ReadAsync(ct);
        var prior = list.FirstOrDefault(value => value.EventKey == receipt.EventKey);
        if (prior is not null)
        { if (prior.BodyHash != receipt.BodyHash) throw new InvalidDataException("Voice receipt key belongs to another body."); return; }
        list.Add(receipt with { TenantId = TenantContext.CurrentId });
        await _storage.WriteAsync(PathName, JsonSerializer.Serialize(list, _json), ct);
        }
        catch { transaction.Abort(); throw; }
    }
}
public sealed record ElevenLabsReceipt(string EventKey, string BodyHash, string? ReferenceNumber, string Type, string? Alert, DateTimeOffset At, string? TenantId = null);

public static class ElevenLabsEndpoints
{
    public static IServiceCollection AddElevenLabs(this IServiceCollection services)
    {
        services.AddSingleton(sp => ElevenLabsOptions.From(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ElevenLabsChannelState>();
        services.AddHttpClient("civicops-elevenlabs", http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton(provider => new ElevenLabsClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient("civicops-elevenlabs"),
            provider.GetRequiredService<ElevenLabsOptions>(), provider.GetRequiredService<TimeProvider>()));
        return services;
    }

    public static IEndpointRouteBuilder MapElevenLabsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/channels/elevenlabs/status", (ElevenLabsOptions options) => Results.Ok(Status(options)));
        app.MapPost("/api/channels/elevenlabs/tools/{tool}", ToolAsync).WithMetadata(new ElevenLabsToolRequired()).RequireRateLimiting("tools");
        app.MapPost("/api/channels/elevenlabs/simulated-chat", SimulatedChatAsync).RequireRateLimiting("intake");
        app.MapPost("/api/channels/elevenlabs/post-call", PostCallAsync)
            .WithMetadata(new RequestSizeLimitAttribute(512 * 1024));
        app.MapGet("/api/channels/elevenlabs/session", SessionAsync).RequireRateLimiting("session");
        app.MapPost("/api/incidents/{id}/callback", CallbackAsync)
            .WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
        app.MapGet("/api/channels/elevenlabs/alerts", async (ElevenLabsChannelState state, CancellationToken ct) =>
            Results.Ok((await state.ReadAsync(ct)).Where(x => x.Alert is not null).Select(x => new { x.ReferenceNumber, x.Alert, x.At })))
            .WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
        return app;
    }

    public static object Status(ElevenLabsOptions options) => new
    {
        mode = options.Simulated ? "simulated" : "live",
        voice = options.Simulated ? "simulated" : string.IsNullOrWhiteSpace(options.AgentId) ? "disabled" : "live",
        chat = options.Simulated ? "simulated" : string.IsNullOrWhiteSpace(options.AgentId) ? "disabled" : "live",
        callbacks = !options.CallbacksEnabled ? "disabled" : options.Simulated ? "simulated" : "live",
        sessionMode = options.SessionMode, languages = options.Simulated ? new[] { "en", "af", "zu" } : new[] { "en", "af" }, uiLanguages = new[] { "en", "af", "zu" },
        privacy = "No audio stored by CivicOps; transcripts available only to authenticated dispatchers."
    };

    private static async Task<IResult> SessionAsync(HttpContext context, ElevenLabsClient client, ElevenLabsOptions options, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (client.Simulated) return Results.Ok(new { mode = "simulated", agentId = (string?)null });
        if (string.IsNullOrWhiteSpace(options.AgentId)) return Results.Problem("Resident agent is not configured.", statusCode: 503);
        // Hard spend cut-off: once the voice session cap is reached the resident falls back to the labelled simulated agent.
        if (context.RequestServices.GetService<SpendCaps>() is { } caps && !caps.TryConsume(SpendCaps.Voice))
            return Results.Ok(new { mode = "simulated", agentId = (string?)null, reason = "voice_cap_reached", label = "Spend cap reached. Simulation mode" });
        if (options.SessionMode == "public")
        {
            var serverLocation = Uri.TryCreate(options.ApiBase, UriKind.Absolute, out var api) ? api.Host switch
            {
                "api.us.elevenlabs.io" => "us", "api.eu.residency.elevenlabs.io" => "eu-residency",
                "api.in.residency.elevenlabs.io" => "in-residency", _ => "global"
            } : "global";
            return Results.Ok(new { mode = "live", agentId = options.AgentId, sessionMode = "public", serverLocation });
        }
        try
        {
            var session = options.SessionMode == "webrtc"
                ? await client.GetConversationTokenAsync(options.AgentId, ct) : await client.GetSignedUrlAsync(options.AgentId, ct);
            var credentialField = options.SessionMode == "webrtc" ? "token" : "signed_url";
            if (session[credentialField] is not JsonValue value || !value.TryGetValue<string>(out var credential) || string.IsNullOrWhiteSpace(credential) || credential.Length > 8192)
                throw new InvalidDataException("ElevenLabs session response omitted session credentials.");
            var result = new JsonObject { ["mode"] = "live", ["sessionMode"] = options.SessionMode, [credentialField] = credential };
            if (options.SessionMode == "webrtc")
            {
                if (session["conversation_id"] is not JsonValue identifier || !identifier.TryGetValue<string>(out var conversationId)
                    || string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 128)
                    throw new InvalidDataException("ElevenLabs session response omitted its conversation identifier.");
                result["conversation_id"] = conversationId;
            }
            else if (!Uri.TryCreate(credential, UriKind.Absolute, out var signedUrl) || signedUrl.Scheme != "wss" || signedUrl.UserInfo.Length > 0)
                throw new InvalidDataException("ElevenLabs session response contained an invalid signed WebSocket URL.");
            return Results.Ok(result);
        }
        catch (ElevenLabsQuotaException)
        {
            // Provider credits exhausted: fall back to the labelled simulated agent instead of showing an error.
            return Results.Ok(new { mode = "simulated", agentId = (string?)null, reason = "voice_cap_reached", label = SpendCaps.Label });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
        { return Results.Problem("Resident session service is temporarily unavailable.", statusCode: 502); }
    }

    private static Task<IResult> SimulatedChatAsync(HttpContext context, ElevenLabsOptions options,
        DispatcherAccess access, IncidentStore store, IncidentCoordinator coordinator, ElevenLabsChannelState state,
        TimeProvider clock, CancellationToken ct)
    {
        // The explicit demo keeps the labelled simulated chat as a fallback even when the live resident agent is enabled.
        if (!options.Simulated && !options.DemoMode) return Task.FromResult(Results.NotFound(new { message = "Simulated resident chat is disabled in live mode." }));
        // This resident-write route permits only report intake, with normal tenant resolution and CSRF.
        // The marker is server-owned; it cannot grant access to the authenticated provider tool route.
        context.Items["simulatedResidentIntake"] = true;
        return ToolAsync("report_incident", context, options, access, store, coordinator, state, clock, ct);
    }

    private static async Task<IResult> ToolAsync(string tool, HttpContext context, ElevenLabsOptions options,
        DispatcherAccess access, IncidentStore store, IncidentCoordinator coordinator, ElevenLabsChannelState state,
        TimeProvider clock, CancellationToken ct)
    {
        var auth = AuthenticateTool(context, options, access.DemoMode);
        if (auth is not null) return auth;
        if (tool is not ("report_incident" or "lookup_case_status" or "add_case_information" or "request_human_handoff" or "list_service_categories"))
            return Results.NotFound(new { message = "Unknown resident tool." });
        JsonElement body;
        try { using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct); body = document.RootElement.Clone(); }
        catch (JsonException) { return Results.BadRequest(new { message = "Send a JSON object." }); }
        if (body.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { message = "Send a JSON object." });
        var error = ValidateToolBody(body);
        if (error is not null) return Results.BadRequest(new { message = error });
        if (tool == "list_service_categories") return Results.Ok(new { services = ServiceCatalog.All, nextStep = ResidentMessages.Text("tool.categoriesPrompt", Text(body, "language")) });
        var conversation = Text(body, "conversation_id") ?? Text(body, "conversationId");
        var turn = Text(body, "turn_id") ?? Text(body, "turnId");
        if (string.IsNullOrWhiteSpace(conversation) || string.IsNullOrWhiteSpace(turn))
            return Results.BadRequest(new { message = "conversation_id and turn_id are required." });
        var headerConversation = context.Request.Headers["X-CivicOps-Conversation-Id"].ToString();
        if (headerConversation.Length > 0 && !string.Equals(headerConversation, conversation, StringComparison.Ordinal))
            return Results.BadRequest(new { message = "Conversation header must match the tool conversation." });
        await state.Gate.WaitAsync(ct);
        try
        {
            if (tool == "report_incident")
            {
                var request = new IncidentRequest(Text(body, "description") ?? "", Text(body, "area"), Text(body, "contact_email"),
                    Source: Text(body, "channel") == "phone" ? "voice" : "chat", SourceLabel: options.Simulated ? "Simulated resident agent" : "ElevenLabs resident agent",
                    ResidentLanguage: Text(body, "language") ?? "en", ContactPreference: Text(body, "contact_preference") ?? "none",
                    ConsentToContact: Bool(body, "consent_to_contact"), CallbackConsent: Bool(body, "callback_consent"),
                    ContactPhone: Text(body, "contact_phone"), ConversationId: conversation,
                    PrivacyConsentAccepted: body.TryGetProperty("privacy_consent_accepted", out var accepted) && accepted.ValueKind is JsonValueKind.True or JsonValueKind.False ? accepted.GetBoolean() : null,
                    PrivacyConsentVersion: Text(body, "privacy_consent_version"));
                error = RequestValidation.Intake(request, null) ?? PrivacyConsent.Validate(request, !access.DemoMode);
                if (error is not null) return Results.BadRequest(new { message = error });
                var created = await coordinator.CreateAsync(request, "elevenlabs:" + IdentityHash(conversation, turn, tool), ct);
                if (created.Error is not null) return Results.Conflict(new { message = created.Error });
                return Results.Ok(new { referenceNumber = created.Incident.ReferenceNumber, status = created.Incident.Status.ToString(),
                    nextStep = ResidentMessages.Text("tool.reportRecorded", created.Incident.ResidentLanguage), replayed = !created.Created,
                    mode = options.Simulated ? "simulated" : "live" });
            }
            var reference = Text(body, "reference_number") ?? Text(body, "referenceNumber") ?? Text(body, "reference");
            var found = string.IsNullOrWhiteSpace(reference) ? null : await store.GetAsync(reference, ct);
            if (found is null) return Results.NotFound(new { message = ResidentMessages.Text("tool.notFound", Text(body, "language")) });
            if (tool == "lookup_case_status") return Results.Ok(new { referenceNumber = found.ReferenceNumber, status = found.Status.ToString(),
                publicUpdate = IncidentView.SafeUpdate(found.Status, found.ResidentLanguage), nextStep = ResidentMessages.Text("tool.dispatchGate", found.ResidentLanguage) });
            // A public case reference is sufficient for read-only status. Mutations need the original conversation.
            if (!MatchesConversation(found, conversation)) return Results.StatusCode(403);
            var information = Text(body, "information") ?? Text(body, "reason") ?? "Resident requested a human handoff.";
            if (RequestValidation.Text(information, "information", 2000, 1) is { } infoError) return Results.BadRequest(new { message = infoError });
            var eventKey = IdentityHash(conversation, turn, tool);
            var fingerprint = Hash(body.GetRawText());
            var replayed = false;
            var changed = await store.ChangeAsync(found.Id, null, incident =>
            {
                var prior = incident.Audit.FirstOrDefault(x => x.Action == $"elevenlabs-tool-{eventKey}");
                if (prior is not null)
                {
                    replayed = true;
                    return prior.Detail.StartsWith(fingerprint + ":", StringComparison.Ordinal) ? "tool-replay" : "This turn already belongs to different information.";
                }
                incident.NeedsHumanReview = true;
                if (DeterministicClassifier.Priority(information) == "Urgent") incident.Priority = "Urgent";
                incident.Audit.Add(new($"elevenlabs-tool-{eventKey}", "ElevenLabs resident tool", fingerprint + ": " + information, clock.GetUtcNow()));
                return null;
            }, ct);
            if (changed.Error is not null && changed.Error != "tool-replay") return Results.Conflict(new { message = changed.Error });
            return Results.Ok(new { referenceNumber = found.ReferenceNumber, nextStep = tool == "request_human_handoff"
                ? ResidentMessages.Text("tool.handoffRecorded", found.ResidentLanguage) : ResidentMessages.Text("tool.infoRecorded", found.ResidentLanguage), replayed });
        }
        finally { state.Gate.Release(); }
    }

    private static async Task<IResult> PostCallAsync(HttpContext context, ElevenLabsOptions options,
        IncidentStore store, IncidentCoordinator coordinator, ElevenLabsChannelState state, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.WebhookSecret)) return Results.Problem("Post-call webhook secret is not configured.", statusCode: 503);
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (stream.Length + count > 512 * 1024) return Results.StatusCode(413);
            stream.Write(buffer, 0, count);
        }
        var bytes = stream.ToArray();
        if (!ElevenLabsSignature.Verify(bytes, context.Request.Headers["ElevenLabs-Signature"], options.WebhookSecret, clock.GetUtcNow()))
            return Results.Unauthorized();
        JsonElement root;
        try { using var document = JsonDocument.Parse(bytes); root = document.RootElement.Clone(); }
        catch (JsonException) { return Results.BadRequest(new { message = "Invalid webhook JSON." }); }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { message = "Webhook data must be an object." });
        var type = Text(root, "type");
        if (type is not ("post_call_transcription" or "post_call_audio" or "call_initiation_failure"))
            return Results.BadRequest(new { message = "Unknown post-call event type." });
        var conversation = Text(data, "conversation_id");
        if (RequestValidation.Text(conversation, "conversation_id", 128, 1) is { } validation) return Results.BadRequest(new { message = validation });
        var eventKey = IdentityHash(type, conversation!);
        var bodyHash = Convert.ToHexString(SHA256.HashData(bytes));
        await state.Gate.WaitAsync(ct);
        try
        {
            var prior = (await state.ReadAsync(ct)).FirstOrDefault(x => x.EventKey == eventKey);
            if (prior is not null) return prior.BodyHash == bodyHash ? Results.Ok(new { received = true, replayed = true, prior.ReferenceNumber })
                : Results.Conflict(new { message = "This conversation event was already processed with different content." });
            if (type == "post_call_audio")
            {
                await store.RecordSystemEventAsync(new("elevenlabs-audio-ignored", "ElevenLabs webhook", "Audio event ignored; no audio retained.", clock.GetUtcNow()), ct);
                await state.RecordAsync(new(eventKey, bodyHash, null, type, null, clock.GetUtcNow()), ct);
                return Results.Ok(new { received = true, ignored = true, message = "Audio is never stored." });
            }
            var incident = (await store.ListAsync(ct)).FirstOrDefault(x => MatchesConversation(x, conversation!));
            if (type == "call_initiation_failure")
            {
                var reason = PiiRedactor.Redact(Limit(Text(data, "failure_reason") ?? "unknown", 100));
                if (incident is not null) await store.ChangeAsync(incident.Id, null, candidate =>
                { candidate.Audit.Add(new("elevenlabs-call-failed", "ElevenLabs webhook", $"Callback failed: {reason}. Human follow-up required.", clock.GetUtcNow())); return null; }, ct);
                else await store.RecordSystemEventAsync(new("elevenlabs-unlinked-call-failed", "ElevenLabs webhook", $"Unlinked call initiation failed: {reason}. Human follow-up required.", clock.GetUtcNow()), ct);
                await state.RecordAsync(new(eventKey, bodyHash, incident?.ReferenceNumber, type, $"Call initiation failed: {reason}. Human follow-up required.", clock.GetUtcNow()), ct);
                return Results.Ok(new { received = true, dispatcherAlert = true });
            }
            var summary = "";
            JsonElement analysis = default;
            if (data.TryGetProperty("analysis", out analysis) && analysis.ValueKind == JsonValueKind.Object) summary = Limit(Text(analysis, "transcript_summary") ?? "", 4000);
            var collected = analysis.ValueKind == JsonValueKind.Object && analysis.TryGetProperty("data_collection_results", out var results) && results.ValueKind == JsonValueKind.Object ? results : default;
            var description = Collected(collected, "description") ?? Collected(collected, "what");
            string? dispatcherAlert = null;
            if (incident is null && !string.IsNullOrWhiteSpace(description) && description.Trim().Length >= 10)
            {
                var request = new IncidentRequest(Limit(description, 4000), Limit(Collected(collected, "area") ?? "Unspecified", 200),
                    Source: "voice", SourceLabel: "ElevenLabs post-call review", ResidentLanguage: Limit(Collected(collected, "language") ?? "en", 12),
                    ConversationId: conversation, PrivacyConsentAccepted: CollectedBoolean(collected, "privacy_consent_accepted"),
                    PrivacyConsentVersion: Collected(collected, "privacy_consent_version"));
                if (PrivacyConsent.Validate(request, !options.DemoMode) is { } consentError)
                {
                    await state.RecordAsync(new(eventKey, bodyHash, null, type, "Unlinked report not created: explicit current privacy-purpose consent was not collected.", clock.GetUtcNow()), ct);
                    return Results.Ok(new { received = true, dispatcherAlert = true, message = consentError });
                }
                var created = await coordinator.CreateAsync(request, $"elevenlabs-post-call:{conversation}", ct);
                if (created.Error is not null) return Results.Conflict(new { message = created.Error });
                incident = created.Incident;
            }
            if (incident is not null)
            {
                var transcript = Transcript(data);
                var metadata = SafeMetadata(data, collected);
                var dangerText = string.Join(' ', new[] { Collected(collected, "hazards") ?? "", description ?? "", ResidentTranscript(data) });
                var urgent = DeterministicClassifier.Priority(dangerText.ToLowerInvariant()) == "Urgent";
                var humanHandoff = CollectedBoolean(collected, "human_handoff_requested") == true;
                if (urgent) dispatcherAlert = "Post-call danger language detected. Immediate dispatcher review required; emergency services redirection applies.";
                else if (humanHandoff) dispatcherAlert = "Resident requested a human handoff. Dispatcher review required.";
                await store.ChangeAsync(incident.Id, null, candidate =>
                {
                    if (candidate.Audit.Any(x => x.Action == $"elevenlabs-post-call-{eventKey}")) return "webhook-replay";
                    candidate.TranscriptSummary = PiiRedactor.Redact(summary);
                    candidate.RawTranscript = PiiRedactor.Redact(transcript);
                    candidate.CallMetadata = PiiRedactor.RedactJson(JsonSerializer.SerializeToElement(JsonNode.Parse(metadata))).GetRawText();
                    if (candidate.Approval is null || humanHandoff) candidate.NeedsHumanReview = true;
                    if (urgent)
                    {
                        candidate.Priority = "Urgent";
                        candidate.NeedsHumanReview = true;
                        candidate.Audit.Add(new("elevenlabs-danger-followup", "ElevenLabs webhook", "Post-call danger language requires immediate human review; emergency redirection remains mandatory.", clock.GetUtcNow()));
                    }
                    candidate.Audit.Add(new($"elevenlabs-post-call-{eventKey}", "ElevenLabs webhook", "Transcript and data-collection results attached for human review; no dispatch action taken.", clock.GetUtcNow()));
                    return null;
                }, ct);
            }
            await state.RecordAsync(new(eventKey, bodyHash, incident?.ReferenceNumber, type, dispatcherAlert ?? (incident is null ? "Unlinked conversation requires review; no reportable description was collected." : null), clock.GetUtcNow()), ct);
            return Results.Ok(new { received = true, referenceNumber = incident?.ReferenceNumber, needsHumanReview = true });
        }
        finally { state.Gate.Release(); }
    }

    private static async Task<IResult> CallbackAsync(string id, HttpContext context, DispatcherAccess access,
        ElevenLabsOptions options, ElevenLabsClient client, IncidentStore store, ElevenLabsChannelState state, TimeProvider clock, ChannelStore channels, CancellationToken ct)
    {
        var auth = access.Check(context.Request);
        if (auth is not null) return auth;
        if (!options.CallbacksEnabled) return Results.Problem("Callbacks disabled: configure ELEVENLABS_AGENT_PHONE_NUMBER_ID.", statusCode: 503);
        var incident = await store.GetAsync(id, ct);
        if (incident is null) return Results.NotFound();
        if (!incident.CallbackConsent || !incident.ConsentToContact || string.IsNullOrWhiteSpace(incident.ContactPhone))
            return Results.Conflict(new { message = "Recorded contact and callback consent, plus a contact phone, are required." });
        if (await channels.IsSuppressedAsync(incident.ContactPhone!, "voice", ct))
            return Results.Conflict(new { message = "Callback blocked: this number has opted out of voice or all channels." });
        var agent = options.CallbackAgentId;
        if (!client.Simulated && string.IsNullOrWhiteSpace(agent)) return Results.Problem("Callback agent is not configured.", statusCode: 503);
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        if (RequestValidation.Text(key, "Idempotency-Key", 200, 1) is { } error) return Results.BadRequest(new { message = error });
        await state.Gate.WaitAsync(ct);
        try
        {
            incident = await store.GetAsync(id, ct) ?? incident;
            if (!incident.CallbackConsent || !incident.ConsentToContact || string.IsNullOrWhiteSpace(incident.ContactPhone))
                return Results.Conflict(new { message = "Callback consent or contact changed. Refresh the case before calling." });
            if (await channels.IsSuppressedAsync(incident.ContactPhone!, "voice", ct))
                return Results.Conflict(new { message = "Callback blocked: this number has opted out of voice or all channels." });
            var keyHash = IdentityHash("callback", incident.Id, key);
            var prior = incident.Audit.FirstOrDefault(x => x.Action == $"elevenlabs-callback-{keyHash}");
            if (prior is not null) return Results.Ok(new { mode = options.Simulated ? "simulated" : "live", replayed = true, incident.ReferenceNumber });
            // Persist intent before the external side effect. Ambiguous transport failures require human reconciliation.
            await store.ChangeAsync(incident.Id, null, candidate =>
            { candidate.Audit.Add(new($"elevenlabs-callback-{keyHash}", access.Identity(context), "Callback request recorded. Retry suppressed; check provider conversation before requesting another call.", clock.GetUtcNow())); return null; }, ct);
            try
            {
                var response = await client.OutboundCallAsync(agent ?? "simulated-callback-agent", options.PhoneNumberId!, incident.ContactPhone!,
                    new() { ["case_reference"] = incident.ReferenceNumber, ["case_status"] = incident.Status.ToString(), ["public_update"] = incident.PublicUpdate, ["resident_language"] = incident.ResidentLanguage }, options.PhoneProvider, ct);
                if (response["success"] is not JsonValue success || !success.TryGetValue<bool>(out var started) || !started)
                    throw new InvalidDataException("ElevenLabs did not confirm the callback request.");
                if (response["message"] is not JsonValue message || !message.TryGetValue<string>(out var providerMessage) || providerMessage is null || providerMessage.Length > 2048)
                    throw new InvalidDataException("ElevenLabs callback response omitted a valid message.");
                string? conversationId = null;
                if (response["conversation_id"] is not null && (response["conversation_id"] is not JsonValue identifier
                    || !identifier.TryGetValue<string>(out conversationId) || string.IsNullOrWhiteSpace(conversationId) || conversationId.Length > 128))
                    throw new InvalidDataException("ElevenLabs callback response contained an invalid conversation identifier.");
                await store.ChangeAsync(incident.Id, null, candidate =>
                { candidate.Audit.Add(new("elevenlabs-callback", access.Identity(context), $"{(options.Simulated ? "Simulated" : "Live")} callback: {conversationId ?? "provider-pending"}", clock.GetUtcNow())); return null; }, ct);
                return Results.Ok(new { mode = options.Simulated ? "simulated" : "live", incident.ReferenceNumber,
                    conversationId, message = options.Simulated ? "Simulated callback; no call placed." : "Callback request submitted." });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
            {
                await store.ChangeAsync(incident.Id, null, candidate =>
                { candidate.Audit.Add(new("elevenlabs-callback-uncertain", access.Identity(context), "Provider response unavailable. Check provider before retrying; callback may have started.", clock.GetUtcNow())); return null; }, CancellationToken.None);
                return Results.Problem("Callback outcome needs dispatcher reconciliation before another request.", statusCode: 502);
            }
        }
        finally { state.Gate.Release(); }
    }

    private static IResult? AuthenticateTool(HttpContext context, ElevenLabsOptions options, bool demo)
    {
        if (context.Items["simulatedResidentIntake"] is true && (options.Simulated || options.DemoMode)) return null;
        if (context.User.HasClaim("civicops:scope", "tools:invoke") && context.User.HasClaim(claim => claim.Type == "civicops:service-account")) return null;
        if (string.IsNullOrWhiteSpace(options.ToolSecret)) return demo ? null : Results.Problem("Resident tool authentication is not configured.", statusCode: 503);
        return ElevenLabsSignature.EqualSecret(options.ToolSecret, context.Request.Headers["X-CivicOps-Tool-Secret"].ToString()) ? null : Results.Unauthorized();
    }
    private static string? ValidateToolBody(JsonElement body)
    {
        foreach (var property in body.EnumerateObject())
        {
            if (property.Name.Length > 80) return "Field names must contain at most 80 characters.";
            var limit = property.Name switch { "description" => 4000, "information" or "reason" => 2000, "area" => 200, "contact_email" => 254,
                "conversation_id" or "conversationId" or "turn_id" or "turnId" or "caller_id" => 128, "language" => 12, "contact_phone" => 40, _ => 200 };
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False)) return $"{property.Name} must be text or a consent boolean.";
            if (property.Name is "consent_to_contact" or "callback_consent" or "privacy_consent_accepted")
            { if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return $"{property.Name} must be a boolean."; }
            else if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) return $"{property.Name} must be text.";
            if (property.Value.ValueKind == JsonValueKind.String && RequestValidation.Text(property.Value.GetString(), property.Name, limit) is { } error) return error;
        }
        return null;
    }
    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static bool Bool(JsonElement element, string name) => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
    private static string Limit(string value, int max) => value.Length > max ? value[..max] : value;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string IdentityHash(params string[] values) => Hash(JsonSerializer.Serialize(values));
    private static bool MatchesConversation(Incident incident, string conversation) => incident.ConversationId == conversation
        || incident.Audit.Any(a => a.Action == "elevenlabs-callback" && a.Detail.EndsWith(": " + conversation, StringComparison.Ordinal));
    private static string? Collected(JsonElement collection, string name)
    {
        if (collection.ValueKind != JsonValueKind.Object || !collection.TryGetProperty(name, out var field)) return null;
        if (field.ValueKind == JsonValueKind.String) return field.GetString();
        return Text(field, "value");
    }
    private static bool? CollectedBoolean(JsonElement collection, string name)
    {
        if (collection.ValueKind != JsonValueKind.Object || !collection.TryGetProperty(name, out var field)) return null;
        if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("value", out var nested)) field = nested;
        return field.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }
    private static string Transcript(JsonElement data)
    {
        if (!data.TryGetProperty("transcript", out var turns) || turns.ValueKind != JsonValueKind.Array) return "";
        var text = new StringBuilder();
        foreach (var turn in turns.EnumerateArray().Take(500))
        {
            text.Append(Limit(Text(turn, "role") ?? "unknown", 20)).Append(": ").AppendLine(Limit(Text(turn, "message") ?? "", 4000));
            if (text.Length >= 64000) break;
        }
        return Limit(text.ToString(), 64000);
    }
    private static string ResidentTranscript(JsonElement data)
    {
        if (!data.TryGetProperty("transcript", out var turns) || turns.ValueKind != JsonValueKind.Array) return "";
        return Limit(string.Join(' ', turns.EnumerateArray().Take(500).Where(turn => Text(turn, "role") == "user").Select(turn => Limit(Text(turn, "message") ?? "", 4000))), 64000);
    }
    private static string SafeMetadata(JsonElement data, JsonElement collected)
    {
        var safe = new JsonObject { ["agent_id"] = Limit(Text(data, "agent_id") ?? "", 128), ["conversation_id"] = Text(data, "conversation_id") };
        if (data.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "start_time_unix_secs", "call_duration_secs", "cost", "termination_reason" })
                if (metadata.TryGetProperty(name, out var value) && value.GetRawText().Length <= 400) safe[name] = JsonNode.Parse(value.GetRawText());
        }
        // Only the declared collection fields are retained. Caller IDs and provider telephone metadata are omitted.
        var values = new JsonObject();
        foreach (var name in new[] { "description", "area", "language", "hazards", "contact_preference", "case_reference", "human_handoff_requested" })
            if (Collected(collected, name) is { } value) values[name] = Limit(value, name == "description" ? 4000 : 200);
        if (CollectedBoolean(collected, "human_handoff_requested") is { } handoff) values["human_handoff_requested"] = handoff;
        safe["data_collection"] = values;
        return safe.ToJsonString();
    }
}
