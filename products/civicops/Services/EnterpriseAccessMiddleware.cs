using System.Security.Claims;
using CivicOps.Models;
using Microsoft.AspNetCore.Antiforgery;

namespace CivicOps.Services;

public static class EnterpriseAccessMiddleware
{
    private static readonly string[] ElevatedCrewReadRoles = ["dispatcher", "supervisor", "admin", "auditor"];
    private static readonly string[] ElevatedCrewWriteRoles = ["dispatcher", "supervisor", "admin"];
    public static IApplicationBuilder UseCivicOpsTenantPath(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (OperationalHealth.IsGlobalProbe(http))
        {
            http.Items["residentTenant"] = TenantContext.DefaultTenantId;
            http.Items["explicitTenant"] = false;
            await next(http); return;
        }
        var tenants = http.RequestServices.GetRequiredService<TenantStore>();
        var path = http.Request.Path.Value ?? "/";
        Tenant? resolved = null;
        if (path.StartsWith("/m/", StringComparison.Ordinal))
        {
            var parts = path.Split('/', 4);
            resolved = await tenants.ResolveSlugAsync(parts[2], http.RequestAborted);
            if (resolved is null) { http.Response.StatusCode = 404; return; }
            http.Request.PathBase = http.Request.PathBase.Add(new PathString("/m/" + parts[2]));
            http.Request.Path = new PathString(parts.Length == 4 ? "/" + parts[3] : "/");
        }
        resolved ??= await tenants.ResolveHostAsync(http.Request.Host.Host, http.RequestAborted);
        http.Items["residentTenant"] = resolved?.Id ?? TenantContext.DefaultTenantId;
        http.Items["explicitTenant"] = resolved is not null;
        await next(http);
    });

    public static IApplicationBuilder UseCivicOpsPrincipal(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (OperationalHealth.IsGlobalProbe(http))
        {
            // Public process/storage probes must not depend on credential, tenant or catalogue persistence.
            http.User = new ClaimsPrincipal();
            await next(http); return;
        }
        var authorization = http.Request.Headers.Authorization.ToString();
        var dispatch = http.RequestServices.GetRequiredService<DispatcherAccess>();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization.Length is > 7 and <= 1031 ? authorization[7..].Trim() : "";
            var principal = await http.RequestServices.GetRequiredService<CrewStore>().AuthenticateAsync(token, http.RequestAborted)
                ?? await http.RequestServices.GetRequiredService<ServiceAccountStore>().AuthenticateAsync(token, http.RequestAborted)
                ?? dispatch.AuthenticateBootstrap(http)
                ?? http.RequestServices.GetRequiredService<TrainingAccess>().AuthenticateBootstrap(http);
            if (principal is null)
            {
                http.User = new ClaimsPrincipal(); http.Items["unrecognizedBearer"] = true;
                if (http.GetEndpoint()?.Metadata.GetMetadata<TrainingRequired>() is null) http.Items["accessBlocked"] = Results.Unauthorized();
            }
            else http.User = principal;
        }
        var tool = http.GetEndpoint()?.Metadata.GetMetadata<ElevenLabsToolRequired>() is not null;
        if (http.User.Identity?.IsAuthenticated is not true && tool && http.Items["unrecognizedBearer"] is not true)
        {
            var options = http.RequestServices.GetRequiredService<ElevenLabsOptions>();
            if (!string.IsNullOrWhiteSpace(options.ToolSecret)
                && ElevenLabsSignature.EqualSecret(options.ToolSecret, http.Request.Headers["X-CivicOps-Tool-Secret"].ToString()))
                http.User = Principal("Bootstrap ElevenLabs tool", "agent", "default", "tool-bootstrap");
            else if (dispatch.DemoMode && string.IsNullOrWhiteSpace(options.ToolSecret))
                http.User = Principal("Demo agent (unverified)", "agent", http.Items["residentTenant"] as string ?? "default", "demo-compat");
        }
        // Preserve the zero-configuration walkthrough only while explicit demo mode is enabled.
        var webhook = http.Request.Path.Value is "/api/channels/zendesk/webhook" or "/api/channels/elevenlabs/post-call"
            || http.Request.Path.StartsWithSegments("/api/v1/channels/webhooks");
        if (http.User.Identity?.IsAuthenticated is not true && dispatch.DemoMode && !tool && !webhook && http.Items["unrecognizedBearer"] is not true)
            http.User = Principal("Demo supervisor (not signed in, unverified)", "supervisor", http.Items["residentTenant"] as string ?? "default", "demo-compat");
        var tenantId = http.User.Identity?.IsAuthenticated is true ? http.User.FindFirstValue("civicops:tenant") : http.Items["residentTenant"] as string;
        if (string.IsNullOrWhiteSpace(tenantId) || await http.RequestServices.GetRequiredService<TenantStore>().GetAsync(tenantId, http.RequestAborted) is null)
        { http.Response.StatusCode = 403; return; }
        if (http.Items["explicitTenant"] is true && tenantId != http.Items["residentTenant"] as string)
        { http.Response.StatusCode = 403; return; }
        using (TenantContext.Use(tenantId))
        using (WorkOrderCatalog.Use(await http.RequestServices.GetRequiredService<CatalogueAdminStore>().GetAsync(http.RequestAborted)))
            await next(http);
    });

    public static bool CrewOnly(ClaimsPrincipal principal) => principal.IsInRole("crew")
        && !ElevatedCrewReadRoles.Any(principal.IsInRole);
    public static bool CrewWritesRestricted(ClaimsPrincipal principal) => principal.IsInRole("crew")
        && !ElevatedCrewWriteRoles.Any(principal.IsInRole);
    public static bool AssignedTo(ClaimsPrincipal principal, Incident incident, bool writing = false) => !(writing ? CrewWritesRestricted(principal) : CrewOnly(principal))
        || incident.AssignedCrewId is not null && incident.AssignedCrewId == principal.FindFirstValue("civicops:crew");

    public static IApplicationBuilder UseCivicOpsResourceGuards(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (http.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE"
            && http.User.Identity?.AuthenticationType == "civicops-session")
        {
            try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException)
            {
                await Results.Problem("Refresh the session security token before submitting.", statusCode: 400,
                    title: "Request verification failed", extensions: new Dictionary<string, object?>
                    { ["error"] = "Refresh the session security token before submitting." }).ExecuteAsync(http);
                return;
            }
        }
        if (http.Request.RouteValues["id"] is string id
            && (http.Request.Path.StartsWithSegments("/api/incidents") || http.Request.Path.StartsWithSegments("/api/dispatch/incidents")))
        {
            var store = http.RequestServices.GetRequiredService<IncidentStore>();
            var incident = await store.GetAsync(id, http.RequestAborted);
            if (incident is not null && !AssignedTo(http.User, incident, http.Request.Method != "GET")) { http.Response.StatusCode = 404; return; }
            var path = http.Request.Path.Value ?? "";
            if (incident is not null && (path.EndsWith("/approve", StringComparison.Ordinal) || path.EndsWith("/assign", StringComparison.Ordinal))
                && !path.Contains("/comms/", StringComparison.Ordinal)
                && (incident.Priority == "Urgent" || http.RequestServices.GetRequiredService<WorkOrderCatalog>().Find(incident.WorkOrderTypeCode)?.HazardClass is "high" or "critical")
                && !http.User.IsInRole("supervisor") && !http.User.IsInRole("admin"))
            { http.Response.StatusCode = 403; await http.Response.WriteAsJsonAsync(new { error = "High-hazard approval and assignment require a supervisor." }); return; }
        }
        await next(http);
    });
    private static ClaimsPrincipal Principal(string name, string role, string tenant, string scheme) => new(new ClaimsIdentity(
        [new(ClaimTypes.Name, name), new(ClaimTypes.NameIdentifier, scheme + ":" + name), new(ClaimTypes.Role, role), new("civicops:tenant", tenant)], scheme));
}
