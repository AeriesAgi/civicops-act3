using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace CivicOps.Services;

/// <summary>The only role/permission matrix. Every mapped route must be listed explicitly.</summary>
public static class PermissionMatrix
{
    public static readonly string[] Roles = ["resident", "agent", "dispatcher", "supervisor", "crew", "auditor", "admin", "training-client"];
    public static readonly IReadOnlyDictionary<string, string[]> Permissions = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["public"] = ["resident", "agent", "dispatcher", "supervisor", "crew", "auditor", "admin"],
        ["identity-public"] = Roles,
        ["authenticated"] = Roles,
        ["resident-read"] = ["resident", "agent", "dispatcher", "supervisor", "crew", "auditor", "admin"],
        ["resident-write"] = ["resident", "agent", "dispatcher", "supervisor", "admin"],
        ["operations-read"] = ["dispatcher", "supervisor", "auditor", "admin"],
        ["incident-read"] = ["dispatcher", "supervisor", "crew", "auditor", "admin", "agent"],
        ["workforce-read"] = ["dispatcher", "supervisor", "crew", "auditor", "admin"],
        ["staff-identity"] = ["dispatcher", "supervisor", "crew", "auditor", "admin"],
        ["dispatch"] = ["dispatcher", "supervisor", "admin"],
        ["work-status"] = ["dispatcher", "supervisor", "crew", "admin"],
        ["supervise"] = ["supervisor", "admin"],
        ["audit"] = ["supervisor", "auditor", "admin"],
        ["admin"] = ["admin"],
        ["tools"] = ["agent"],
        ["zendesk-webhook"] = ["agent"],
        ["elevenlabs-webhook"] = ["agent"],
        ["channel-webhook"] = ["agent"],
        // SOS ingress is still HMAC-authenticated by the adapter; this policy lets a
        // configured aggregator (or the local dispatch token in smoke) reach that guard.
        ["sos-ingress"] = ["agent", "dispatcher", "supervisor", "admin"],
        ["training-read"] = ["training-client"],
        ["training-write"] = ["training-client"],
        ["integration-retry"] = ["supervisor", "admin", "agent"]
        , ["crew-self"] = ["crew"]
        // SOS trigger/location writes are available to residents and authenticated staff;
        // control-room decisions remain under the dispatch policy below.
        , ["sos-trigger"] = ["resident", "dispatcher", "supervisor", "crew", "admin"]
    };
    public static readonly IReadOnlyDictionary<string, string> Endpoints = BuildEndpoints();
    private static Dictionary<string, string> BuildEndpoints()
    {
        var endpoints = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string policy, string method, params string[] paths)
        { foreach (var path in paths) endpoints.Add(method + " " + path, policy); }
        Add("public", "GET", "/healthz", "/health/live", "/health/ready", "/openapi.json", "/api/email/status", "/api/ai/triage-contract", "/api/ai/status", "/api/spend-caps",
            "/api/channels/status", "/api/channels/elevenlabs/status", "/api/channels/zendesk/status", "/open311/v2/services");
        Add("identity-public", "GET", "/auth/status", "/auth/login");
        Add("identity-public", "POST", "/auth/demo-login", "/auth/bootstrap-login");
        Add("authenticated", "POST", "/auth/logout");
        Add("resident-read", "GET", "/api/incidents/{id}", "/api/channels/elevenlabs/session");
        Add("resident-write", "POST", "/api/incidents", "/open311/v2/requests", "/api/channels/elevenlabs/simulated-chat");
        Add("staff-identity", "GET", "/api/dispatcher");
        Add("workforce-read", "GET", "/api/crews", "/api/work-order-types");
        Add("incident-read", "GET", "/api/incidents", "/api/dispatch/incidents/{id}");
        Add("operations-read", "GET",
            "/api/incidents/{id}/crew-eligibility", "/api/metrics", "/api/integrations/readiness", "/api/integrations/outbox", "/api/channels/elevenlabs/alerts", "/api/stream");
        Add("operations-read", "GET", "/api/v1/map/config", "/api/v1/map/layers", "/api/v1/map/wards", "/api/v1/map/heatmap", "/api/v1/map/stream", "/api/v1/map/crews/{id}", "/api/v1/map/cases/{id}/nearest-crews");
        Add("dispatch", "POST", "/api/v1/map/demo/seed", "/api/v1/map/cases/{id}/dispatch");
        Add("supervise", "POST", "/api/v1/map/hazards", "/api/v1/map/cases/{id}/ward-override");
        Add("admin", "PUT", "/api/v1/map/geographies");
        Add("dispatch", "POST", "/api/incidents/{id}/approve", "/api/incidents/{id}/assign", "/api/incidents/{id}/comms/{draftId}/approve", "/api/incidents/{id}/callback", "/api/demo/seed");
        Add("work-status", "PATCH", "/api/incidents/{id}/status");
        Add("supervise", "POST", "/api/incidents/{id}/work-order-type");
        Add("audit", "GET", "/api/evidence/export", "/api/audit", "/api/audit/verify", "/api/training/audit", "/api/training/audit/verify");
        Add("audit", "GET", "/api/audit/export");
        Add("audit", "POST", "/api/audit/export/local");
        Add("admin", "POST", "/api/retention/purge", "/api/admin/tenants", "/api/admin/users", "/api/admin/service-accounts", "/api/admin/service-accounts/{id}/rotate", "/api/admin/service-accounts/{id}/revoke", "/api/admin/crews", "/api/admin/workers");
        Add("public", "GET", "/api/privacy/notice");
        Add("admin", "POST", "/api/admin/privacy/requests", "/api/admin/privacy/requests/{id}/verify", "/api/admin/privacy/requests/{id}/fulfil");
        Add("admin", "GET", "/api/admin/privacy/requests");
        Add("admin", "GET", "/api/admin/overview", "/api/admin/tenants", "/api/admin/users", "/api/admin/service-accounts", "/api/admin/catalogue", "/api/admin/crews", "/api/admin/workers");
        Add("admin", "PUT", "/api/admin/tenants/{id}", "/api/admin/users/{id}", "/api/admin/catalogue", "/api/admin/crews/{id}", "/api/admin/workers/{id}");
        Add("admin", "POST", "/api/admin/users/{id}/revoke", "/api/admin/users/{id}/rotate");
        Add("tools", "POST", "/api/channels/elevenlabs/tools/{tool}");
        Add("zendesk-webhook", "POST", "/api/channels/zendesk/webhook");
        Add("elevenlabs-webhook", "POST", "/api/channels/elevenlabs/post-call");
        Add("training-read", "GET", "/api/v1/training/work-order-types", "/api/v1/training/skills", "/api/v1/training/certification-types", "/api/v1/training/work-order-types/{code}/scenarios-hint", "/api/v1/training/workers/{id}/qualifications", "/api/v1/training/stream");
        Add("training-write", "POST", "/api/v1/training/completions", "/api/v1/training/certifications");
        Add("integration-retry", "POST", "/api/integrations/outbox/{id}/retry");
        Add("public", "GET", "/api/v1/crew/auth/config");
        Add("identity-public", "POST", "/api/v1/crew/auth/challenge", "/api/v1/crew/auth/oidc", "/api/v1/crew/pairing/exchange");
        Add("supervise", "POST", "/api/v1/crew/pairing", "/api/v1/crew/devices/{id}/revoke", "/api/v1/crew/retention/purge");
        Add("supervise", "GET", "/api/v1/crew/devices", "/api/v1/crew/policy", "/api/v1/crew/jobs/{id}", "/api/v1/crew/jobs/{id}/photos/{photoId}");
        Add("supervise", "PUT", "/api/v1/crew/policy");
        Add("crew-self", "GET", "/api/v1/crew/me", "/api/v1/crew/me/jobs", "/api/v1/crew/me/jobs/{id}", "/api/v1/crew/me/jobs/{id}/photos/{photoId}", "/api/v1/crew/me/locations", "/api/v1/crew/me/locations/export");
        Add("crew-self", "POST", "/api/v1/crew/shifts/start", "/api/v1/crew/shifts/end", "/api/v1/crew/consent", "/api/v1/crew/locations", "/api/v1/crew/me/jobs/{id}/status", "/api/v1/crew/me/jobs/{id}/notes", "/api/v1/crew/me/jobs/{id}/photos", "/api/v1/crew/sos");
        Add("crew-self", "DELETE", "/api/v1/crew/consent");
        Add("operations-read", "GET", "/api/v1/channels/readiness", "/api/v1/channels/metrics", "/api/v1/channels/conversations", "/api/v1/channels/conversations/{id}", "/api/v1/channels/templates");
        Add("dispatch", "POST", "/api/v1/channels/conversations/{id}/reply", "/api/v1/channels/simulated/{provider}", "/api/v1/channels/demo/seed");
        Add("supervise", "POST", "/api/v1/channels/retention/purge");
        Add("admin", "PUT", "/api/admin/channel-templates/{key}/{language}");
        Add("admin", "POST", "/api/admin/channel-templates/preview");
        Add("channel-webhook", "POST", "/api/v1/channels/webhooks/{provider}");
        Add("channel-webhook", "GET", "/api/v1/channels/webhooks/whatsapp");
        Add("sos-trigger", "POST", "/api/v1/sos", "/api/v1/sos/{id}/location", "/api/v1/sos/{id}/cancel");
        Add("dispatch", "POST", "/api/v1/sos/{id}/acknowledge", "/api/v1/sos/{id}/assess", "/api/v1/sos/{id}/dispatch",
            "/api/v1/sos/{id}/resolve", "/api/v1/sos/{id}/false-alarm");
        Add("operations-read", "GET", "/api/v1/sos/queue", "/api/v1/sos/{id}", "/api/v1/sos/stream");
        Add("sos-ingress", "POST", "/api/v1/sos/inbound", "/api/v1/sos/ussd");
        Add("operations-read", "GET", "/api/v1/agencies", "/api/v1/agency-incidents", "/api/v1/agency-incidents/{id}", "/api/v1/detections");
        Add("workforce-read", "GET", "/api/v1/agencies/catalogue");
        Add("supervise", "PUT", "/api/v1/agencies/{id}");
        Add("supervise", "POST", "/api/v1/agency-incidents/{id}/commander", "/api/v1/agency-incidents/{id}/mutual-aid/{requestId}/decision");
        Add("dispatch", "POST", "/api/v1/agency-incidents", "/api/v1/agency-incidents/{id}/status", "/api/v1/agency-incidents/{id}/mutual-aid",
            "/api/v1/detections", "/api/v1/detections/simulate", "/api/v1/detections/{id}/confirm", "/api/v1/detections/{id}/dismiss");
        return endpoints;
    }
    public static string PolicyFor(string method, string route) => Endpoints.TryGetValue(method + " " + route.TrimEnd('/'), out var policy)
        ? policy : throw new InvalidOperationException($"Endpoint {method} {route} has no explicit CivicOps permission policy.");
    public static bool AllowsRole(string policy, string role) => Permissions.TryGetValue(policy, out var roles) && roles.Contains(role, StringComparer.Ordinal);
    public static bool Allows(string policy, ClaimsPrincipal user)
    {
        // A paired device keeps crew:self work and may raise its own SOS. It never gains dispatch or the control-room queue.
        if (user.HasClaim(claim => claim.Type == "civicops:crew-device"))
            return (policy is "crew-self" or "sos-trigger") && user.IsInRole("crew") && user.HasClaim("civicops:scope", "crew:self");
        var authenticated = user.Identity?.IsAuthenticated is true;
        if (policy == "authenticated") return authenticated;
        // Signed bootstrap webhooks are verified against the exact bounded body in their handlers.
        if (policy is "zendesk-webhook" or "elevenlabs-webhook" or "channel-webhook" or "sos-ingress"
            && (!authenticated || user.Identity?.AuthenticationType == "demo-compat")) return true;
        var roles = user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray();
        if (roles.Length == 0 && !authenticated) roles = ["resident"];
        if (!roles.Any(role => AllowsRole(policy, role))) return false;
        if (user.HasClaim(claim => claim.Type == "civicops:service-account"))
        {
            var scope = policy switch
            {
                "tools" => "tools:invoke", "zendesk-webhook" => "zendesk:webhook", "elevenlabs-webhook" => "tools:invoke",
                "training-read" => "training:read", "training-write" => "training:write", "audit" => "audit:read",
                "resident-read" or "incident-read" => "incidents:read", "resident-write" => "incidents:write", "integration-retry" => "integrations:write",
                _ => null
            };
            return scope is not null && user.HasClaim("civicops:scope", scope);
        }
        return true;
    }
    public static IServiceCollection AddCivicOpsAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build();
            foreach (var policy in Permissions.Keys)
                options.AddPolicy(policy, builder => builder.RequireAssertion(context => Allows(policy, context.User)));
        });
        return services;
    }
    public static void ApplyPolicies(this RouteGroupBuilder routes) => ((IEndpointConventionBuilder)routes).Finally(builder =>
    {
        if (builder is not RouteEndpointBuilder route || route.RoutePattern.RawText is not string path) throw new InvalidOperationException("Unexpected endpoint.");
        var methods = builder.Metadata.OfType<HttpMethodMetadata>().LastOrDefault()?.HttpMethods ?? throw new InvalidOperationException("HTTP methods are required.");
        var policies = methods.Select(method => PolicyFor(method, path)).Distinct().ToArray();
        if (policies.Length != 1 || builder.Metadata.OfType<IAllowAnonymous>().Any()) throw new InvalidOperationException("Ambiguous endpoint authorization.");
        foreach (var existing in builder.Metadata.OfType<IAuthorizeData>().ToArray()) builder.Metadata.Remove(existing);
        builder.Metadata.Add(new AuthorizeAttribute(policies[0]));
    });
}
