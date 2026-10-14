using CivicOps.Models;

namespace CivicOps.Services;

public static class PrivacyEndpoints
{
    public static IEndpointRouteBuilder MapPrivacyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/privacy/notice", (PrivacyService privacy, string? language) => Results.Ok(new { version = PrivacyConsent.Version, language = ResidentMessages.Normalize(language),
            purpose = ResidentMessages.Text("privacy.purpose", language), text = ResidentMessages.Text("privacy.notice", language), informationOfficerContact = privacy.InformationOfficerContact,
            informationOfficerState = string.IsNullOrWhiteSpace(privacy.InformationOfficerContact) ? "owner-pending" : "configured-unverified",
            contactOptional = true, locationOptional = true, locationNoticeVersion = PrivacyConsent.LocationNoticeVersion,
            locationText = ResidentMessages.Text("privacy.locationNotice", language),
            minimisation = "Avoid identity numbers, names and sensitive personal details in narratives." }));
        var group = endpoints.MapGroup("/api/admin/privacy").RequireAuthorization("admin").RequireRateLimiting("dispatch");
        group.MapGet("/requests", async (PrivacyService privacy, CancellationToken ct) => Results.Ok(await privacy.ListAsync(ct)));
        group.MapPost("/requests", async (DataSubjectRequestCreate request, HttpContext http, PrivacyService privacy, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await privacy.CreateAsync(request, Actor(http), ct))));
        group.MapPost("/requests/{id}/verify", async (string id, DataSubjectVerification request, HttpContext http, PrivacyService privacy, CancellationToken ct) =>
            await Handle(async () => Results.Ok(await privacy.VerifyAsync(id, request, Actor(http), ct))));
        group.MapPost("/requests/{id}/fulfil", async (string id, DataSubjectFulfilment request, HttpContext http, PrivacyService privacy, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Content-Disposition"] = "attachment; filename=privacy-response.json";
            return await Handle(async () => Results.Ok(await privacy.FulfilAsync(id, request, Actor(http), ct)));
        });
        return endpoints;
    }
    private static string Actor(HttpContext http) => http.User.Identity?.Name ?? "Authenticated administrator";
    private static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (PrivacyConflictException error) { return Results.Problem(error.Message, statusCode: 409, title: "Privacy request conflict"); }
        catch (InvalidOperationException error) { return Results.Problem(error.Message, statusCode: 400, title: "Invalid privacy request"); }
    }
}
