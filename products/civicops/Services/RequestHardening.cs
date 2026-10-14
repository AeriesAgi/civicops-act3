using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace CivicOps.Services;

public static class RequestHardening
{
    private static bool NativeTwilioForm(HttpRequest request) => request.Path.Value is "/api/v1/channels/webhooks/twilio-sms" or "/api/v1/channels/webhooks/twilio-voice"
        && string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    public static readonly string[] Policies = ["intake", "dispatch", "tools", "session"];
    public static IApplicationBuilder UseCivicOpsMediaType(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        // Run before routing creates its synthetic 415 endpoint (which has no application policy metadata).
        // OIDC callbacks retain the framework handler's validated response-mode/state contract.
        if (http.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE"
            && (http.Request.Path.StartsWithSegments("/api") || http.Request.Path.StartsWithSegments("/open311") || http.Request.Path.StartsWithSegments("/auth"))
            && http.Request.ContentLength is not 0 && (http.Request.ContentType is not null || http.Request.ContentLength > 0)
            && !NativeTwilioForm(http.Request)
            && !string.Equals(http.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        { await Reject(http, 415, "State-changing bodies must use application/json."); return; }
        await next(http);
    });
    public static string Partition(HttpContext http, string policy)
    {
        var identity = http.User.Identity;
        // Demo compatibility is not a verified shared principal: preserve distinct resident IP budgets.
        var principal = identity?.IsAuthenticated is true && identity.AuthenticationType != "demo-compat"
            ? http.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        return TenantContext.CurrentId + ":" + policy + ":" + (principal is not null ? "principal:" + IncidentStore.Hash(principal)
            : "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
    }

    public static IApplicationBuilder UseCivicOpsRequestBounds(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (http.Request.Path.Value?.Length > 2048 || http.Request.QueryString.Value?.Length > 4096 || http.Request.Headers.Count > 64)
        { await Reject(http, 400, "Request metadata exceeds its bounds."); return; }
        foreach (var header in http.Request.Headers)
        {
            var bound = header.Key.ToLowerInvariant() switch { "authorization" => 1031, "idempotency-key" => 200,
                "x-civicops-conversation-id" => 128, "x-civicops-tool-secret" => 4096, _ => 8192 };
            if (header.Value.ToString().Length > bound || header.Value.Any(value => value?.Any(char.IsControl) is true)
                || header.Key.ToLowerInvariant() is "authorization" or "idempotency-key" or "x-civicops-conversation-id" && header.Value.Count != 1)
            { await Reject(http, 400, "Request header is malformed or exceeds its bounds."); return; }
        }
        if (http.Request.Method is not ("POST" or "PATCH" or "PUT" or "DELETE")) { await next(http); return; }
        var limit = http.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize ?? 32768;
        var feature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
        if (http.Request.ContentLength > limit) { await Reject(http, 413, "Request body exceeds its bound."); return; }
        using var bounded = new MemoryStream();
        var original = http.Request.Body;
        var buffer = new byte[8192]; int count;
        while ((count = await original.ReadAsync(buffer, http.RequestAborted)) > 0)
        {
            if (bounded.Length + count > limit) { await Reject(http, 413, "Request body exceeds its bound."); return; }
            bounded.Write(buffer, 0, count);
        }
        bounded.Position = 0;
        if (bounded.Length > 0)
        {
            if (NativeTwilioForm(http.Request))
            {
                bounded.Position = 0; http.Request.Body = bounded;
                try { await next(http); } finally { http.Request.Body = original; }
                return;
            }
            if (!string.Equals(http.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            { await Reject(http, 415, "State-changing bodies must use application/json."); return; }
            try
            {
                using var document = await JsonDocument.ParseAsync(bounded, new JsonDocumentOptions { MaxDepth = 32 }, http.RequestAborted);
                var nodes = 0;
                var photoRequest = http.Request.Path.Value?.StartsWith("/api/v1/crew/me/jobs/", StringComparison.Ordinal) is true && http.Request.Path.Value.EndsWith("/photos", StringComparison.Ordinal);
                if (!ValidJson(document.RootElement, ref nodes, photoRequest ? 2_800_000 : 65536)) { await Reject(http, 400, "JSON contains ambiguous fields, unsafe text or excessive complexity."); return; }
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { await Reject(http, 400, "Malformed JSON body."); return; }
        }
        bounded.Position = 0; http.Request.Body = bounded;
        try { await next(http); } finally { http.Request.Body = original; }
    });

    private static bool ValidJson(JsonElement value, ref int nodes, int textMaximum)
    {
        if (++nodes > 12000) return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
                if (property.Name.Length > 128 || RequestValidation.UnsafeUnicode(property.Name) || !names.Add(property.Name)
                    || !ValidJson(property.Value, ref nodes, property.Name == "dataBase64" ? textMaximum : 65536)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > 1000) return false;
            foreach (var item in value.EnumerateArray()) if (!ValidJson(item, ref nodes, textMaximum)) return false;
        }
        else if (value.ValueKind == JsonValueKind.String && (value.GetString()?.Length > textMaximum || RequestValidation.UnsafeUnicode(value.GetString()))) return false;
        return true;
    }

    private static Task Reject(HttpContext http, int status, string detail) => Results.Problem(detail, statusCode: status).ExecuteAsync(http);

    public static RouteGroupBuilder WithCivicOpsProblems(this RouteGroupBuilder routes)
    {
        routes.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (result is IStatusCodeHttpResult { StatusCode: >= 400 and <= 599 } status && result is not Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)
            {
                var extensions = new Dictionary<string, object?>();
                if (result is IValueHttpResult { Value: not null } value)
                {
                    var options = context.HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
                    var payload = JsonSerializer.SerializeToElement(value.Value, options);
                    if (payload.ValueKind == JsonValueKind.Object)
                        foreach (var field in payload.EnumerateObject())
                            extensions[field.Name] = field.Name is "error" or "message"
                                ? context.HttpContext.RequestServices.GetRequiredService<SensitiveValueRedactor>().Redact(field.Value.ToString())
                                : field.Value.Clone();
                }
                var detail = extensions.TryGetValue("error", out var error) || extensions.TryGetValue("message", out error) ? error?.ToString() : null;
                return Results.Problem(context.HttpContext.RequestServices.GetRequiredService<SensitiveValueRedactor>().Redact(detail),
                    statusCode: status.StatusCode, extensions: extensions);
            }
            return result;
        });
        return routes;
    }
}
