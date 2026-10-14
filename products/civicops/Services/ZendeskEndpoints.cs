using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

public static class ZendeskWebhookSignature
{
    public static bool Verify(string? signature, string? timestamp, byte[] rawBody, string? secret, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(timestamp)
            || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            || (clock.GetUtcNow() - instant).Duration() > TimeSpan.FromMinutes(5)) return false;
        byte[] supplied;
        try { supplied = Convert.FromBase64String(signature); } catch (FormatException) { return false; }
        var timestampBytes = Encoding.UTF8.GetBytes(timestamp);
        var signed = new byte[timestampBytes.Length + rawBody.Length];
        timestampBytes.CopyTo(signed, 0);
        rawBody.CopyTo(signed, timestampBytes.Length);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}

public static class ZendeskEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    public static IServiceCollection AddZendeskIntegration(this IServiceCollection services)
    {
        services.AddSingleton(provider => ZendeskOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<IWebHostEnvironment>()));
        services.AddHttpClient("civicops-zendesk", http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton(provider => new ZendeskClient(provider.GetRequiredService<IHttpClientFactory>().CreateClient("civicops-zendesk"),
            provider.GetRequiredService<ZendeskOptions>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<DurableStorage>()));
        services.AddSingleton<IntegrationOutbox>();
        services.AddHostedService<IntegrationOutboxWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapZendeskEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/channels/zendesk/status", async (IntegrationOutbox outbox, CancellationToken ct) =>
            Results.Ok(await outbox.GetStatusAsync(ct)));
        app.MapGet("/api/integrations/outbox", async (IntegrationOutbox outbox, CancellationToken ct) =>
            Results.Ok(new { status = await outbox.GetStatusAsync(ct), items = (await outbox.ListAsync(ct)).Select(x => new
            {
                x.Id, x.CaseReference, x.Operation, x.State, x.Attempts, x.CreatedAt, x.NextAttemptAt,
                x.DeliveredAt, x.LastError, x.TicketId
            }) })).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
        app.MapPost("/api/integrations/outbox/{id}/retry", async (string id, IntegrationOutbox outbox, CancellationToken ct) =>
            await outbox.RetryDeadLetterAsync(id, ct) ? Results.Ok(new { queued = true }) : Results.NotFound())
            .WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
        app.MapPost("/api/channels/zendesk/webhook", HandleWebhookAsync).RequireRateLimiting("intake")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(65_536));
        return app;
    }

    private static async Task<IResult> HandleWebhookAsync(HttpRequest request, ZendeskOptions options,
        IncidentStore store, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.WebhookSecret))
            return Results.Problem("Zendesk webhook verification is not configured.", statusCode: 503);
        var bytes = await ReadBodyAsync(request, ct);
        if (bytes is null) return Results.StatusCode(413);
        if (!ZendeskWebhookSignature.Verify(request.Headers["X-Zendesk-Webhook-Signature"],
            request.Headers["X-Zendesk-Webhook-Signature-Timestamp"], bytes, options.WebhookSecret, clock))
            return Results.Unauthorized();
        ZendeskWebhookEvent? webhook;
        try { webhook = JsonSerializer.Deserialize<ZendeskWebhookEvent>(bytes, WebJson); }
        catch (JsonException) { return Results.BadRequest(new { error = "Webhook payload must be an object with valid field types." }); }
        if (webhook is null || string.IsNullOrWhiteSpace(webhook.EventId) || webhook.EventId.Length > 128
            || string.IsNullOrWhiteSpace(webhook.CaseReference) || webhook.CaseReference.Length > 64
            || webhook.Comment?.Length > 4_000 || webhook.Status?.Length > 64 || webhook.Source?.Length > 64)
            return Results.BadRequest(new { error = "Webhook fields exceed bounds or required fields are missing." });
        if (webhook.Source == "civicops" || webhook.Comment?.Contains("[CivicOps event ", StringComparison.Ordinal) == true)
            return Results.Ok(new { ignored = true, reason = "CivicOps origin loop guard" });
        var replayed = false;
        var changed = await store.ChangeAsync(webhook.CaseReference, null, incident =>
        {
            // Hash the complete event id: delimiter-bearing ids must never match another event's prefix.
            var marker = $"Zendesk event {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(webhook.EventId)))}:";
            if (incident.Audit.Any(x => x.Action == "zendesk-webhook" && x.Detail.StartsWith(marker, StringComparison.Ordinal)))
            { replayed = true; return "replayed"; }
            if (webhook.TicketId is > 0 && incident.ZendeskTicketId is not null && webhook.TicketId != incident.ZendeskTicketId)
                return "Webhook ticket does not match this case.";
            incident.LastZendeskEventId = webhook.EventId;
            incident.NeedsHumanReview = true;
            incident.Audit.Add(new("zendesk-webhook", "Verified case record update",
                $"{marker} upstream status={webhook.Status ?? "unchanged"}; agent reply={webhook.Comment ?? "none"}. Dispatcher review required.", clock.GetUtcNow()));
            return null;
        }, ct);
        if (changed.Incident is null) return Results.NotFound();
        if (replayed) return Results.Ok(new { replayed = true });
        if (changed.Error is not null) return Results.BadRequest(new { error = changed.Error });
        return Results.Ok(new { recorded = true, referenceNumber = changed.Incident.ReferenceNumber, needsHumanReview = true });
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength is > 65_536) return null;
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (memory.Length + count > 65_536) return null;
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
}
