using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CivicOps.Models;

namespace CivicOps.Services;

public static class SosEndpoints
{
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter() }
    };
    public static IServiceCollection AddCivicOpsSos(this IServiceCollection services)
    { services.AddSingleton<SosStore>(); return services; }

    public static IEndpointRouteBuilder MapCivicOpsSosEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/sos").RequireRateLimiting("intake");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (SosRequestException error) { return Results.Problem(error.Message, statusCode: error.Status); }
            catch (JsonException) { return Results.BadRequest(new { error = "Payload must match the closed SOS schema. Unknown biometric, identification and tracking fields are rejected." }); }
        });
        group.MapPost("", async (JsonElement payload, HttpContext http, SosStore store, CrewStore crew, CancellationToken ct) =>
        {
            var request = Read<SosCreateRequest>(payload);
            string? staff = null; string? crewName = null;
            if (http.User.IsInRole("crew"))
            {
                var actor = await crew.ActorAsync(http.User, ct);
                staff = actor.Name; crewName = actor.CrewId;
                // Coordinates belong on the follow-up call, which checks the visible indicator. Creating an alert must not start tracking.
                if (request.Location is not null) throw new SosRequestException(403, "Crew coordinates are accepted only on the active SOS location call while the visible sharing indicator is already on. No coordinates retained.");
            }
            else if (http.User.Identity?.IsAuthenticated is true && http.User.IsInRole("resident") is false) staff = StaffName(http);
            return Results.Ok(await store.CreateAsync(request, staff, crewName, ct, OwnerKey(http)));
        }).RequireAuthorization("sos-trigger");
        group.MapPost("/{id}/location", async (string id, JsonElement payload, HttpContext http, SosStore store, CrewStore crew, CancellationToken ct) =>
        {
            var request = Read<SosLocationRequest>(payload);
            if (http.User.IsInRole("crew"))
            {
                var actor = await crew.ActorAsync(http.User, ct); var me = await crew.MeAsync(actor, ct);
                if (request.Location is not null && (me.Shift is null || me.Consent is null || !request.VisibleIndicator || (request.ConsentId is not null && request.ConsentId != me.Consent.Id)))
                    throw new SosRequestException(403, "Crew SOS location requires the current active shift, explicit revocable consent and visible sharing indicator. No coordinates retained.");
            }
            return Results.Ok(await store.AddLocationAsync(id, request, StaffName(http), ct, OwnerKey(http), OwnSos(http.User)));
        }).RequireAuthorization("sos-trigger");
        group.MapPost("/{id}/cancel", async (string id, JsonElement payload, HttpContext http, SosStore store, CancellationToken ct) =>
            Results.Ok(RequireAlert(await store.CancelAsync(id, Read<SosActionRequest>(payload), StaffName(http), ct, OwnerKey(http), OwnSos(http.User))))).RequireAuthorization("sos-trigger");
        foreach (var (route, target) in new[] { ("acknowledge", SosStatus.Acknowledged), ("assess", SosStatus.Assessing), ("dispatch", SosStatus.Dispatched), ("resolve", SosStatus.Resolved), ("false-alarm", SosStatus.FalseAlarm) })
        {
            var routeCopy = route; var targetCopy = target;
            group.MapPost("/{id}/" + routeCopy, async (string id, JsonElement payload, HttpContext http, SosStore store, CancellationToken ct) =>
                Results.Ok(RequireAlert(await store.ActAsync(id, targetCopy, Read<SosActionRequest>(payload), StaffName(http), ct)))).RequireAuthorization("dispatch");
        }
        group.MapGet("/queue", async (HttpContext http, SosStore store, CancellationToken ct) => Results.Ok(await store.QueueAsync(StaffName(http), ct))).RequireAuthorization("operations-read");
        group.MapGet("/{id}", async (string id, HttpContext http, SosStore store, CancellationToken ct) => (await store.GetAsync(id, StaffName(http), ct)) is { } alert ? Results.Ok(alert) : Results.NotFound()).RequireAuthorization("operations-read");
        group.MapGet("/stream", async (HttpContext http, SosStore store, CancellationToken ct) =>
        {
            http.Response.ContentType = "text/event-stream"; http.Response.Headers.CacheControl = "no-store";
            await foreach (var alert in store.StreamAsync(ct))
            { await http.Response.WriteAsync("event: sos\ndata: " + JsonSerializer.Serialize(alert, Strict) + "\n\n", ct); await http.Response.Body.FlushAsync(ct); }
        }).RequireAuthorization("operations-read");
        group.MapPost("/inbound", async (JsonElement payload, HttpContext http, IConfiguration configuration, SosStore store, CancellationToken ct) =>
        {
            VerifyIngress(payload, http, configuration);
            var request = Read<SosInboundRequest>(payload); CheckTenant(request.TenantId, http);
            var channel = request.Channel.Trim().ToLowerInvariant(); if (channel is not ("sms" or "whatsapp")) throw new SosRequestException(400, "Inbound SOS channel must be sms or whatsapp.");
            Text(request.Sender, "sender", 320, 1); Text(request.Message, "message", 2000, 1);
            if (!IsKeyword(request.Message)) throw new SosRequestException(400, "Inbound message is not an allow-listed SOS keyword.");
            var operation = "keyword-" + IncidentStore.Hash(TenantContext.CurrentId + ":" + channel + ":" + request.Sender + ":" + request.Message.Trim().ToUpperInvariant());
            var result = await store.CreateAsync(new(operation, channel, request.Message, request.Sender, null, false, null, null, null), null, null, ct);
            return Results.Ok(new { triggered = true, alert = result.Alert, replayed = result.Replayed, locationRequired = result.LocationRequired,
                message = result.Message, emergencyNotice = result.EmergencyNotice, simulated = true });
        }).RequireAuthorization("channel-webhook");
        group.MapPost("/ussd", async (JsonElement payload, HttpContext http, IConfiguration configuration, SosStore store, CancellationToken ct) =>
        {
            VerifyIngress(payload, http, configuration);
            var request = Read<SosUssdRequest>(payload); CheckTenant(request.TenantId, http);
            Text(request.SessionId, "sessionId", 128, 1); Text(request.Text, "text", 128, 1);
            var text = request.Text.Trim().ToUpperInvariant();
            if (text.StartsWith("*120*", StringComparison.Ordinal) && text.EndsWith('#')) text = text[5..^1];
            if (text is not ("1" or "SOS" or "0")) return Results.Ok(new { menu = "1 SOS | 2 Cancel | 0 Exit", triggered = false, simulated = true, emergencyNotice = SosStore.EmergencyNotice });
            if (text == "0") return Results.Ok(new { menu = "Session ended", triggered = false, simulated = true, emergencyNotice = SosStore.EmergencyNotice });
            var sender = string.IsNullOrWhiteSpace(request.Sender) ? "ussd-session:" + request.SessionId : request.Sender;
            var operation = "ussd-" + IncidentStore.Hash(TenantContext.CurrentId + ":" + request.SessionId + ":" + text);
            var result = await store.CreateAsync(new(operation, "ussd", "USSD SOS menu selection", sender, null, false, null, null, null), null, null, ct);
            return Results.Ok(new { menu = "SOS recorded. A control-room human will assess it. 10111/10177/112 for life-threatening emergencies.", triggered = true,
                alert = result.Alert, replayed = result.Replayed, locationRequired = result.LocationRequired,
                message = result.Message, emergencyNotice = result.EmergencyNotice, simulated = true });
        }).RequireAuthorization("channel-webhook");
        return app;
    }
    private static SosAlert RequireAlert(SosAlert? alert) => alert ?? throw new SosRequestException(404, "SOS alert not found.");
    private static bool OwnSos(ClaimsPrincipal user) => user.IsInRole("resident") || user.IsInRole("crew") || user.HasClaim(claim => claim.Type == "civicops:crew-device");
    private static string StaffName(HttpContext http) => http.User.Identity?.Name ?? "Authenticated municipal operator";
    private static string? OwnerKey(HttpContext http) => http.User.Identity?.IsAuthenticated is true
        ? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.Identity.Name : null;
    private static T Read<T>(JsonElement payload)
    { if (payload.ValueKind != JsonValueKind.Object) throw new JsonException(); return payload.Deserialize<T>(Strict) ?? throw new JsonException(); }
    private static void Text(string? value, string name, int max, int min) { if (RequestValidation.Text(value, name, max, min) is { } error) throw new SosRequestException(400, error); }
    private static void CheckTenant(string? requested, HttpContext http)
    { if (!string.IsNullOrWhiteSpace(requested) && requested != TenantContext.CurrentId) throw new SosRequestException(403, "Tenant binding does not match this municipality."); }
    private static void VerifyIngress(JsonElement payload, HttpContext http, IConfiguration configuration)
    {
        // The local demo adapter is deliberately synthetic. Configured deployments must send
        // a bounded HMAC envelope; provider credentials and delivery are never called here.
        if (bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demo) && demo) return;
        var secret = SecretValues.Read(configuration, "CIVICOPS_CHANNEL_INGRESS_SECRET");
        var signature = http.Request.Headers["X-CivicOps-SOS-Signature"].ToString();
        var timestamp = http.Request.Headers["X-CivicOps-SOS-Timestamp"].ToString();
        if (string.IsNullOrWhiteSpace(secret) || !long.TryParse(timestamp, out var unix) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix) > 300)
            throw new SosRequestException(401, "Signed SOS ingress is not configured or the envelope is stale.");
        var canonical = timestamp + "." + payload.GetRawText();
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var supplied = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? signature[7..] : signature;
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied.ToLowerInvariant()), Encoding.ASCII.GetBytes(expected)))
            throw new SosRequestException(401, "Invalid signed SOS ingress envelope.");
    }
    private static bool IsKeyword(string value)
    {
        var canonical = value.Trim().ToUpperInvariant().Replace("’", "'");
        return canonical is "SOS" or "HELP ME" or "HELP" or "HULP MY" or "HULP" or "SIZA" or "NGISIZE" or "USIZO" or "NGICELA USIZO" || canonical.Contains(" SOS ", StringComparison.Ordinal);
    }
}
