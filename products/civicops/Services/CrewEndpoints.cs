using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CivicOps.Models;
using Microsoft.AspNetCore.Mvc;

namespace CivicOps.Services;

public static class CrewEndpoints
{
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    public static IServiceCollection AddCivicOpsCrew(this IServiceCollection services)
    {
        services.AddSingleton<CrewStore>(); services.AddSingleton<CrewJobService>();
        // Challenge state is process-local like existing sessions; a restart requires new native sign-in.
        services.AddHttpClient("crew-oidc", client => { client.Timeout = TimeSpan.FromSeconds(8); client.MaxResponseContentBufferSize = 262144; });
        services.AddSingleton(provider => new CrewOidcClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient("crew-oidc"),
            provider.GetRequiredService<IConfiguration>(), provider.GetRequiredService<IdentityOptions>(), provider.GetRequiredService<CrewStore>(),
            provider.GetRequiredService<TenantStore>(), provider.GetRequiredService<WorkforceStore>(), provider.GetRequiredService<TimeProvider>()));
        services.AddHostedService<CrewRetentionService>(); return services;
    }
    public static IEndpointRouteBuilder MapCivicOpsCrewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/crew").RequireRateLimiting("dispatch");
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (CrewRequestException error) { return Results.Problem(error.Message, statusCode: error.Status); }
            catch (JsonException) { return Results.BadRequest(new { error = "Payload must match the closed crew schema. Unknown identification, tracking and biometric fields are rejected." }); }
            catch (InvalidDataException) { return Results.Problem("Authenticated crew state is unavailable. A supervisor must investigate storage integrity.", statusCode: 503); }
        });
        group.MapGet("/auth/config", (CrewOidcClient oidc) => Results.Ok(oidc.Configuration));
        group.MapPost("/auth/challenge", (JsonElement payload, CrewOidcClient oidc) =>
        { if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any()) return Results.BadRequest(new { error = "Challenge requires an empty object." }); return Results.Ok(oidc.Challenge()); }).RequireRateLimiting("session");
        group.MapPost("/auth/oidc", async (JsonElement payload, HttpContext http, CrewOidcClient oidc, IncidentStore audit, TimeProvider time, CancellationToken ct) =>
        {
            try
            {
                var expectedTenant = http.Items["explicitTenant"] is true ? http.Items["residentTenant"] as string : null;
                return Results.Ok(await oidc.ExchangeAsync(Read<CrewOidcRequest>(payload), expectedTenant, ct));
            }
            catch (CrewRequestException error)
            {
                await audit.RecordSystemEventAsync(new("crew.native-sign-in-rejected", "CivicOps native sign-in guard",
                    $"Rejected native sign-in with status {error.Status}. No issuer token, claims, nonce or device secret retained.", time.GetUtcNow()), ct);
                throw;
            }
        }).RequireRateLimiting("session");
        group.MapPost("/pairing", async (JsonElement payload, HttpContext http, CrewStore store, CancellationToken ct) => Results.Ok(await store.PairAsync(Read<CrewPairingRequest>(payload), Name(http), ct)));
        group.MapPost("/pairing/exchange", async (JsonElement payload, HttpContext http, CrewStore store, TenantStore tenants, CancellationToken ct) =>
        {
            var request = Read<CrewPairingExchangeRequest>(payload);
            if (!TenantContext.IsValidId(request.TenantId) || await tenants.GetAsync(request.TenantId, ct) is null) return Results.Unauthorized();
            if (http.Items["explicitTenant"] is true && request.TenantId != http.Items["residentTenant"] as string) return Results.StatusCode(403);
            using var scope = TenantContext.Use(request.TenantId);
            return await store.ExchangeAsync(request, ct) is { } issued ? Results.Ok(issued) : Results.Unauthorized();
        }).RequireRateLimiting("session");
        group.MapGet("/devices", async (CrewStore store, CancellationToken ct) => Results.Ok(await store.DevicesAsync(ct)));
        group.MapPost("/devices/{id}/revoke", async (string id, JsonElement payload, HttpContext http, CrewStore store, CancellationToken ct) =>
            await store.RevokeAsync(id, Read<CrewRevokeRequest>(payload).Reason, Name(http), ct) ? Results.Ok(new { revoked = true }) : Results.NotFound());
        group.MapGet("/policy", async (CrewStore store, CancellationToken ct) => Results.Ok(await store.PolicyAsync(ct)));
        group.MapPut("/policy", async (JsonElement payload, HttpContext http, CrewStore store, CancellationToken ct) => Results.Ok(await store.SetPolicyAsync(Read<CrewPolicyRequest>(payload), Name(http), ct)));
        group.MapPost("/retention/purge", async (CrewStore store, CrewJobService jobs, CancellationToken ct) => Results.Ok(new { pointsPurged = await store.PurgeAsync(ct), photosPurged = await jobs.PurgePhotosAsync(ct) }));
        group.MapGet("/me", async (HttpContext http, CrewStore store, CancellationToken ct) => Results.Ok(await store.MeAsync(await store.ActorAsync(http.User, ct), ct)));
        group.MapGet("/me/jobs", async (HttpContext http, CrewStore store, CrewJobService jobs, CancellationToken ct) => Results.Ok(await jobs.JobsAsync(await store.ActorAsync(http.User, ct), ct)));
        group.MapGet("/me/jobs/{id}", async (string id, HttpContext http, CrewStore store, CrewJobService jobs, CancellationToken ct) => await jobs.JobAsync(await store.ActorAsync(http.User, ct), id, ct) is { } job ? Results.Ok(job) : Results.NotFound());
        group.MapGet("/jobs/{id}", async (string id, HttpContext http, CrewJobService jobs, CancellationToken ct) => await jobs.ReviewJobAsync(id, Name(http), ct) is { } job ? Results.Ok(job) : Results.NotFound());
        group.MapGet("/jobs/{id}/photos/{photoId}", async (string id, string photoId, HttpContext http, CrewJobService jobs, CancellationToken ct) =>
        {
            var file = await jobs.ReviewPhotoBytesAsync(id, photoId, Name(http), ct);
            return file is null ? Results.NotFound() : Results.File(file.Value.Data, file.Value.ContentType, photoId + (file.Value.ContentType == "image/png" ? ".png" : ".jpg"));
        });
        group.MapPost("/me/jobs/{id}/status", async (string id, JsonElement payload, HttpContext http, CrewStore store, CrewJobService jobs, CancellationToken ct) => Results.Ok(await jobs.StatusAsync(await store.ActorAsync(http.User, ct), id, Read<CrewJobStatusRequest>(payload), ct)));
        group.MapPost("/me/jobs/{id}/notes", async (string id, JsonElement payload, HttpContext http, CrewStore store, CrewJobService jobs, CancellationToken ct) => Results.Ok(await jobs.NoteAsync(await store.ActorAsync(http.User, ct), id, Read<CrewJobNoteRequest>(payload), ct)));
        group.MapPost("/me/jobs/{id}/photos", async (string id, JsonElement payload, HttpContext http, CrewStore store, CrewJobService jobs, CancellationToken ct) => Results.Ok(await jobs.PhotoAsync(await store.ActorAsync(http.User, ct), id, Read<CrewJobPhotoRequest>(payload), ct))).WithMetadata(new RequestSizeLimitAttribute(3_000_000));
        group.MapGet("/me/jobs/{id}/photos/{photoId}", async (string id, string photoId, HttpContext http, CrewStore store, CrewJobService jobs, CancellationToken ct) =>
        {
            var file = await jobs.PhotoBytesAsync(await store.ActorAsync(http.User, ct), id, photoId, ct);
            return file is null ? Results.NotFound() : Results.File(file.Value.Data, file.Value.ContentType, photoId + (file.Value.ContentType == "image/png" ? ".png" : ".jpg"));
        });
        group.MapPost("/shifts/start", async (JsonElement payload, HttpContext http, CrewStore store, CancellationToken ct) => Results.Ok(await store.StartAsync(await store.ActorAsync(http.User, ct), Read<CrewOperationRequest>(payload), ct)));
        group.MapPost("/shifts/end", async (JsonElement payload, HttpContext http, CrewStore store, CancellationToken ct) => Results.Ok(await store.EndAsync(await store.ActorAsync(http.User, ct), Read<CrewShiftEndRequest>(payload), ct)));
        group.MapPost("/consent", async (JsonElement payload, HttpContext http, CrewStore store, CancellationToken ct) => Results.Ok(await store.ConsentAsync(await store.ActorAsync(http.User, ct), Read<CrewConsentRequest>(payload), ct)));
        group.MapDelete("/consent", async ([FromBody] JsonElement payload, HttpContext http, CrewStore store, CancellationToken ct) => Results.Ok(await store.WithdrawAsync(await store.ActorAsync(http.User, ct), Read<CrewOperationRequest>(payload), ct)));
        group.MapPost("/locations", async (JsonElement payload, HttpContext http, CrewStore store, CancellationToken ct) =>
        {
            var result = await store.LocationsAsync(await store.ActorAsync(http.User, ct), Read<CrewLocationRequest>(payload), ct);
            return result.Accepted < 0 ? Results.Problem("Location requires the current device-owned active shift, current explicit consent and visible sharing indicator. No coordinates retained.", statusCode: 403) : Results.Ok(result);
        });
        group.MapGet("/me/locations", async (HttpContext http, CrewStore store, CancellationToken ct) => Results.Ok(await store.HistoryAsync(await store.ActorAsync(http.User, ct), Integer(http, "page", 1), Integer(http, "limit", 1000), false, ct)));
        group.MapGet("/me/locations/export", async (HttpContext http, CrewStore store, CancellationToken ct) =>
        {
            var history = await store.HistoryAsync(await store.ActorAsync(http.User, ct), 1, 1000, true, ct);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(history, Strict), "application/json", "civicops-own-location-history.json");
        });
        group.MapPost("/sos", async (JsonElement payload, HttpContext http, CrewStore store, SosStore sos, CancellationToken ct) =>
        {
            var actor = await store.ActorAsync(http.User, ct);
            var request = Read<CrewSosRequest>(payload);
            if (request.Location is not null || request.ShareLocation)
                throw new CrewRequestException(400, "The crew SOS route accepts no coordinates. Share a landmark on the active SOS location call only while the visible indicator is already on.");
            CrewStore.Text(request.Note, "note", 2000, 5);
            CrewStore.Text(request.OperationId, "operationId", 128, 8);
            try
            {
                var created = await sos.CreateAsync(new SosCreateRequest(request.OperationId, "crew", request.Note, null, null, false, null, null, null),
                    actor.Name, actor.CrewId, ct, http.User.FindFirstValue(ClaimTypes.NameIdentifier));
                return Results.Ok(new CrewSosResult(created.Alert.Id, created.Alert.Status.ToString(),
                    created.Message + " No responders were called or dispatched. " + SosStore.EmergencyNotice, true, true));
            }
            catch (SosRequestException error) { throw new CrewRequestException(error.Status, error.Message); }
        });
        return app;
    }
    private static string Name(HttpContext http) => http.User.Identity?.Name ?? "Authenticated supervisor";
    private static int Integer(HttpContext http, string name, int fallback) => !http.Request.Query.ContainsKey(name) ? fallback
        : http.Request.Query[name].Count == 1 && int.TryParse(http.Request.Query[name], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new CrewRequestException(400, "History query parameters must be a single bounded integer.");
    private static T Read<T>(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new JsonException();
        var fields = typeof(T).GetProperties().Select(value => JsonNamingPolicy.CamelCase.ConvertName(value.Name)).ToArray();
        var keys = payload.EnumerateObject().Select(value => value.Name).ToArray();
        if (keys.Length != fields.Length || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || fields.Any(value => !keys.Contains(value, StringComparer.Ordinal))) throw new JsonException();
        return payload.Deserialize<T>(Strict) ?? throw new JsonException();
    }
}
