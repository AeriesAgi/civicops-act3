using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace CivicOps.Services;

public static class OpenApiDocument
{
    private static readonly string[] Tags = ["Residents", "Staff", "Administration", "Training", "Channels", "Identity", "Operations", "Maps", "Crew", "Documentation"];
    public static JsonObject Create(EndpointDataSource source)
    {
        var schemas = new OpenApiSchemas(); LoadPortableSchemas(schemas);
        var contracts = new OpenApiContracts(schemas);
        schemas.Add("ProblemDetails", new JsonObject { ["type"] = "object", ["description"] = "RFC 9457 problem details. Middleware authentication/authorization may return an empty body; legacy resource guards may return JSON error instead. Extensions vary by error and can include the current safe incident projection.",
            ["properties"] = new JsonObject { ["type"] = OpenApiSchemas.String(), ["title"] = OpenApiSchemas.String(), ["status"] = OpenApiSchemas.Integer(),
                ["detail"] = OpenApiSchemas.String(), ["instance"] = OpenApiSchemas.String(), ["error"] = OpenApiSchemas.String(), ["traceId"] = OpenApiSchemas.String() }, ["additionalProperties"] = true });
        schemas.Add("LegacyError", OpenApiSchemas.Object(("error", OpenApiSchemas.String())));
        var paths = new JsonObject();
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>().OrderBy(endpoint => endpoint.RoutePattern.RawText, StringComparer.Ordinal))
        {
            var route = endpoint.RoutePattern.RawText?.TrimEnd('/');
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (route is null || methods is null) continue;
            paths[route] ??= new JsonObject();
            foreach (var method in methods.Order(StringComparer.Ordinal))
            {
                var policy = endpoint.Metadata.GetMetadata<IAuthorizeData>()?.Policy ?? throw new InvalidOperationException("OpenAPI endpoint lacks an explicit policy.");
                var tag = Tag(route);
                var parameters = new JsonArray();
                foreach (var parameter in endpoint.RoutePattern.Parameters)
                {
                    var schema = OpenApiSchemas.String();
                    if (parameter.Name == "tool") schema["enum"] = new JsonArray("report_incident", "lookup_case_status", "add_case_information", "request_human_handoff", "list_service_categories");
                    parameters.Add(Parameter(parameter.Name, "path", true, "Tenant-bound record identifier, catalogue code or supported resident tool name.", schema, parameter.Name == "tool" ? "report_incident" : "synthetic-id"));
                }
                AddParameters(parameters, method, route);
                var success = contracts.Response(method, route);
                var media = route.EndsWith("/stream", StringComparison.Ordinal) ? "text/event-stream" : route == "/api/audit/export" ? "application/zip"
                    : route.StartsWith("/api/v1/crew/", StringComparison.Ordinal) && route.EndsWith("/photos/{photoId}", StringComparison.Ordinal) ? "application/octet-stream"
                    : route is "/api/v1/map/layers" or "/api/v1/map/wards" or "/api/v1/map/heatmap" ? "application/geo+json" : "application/json";
                var responses = new JsonObject { ["200"] = Success(schemas, success, media, "Successful authoritative response. Conditional fields depend on the selected operation/mode.") };
                if (method == "POST" && route is "/api/incidents" or "/api/v1/training/completions" or "/api/v1/training/certifications" or "/api/v1/map/hazards")
                    responses["201"] = Success(schemas, (JsonObject)success.DeepClone(), media, "Created durable record; repeating the same idempotency key returns 200 with replayed=true.");
                if (route == "/auth/login") { responses.Remove("200"); responses["302"] = Redirect("Redirect to the configured trusted OIDC issuer for authorization code + PKCE."); }
                if (route == "/auth/logout") responses["302"] = Redirect("OIDC sessions may redirect through the configured issuer's logout endpoint.");
                if (method == "GET" && route is "/api/v1/training/work-order-types" or "/api/v1/training/skills" or "/api/v1/training/certification-types")
                {
                    parameters.Add(Parameter("If-None-Match", "header", false, "Return 304 when the catalogue ETag matches; '*' is supported.", OpenApiSchemas.String(), "\"synthetic-etag\""));
                    responses["304"] = new JsonObject { ["description"] = "Catalogue unchanged; no response body.", ["headers"] = CatalogueHeaders() };
                    responses["200"]!["headers"] = CatalogueHeaders();
                }
                if (method == "GET" && route is "/api/v1/map/layers" or "/api/v1/map/wards" or "/api/v1/map/heatmap")
                {
                    parameters.Add(Parameter("If-None-Match", "header", false, "Tenant-bound content ETag. Comma-separated tags and '*' are supported; successful writes/changed features invalidate it.", OpenApiSchemas.String(), "\"synthetic-map-etag\""));
                    var headers = new JsonObject { ["ETag"] = new JsonObject { ["description"] = "Hash of tenant, query and projected feature content; generatedAt is excluded so unchanged state can return 304.", ["schema"] = OpenApiSchemas.String() },
                        ["Cache-Control"] = new JsonObject { ["description"] = "no-store; operational responses are not persisted by browser/service worker caches.", ["schema"] = OpenApiSchemas.String() } };
                    responses["304"] = new JsonObject { ["description"] = "Authenticated map projection unchanged; no body.", ["headers"] = headers.DeepClone() };
                    responses["200"]!["headers"] = headers;
                }
                foreach (var (status, description) in new (string, string)[] { ("400", "Invalid bounded request or missing/invalid browser CSRF token."),
                    ("401", "Credential missing, expired or unrecognized; signed webhook verification failed."), ("403", "Role, credential scope or municipality binding forbids this route."),
                    ("404", "Record/route unavailable or intentionally hidden by municipality/crew isolation; simulation route disabled in live mode."),
                    ("409", "Optimistic version, lifecycle, catalogue or idempotency conflict; refresh authoritative state."),
                    ("413", "Body exceeds this endpoint's configured request bound."), ("415", "Unsupported request content type."),
                    ("429", "Tenant/principal/IP quota exhausted; wait before retrying."), ("500", "Unexpected internal failure; no sensitive diagnostic values returned."),
                    ("502", "Configured provider temporarily failed; callback outcome can require human reconciliation."), ("503", "Required configuration or durable storage is unavailable. Demo remains explicitly simulated.") })
                {
                    responses[status] = new JsonObject { ["description"] = description + " Authentication/resource middleware may return an empty body.", ["content"] = new JsonObject
                    {
                        ["application/problem+json"] = new JsonObject { ["schema"] = OpenApiSchemas.Ref("ProblemDetails"), ["example"] = new JsonObject { ["type"] = "about:blank", ["title"] = description.Split(';')[0], ["status"] = int.Parse(status, System.Globalization.CultureInfo.InvariantCulture), ["detail"] = "Synthetic bounded error example." } },
                        ["application/json"] = new JsonObject { ["schema"] = OpenApiSchemas.Ref("LegacyError"), ["example"] = new JsonObject { ["error"] = "Synthetic bounded error example." } }
                    } };
                }
                var operation = new JsonObject
                {
                    ["operationId"] = method.ToLowerInvariant() + "_" + route.Replace('/', '_').Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal).Replace('-', '_'),
                    ["summary"] = method + " " + route, ["description"] = Description(route, policy), ["tags"] = new JsonArray(tag),
                    ["parameters"] = parameters, ["responses"] = responses, ["security"] = Security(endpoint, policy),
                    ["x-civicops-policy"] = policy, ["x-civicops-roles"] = new JsonArray(PermissionMatrix.Permissions[policy].Select(role => (JsonNode)JsonValue.Create(role)!).ToArray()),
                    ["x-civicops-tenant"] = "Authenticated civicops:tenant claim; resident host or /m/{slug} prefix. Body tenant fields never select a municipality.",
                    ["x-civicops-errors"] = "Shared middleware errors listed are possible cross-cutting responses, not a promise that each business handler emits each status."
                };
                var request = contracts.Request(method, route) ?? (endpoint.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType is Type requestType
                    && method is "POST" or "PUT" or "PATCH" ? schemas.Type(requestType) : null);
                if (request is not null) operation["requestBody"] = new JsonObject { ["required"] = true, ["description"] = "Bounded JSON request. Examples are synthetic and contain no credentials. Fields are validated by the handler; production resident writes also require current purpose-notice consent.",
                    ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = request, ["example"] = schemas.Example(request) } } };
                if (route == "/api/v1/channels/webhooks/{provider}")
                {
                    operation["requestBody"]!["description"] = "Exact native JSON or Twilio URL-encoded form body; do not reserialize before signature verification. Trusted relay retains original provider signature.";
                    operation["requestBody"]!["content"]!["application/x-www-form-urlencoded"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } } };
                    responses["200"]!["content"]!["application/xml"] = new JsonObject { ["schema"] = OpenApiSchemas.String(), ["example"] = "<Response><Say>Synthetic IVR menu; no dispatch action.</Say></Response>" };
                }
                if (route == "/api/v1/channels/webhooks/whatsapp")
                {
                    responses["200"]!["content"] = new JsonObject { ["text/plain"] = new JsonObject { ["schema"] = OpenApiSchemas.String(), ["example"] = "12345" } };
                }
                ((JsonObject)paths[route]!)[method.ToLowerInvariant()] = operation;
            }
        }
        return new JsonObject
        {
            ["openapi"] = "3.1.0", ["jsonSchemaDialect"] = "https://json-schema.org/draft/2020-12/schema",
            ["info"] = new JsonObject { ["title"] = "CivicOps municipal API", ["version"] = "11.0.0",
                ["description"] = "Implemented CivicOps endpoint contracts, explicit role policies and tenant-bound projections. File storage is single-instance; PostgreSQL is shared transactional storage. Explicit demo uses synthetic data and simulated integrations. Provider/AMD/municipal production validation is owner-pending. OIDC establishes an opaque cookie session; issuer access/ID tokens are not CivicOps service bearer credentials.",
                ["license"] = new JsonObject { ["name"] = "MIT", ["identifier"] = "MIT" }, ["contact"] = new JsonObject { ["name"] = "Deployment owner; production support contact pending" } },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "/", ["description"] = "Current deployment origin; resident municipality paths may use /m/{slug} as a prefix." }),
            ["tags"] = new JsonArray(Tags.Select(tag => (JsonNode)new JsonObject { ["name"] = tag, ["description"] = tag + " endpoints; permission and municipality isolation are enforced on the server." }).ToArray()),
            ["paths"] = paths, ["components"] = new JsonObject { ["schemas"] = schemas.Definitions, ["securitySchemes"] = SecuritySchemes() }
        };
    }
    private static JsonObject Success(OpenApiSchemas schemas, JsonObject schema, string media, string description) => new()
    {
        ["description"] = description, ["content"] = new JsonObject { [media] = new JsonObject { ["schema"] = schema,
            ["example"] = media == "text/event-stream" ? JsonValue.Create("event: ready\ndata: {}\n\n") : media == "application/zip" ? JsonValue.Create("Synthetic binary ZIP download") : schemas.Example(schema) } }
    };
    private static JsonObject Redirect(string description) => new() { ["description"] = description, ["headers"] = new JsonObject { ["Location"] = new JsonObject { ["description"] = "Trusted issuer redirect or local application return path.", ["schema"] = OpenApiSchemas.String() } } };
    private static JsonObject CatalogueHeaders() => new() { ["ETag"] = new JsonObject { ["description"] = "Current catalogue content ETag.", ["schema"] = OpenApiSchemas.String() },
        ["X-CivicOps-Catalog-Version"] = new JsonObject { ["description"] = "Catalogue semantic version.", ["schema"] = OpenApiSchemas.String() } };
    private static JsonObject Parameter(string name, string location, bool required, string description, JsonObject schema, object example) => new()
    { ["name"] = name, ["in"] = location, ["required"] = required, ["description"] = description, ["schema"] = schema, ["example"] = JsonSerializer.SerializeToNode(example) };
    private static void AddParameters(JsonArray parameters, string method, string route)
    {
        if (method is "POST" or "PUT" or "PATCH" or "DELETE")
            parameters.Add(Parameter("X-CivicOps-CSRF", "header", route is "/auth/demo-login" or "/auth/bootstrap-login", "Required for browser-cookie mutations and both local login forms. Fetch a fresh token/cookie from /auth/status. Bearer and signed webhook requests are exempt.", OpenApiSchemas.String(), ""));
        if (method == "POST" && (route is "/api/incidents" or "/open311/v2/requests" || route.StartsWith("/api/v1/training/", StringComparison.Ordinal) || route.EndsWith("/callback", StringComparison.Ordinal)))
        {
            var required = route.StartsWith("/api/v1/training/", StringComparison.Ordinal) || route.EndsWith("/callback", StringComparison.Ordinal);
            parameters.Add(Parameter("Idempotency-Key", "header", required, "Unique synthetic/client-generated request key; reuse only with the identical bounded payload.", new JsonObject { ["type"] = "string", ["minLength"] = required ? 1 : 0, ["maxLength"] = route.StartsWith("/api/v1/training/", StringComparison.Ordinal) ? 128 : 200 }, "synthetic-request-001"));
        }
        if (route == "/auth/login") parameters.Add(Parameter("returnUrl", "query", false, "Bounded local application path used after successful sign-in; external URLs are rejected.", new JsonObject { ["type"] = "string", ["maxLength"] = 512 }, "/"));
        if (route == "/api/privacy/notice") parameters.Add(Parameter("language", "query", false, "Purpose notice language. Afrikaans/isiZulu drafts require native-speaker review before production.", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("en", "af", "zu") }, "en"));
        if (route == "/api/v1/crew/me/locations")
        {
            parameters.Add(Parameter("page", "query", false, "One-based own-history page, latest points first; export endpoint returns the entire retained history.", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000 }, 1));
            parameters.Add(Parameter("limit", "query", false, "Maximum own location points per page; default1000.", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 1000 }, 1000));
        }
        if (route.EndsWith("/zendesk/webhook", StringComparison.Ordinal)) parameters.Add(Parameter("X-Zendesk-Webhook-Signature-Timestamp", "header", true, "ISO-8601 timestamp within five minutes; signature covers timestamp bytes followed by the exact raw request body.", OpenApiSchemas.String("date-time"), "2026-10-08T12:00:00Z"));
        if (route.Contains("/elevenlabs/tools/", StringComparison.Ordinal)) parameters.Add(Parameter("X-CivicOps-Conversation-Id", "header", false, "When present, must match the conversation identifier in the tool body; bounded to 128 characters.", new JsonObject { ["type"] = "string", ["maxLength"] = 128 }, "synthetic-conversation"));
        if (route == "/api/v1/map/layers")
        {
            parameters.Add(Parameter("bbox", "query", false, "west,south,east,north; finite, ordered, antimeridian-free bounds inside longitude 16–33 and latitude -35–-22. Defaults to that regional envelope.", new JsonObject { ["type"] = "string", ["maxLength"] = 160 }, "30.865,-30.035,31.085,-29.78"));
            parameters.Add(Parameter("layers", "query", false, "Unique comma-separated layer names; default all. Each parameter may occur once.", new JsonObject { ["type"] = "string", ["maxLength"] = 80 }, "municipalities,wards,cases,crews,hazards,assets"));
            parameters.Add(Parameter("since", "query", false, "ISO-8601 timestamp filtering case UpdatedAt, crew LastSeenAt and hazard OccurredAt. Full refresh remains necessary for deletions and static boundaries/assets; SSE carries refresh metadata only.", OpenApiSchemas.String("date-time"), "2026-10-08T12:00:00Z"));
            parameters.Add(Parameter("page", "query", false, "One-based stable ID-ordered page. Refresh from page one after an SSE change; pages are not a historical snapshot.", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10000 }, 1));
            parameters.Add(Parameter("limit", "query", false, "Maximum features per page, default 500.", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 500 }, 500));
        }
        if (route is "/api/v1/channels/webhooks/{provider}" or "/api/v1/channels/simulated/{provider}")
        {
            parameters.Add(Parameter("X-CivicOps-Timestamp", "header", true, "Signed Unix seconds; age at most300seconds and future skew at most30seconds.", OpenApiSchemas.String(), "1791504000"));
            parameters.Add(Parameter("X-CivicOps-Nonce", "header", true, "Fresh16–128character nonce. Persisted per municipality/provider; reused nonce returns401.", OpenApiSchemas.String(), "synthetic-nonce-0001"));
            parameters.Add(Parameter("X-CivicOps-Signature", "header", true, "sha256=hex HMAC of timestamp.nonce.provider.trusted-public-url.exact-body. Simulator binds municipality ID; native endpoints require provider authentication too.", OpenApiSchemas.String(), ""));
        }
        if (route == "/api/v1/channels/webhooks/whatsapp")
        {
            parameters.Add(Parameter("hub.mode", "query", true, "Meta verify mode subscribe.", OpenApiSchemas.String(), "subscribe"));
            parameters.Add(Parameter("hub.verify_token", "query", true, "Owner-configured verification token, constant-time checked; never returned.", OpenApiSchemas.String(), ""));
            parameters.Add(Parameter("hub.challenge", "query", true, "Bounded numeric challenge returned verbatim as text/plain when authenticated.", OpenApiSchemas.String(), "12345"));
        }
        if (route == "/api/v1/map/heatmap")
        {
            parameters.Add(Parameter("metric", "query", false, "Operational case/hazard events only; no worker/person location metric. Default cases.", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("cases", "hazards") }, "cases"));
            parameters.Add(Parameter("window", "query", false, "Rolling incident aggregation window; default 24h.", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("24h", "7d") }, "24h"));
        }
    }
    private static JsonArray Security(RouteEndpoint endpoint, string policy)
    {
        var security = new JsonArray();
        void Scheme(string name) => security.Add(new JsonObject { [name] = new JsonArray() });
        if (policy is "public" or "identity-public") return security;
        if (policy == "crew-self") { Scheme("crewDeviceBearer"); Scheme("staffSession"); return security; }
        if (policy == "channel-webhook")
        {
            if (endpoint.RoutePattern.RawText == "/api/v1/channels/webhooks/whatsapp") return security;
            Scheme("channelIngressSignature"); return security;
        }
        if (policy == "sos-ingress") { Scheme("sosIngressSignature"); Scheme("staffSession"); return security; }
        if (policy is "resident-read" or "resident-write") security.Add(new JsonObject());
        if (policy is "zendesk-webhook" or "elevenlabs-webhook")
        { Scheme(policy == "zendesk-webhook" ? "zendeskWebhookSignature" : "elevenLabsWebhookSignature"); return security; }
        if (endpoint.Metadata.GetMetadata<TrainingRequired>() is not null) { Scheme("trainingBearer"); Scheme("serviceAccountBearer"); return security; }
        if (endpoint.Metadata.GetMetadata<ElevenLabsToolRequired>() is not null) { Scheme("elevenLabsToolSecret"); Scheme("serviceAccountBearer"); return security; }
        if (endpoint.Metadata.GetMetadata<DispatchRequired>() is not null) Scheme("dispatcherBearer");
        Scheme("staffSession");
        if (policy is "resident-read" or "resident-write" or "incident-read" or "audit" or "integration-retry") Scheme("serviceAccountBearer");
        return security;
    }
    private static string Description(string route, string policy) => "Permission: " + policy + ". " + (route == "/api/v1/sos/stream"
        ? "SSE event name is sos. Each data line is one current tenant-scoped alert for the control room. Re-fetch the queue after reconnect; no durable replay. "
        : route.EndsWith("/stream", StringComparison.Ordinal)
        ? "SSE sends event names with empty JSON data, starting with ready. Re-fetch authoritative tenant-scoped state after events/reconnect; no durable event replay or event IDs are provided. " : "")
        + (route.Contains("/elevenlabs/", StringComparison.Ordinal) ? "Missing provider configuration selects a labelled simulated or disabled mode; live provider behavior remains owner validation. " : "")
        + (route == "/api/incidents/{id}" ? "Privacy-minimal resident projection: no descriptions, contacts, transcript or internal actors. Resident updates follow the stored report language. " : "")
        + (route.StartsWith("/api/v1/map/", StringComparison.Ordinal) ? "Tenant-authorized municipal map. Synthetic demo geometry/positions are labelled. Real latest crew positions require active shift/consent and supervisor access; raw histories are absent. Camera imagery, biometric identification and person/neighbourhood risk scoring are prohibited. Suggestions never approve/dispatch; named humans provide a reason for safety and assignment writes. " : "")
        + (route.StartsWith("/api/v1/crew/", StringComparison.Ordinal) ? "Municipality-local crew app. Crew devices have crew:self access plus sos-trigger for their own alert; they cannot dispatch or read the control-room queue. Expiry/revocation is checked per request. Location requires a current device-owned bounded shift, current explicit revocable purpose consent and visible indicator; off-shift/revoked buffered uploads are rejected and audited without coordinates. Own raw histories are retained1–30days and automatically purged. Crew completion records a worker report; a dispatcher resolves the case through the existing human-review workflow. Photos are always stripped of metadata server-side. POST /sos records a real control-room alert and accepts no coordinates. " : "")
        + (route == "/api/v1/map/stream" ? "Bounded 16-event subscriber channel coalesces refreshes, heartbeat every 15 seconds, five-minute reconnect to recheck authorization. No coordinate payload, durable replay or distributed fan-out. " : "")
        + "Cookie writes require CSRF. Municipality binding and service-account scope are enforced server-side. EXPLICIT DEMO compatibility is enabled only by CIVICOPS_DEMO_MODE=true.";
    private static string Tag(string route) => route.StartsWith("/auth/", StringComparison.Ordinal) ? "Identity" : route.StartsWith("/api/admin/", StringComparison.Ordinal) ? "Administration"
        : route.StartsWith("/api/v1/map/", StringComparison.Ordinal) ? "Maps"
        : route.StartsWith("/api/v1/crew/", StringComparison.Ordinal) ? "Crew"
        : route.StartsWith("/api/v1/training/", StringComparison.Ordinal) ? "Training" : route.Contains("/channels/", StringComparison.Ordinal) ? "Channels"
        : route == "/openapi.json" ? "Documentation" : route.StartsWith("/health", StringComparison.Ordinal) || route.Contains("/audit", StringComparison.Ordinal) || route.Contains("/integrations/", StringComparison.Ordinal) || route.Contains("/ai/", StringComparison.Ordinal) || route.Contains("/metrics", StringComparison.Ordinal) ? "Operations"
        : route.StartsWith("/open311/", StringComparison.Ordinal) || route == "/api/incidents/{id}" || route == "/api/incidents" || route == "/api/privacy/notice" ? "Residents" : "Staff";
    private static JsonObject SecuritySchemes() => new()
    {
        ["oidc"] = new JsonObject { ["type"] = "openIdConnect", ["openIdConnectUrl"] = "https://issuer.example.invalid/.well-known/openid-configuration",
            ["description"] = "Owner-specific discovery placeholder. Set CIVICOPS_OIDC_AUTHORITY to the trusted HTTPS issuer; start /auth/login for authorization code + PKCE. Successful login establishes staffSession. Issuer bearer tokens are not accepted by this API.", ["x-civicops-authority-setting"] = "CIVICOPS_OIDC_AUTHORITY" },
        ["staffSession"] = new JsonObject { ["type"] = "apiKey", ["name"] = "__Host-CivicOps-Session", ["in"] = "cookie", ["description"] = "Opaque server-side staff session from OIDC or administrator bootstrap login. Secure, HttpOnly, SameSite=Lax. Cookie writes require X-CivicOps-CSRF. Process-local sessions expire on restart." },
        ["serviceAccountBearer"] = Bearer("Municipality-bound administrator-issued opaque service token, stored only as a hash. Permission scopes: tools:invoke, zendesk:webhook, training:read, training:write, incidents:read, incidents:write, audit:read, integrations:write. Expiry/revocation/rotation checked per request; token appears once in create/rotate response."),
        ["crewDeviceBearer"] = Bearer("Supervisor-paired or trusted native-OIDC opaque crew device token, hashed at rest, at most8hours and immediately revocable. Bound to one municipality/worker/crew; only crew:self routes allowed. Never accepts caller-selected worker identity or police/dispatch approval."),
        ["dispatcherBearer"] = Bearer("Legacy default-tenant bootstrap dispatcher token; configured named identity is resolved on the server. Prefer OIDC staffSession for staff access."),
        ["trainingBearer"] = Bearer("Separate CIVICOPS_TRAINING_CLIENTS bootstrap token; cannot access incident/provider-tool routes. Default-tenant only; use scoped municipality-bound service accounts for other tenants."),
        ["elevenLabsToolSecret"] = new JsonObject { ["type"] = "apiKey", ["name"] = "X-CivicOps-Tool-Secret", ["in"] = "header", ["description"] = "Legacy default-tenant resident-tool shared secret. Prefer serviceAccountBearer with tools:invoke." },
        ["zendeskWebhookSignature"] = new JsonObject { ["type"] = "apiKey", ["name"] = "X-Zendesk-Webhook-Signature", ["in"] = "header", ["description"] = "Base64 HMAC-SHA256 over timestamp + exact raw request bytes, with configured ZENDESK_WEBHOOK_SECRET. Timestamp header is mandatory; maximum replay age five minutes. A service bearer does not replace raw-body signature verification." },
        ["elevenLabsWebhookSignature"] = new JsonObject { ["type"] = "apiKey", ["name"] = "ElevenLabs-Signature", ["in"] = "header", ["description"] = "t=unix-seconds,v0=hex HMAC-SHA256 of timestamp + '.' + exact raw request bytes using ELEVENLABS_WEBHOOK_SECRET. Maximum replay age five minutes. Signature verification is mandatory." },
        ["channelIngressSignature"] = new JsonObject { ["type"] = "apiKey", ["name"] = "X-CivicOps-Signature", ["in"] = "header", ["description"] = "Trusted authenticated relay HMAC-SHA256 envelope in addition to provider-native authentication. Timestamp, nonce, provider, configured tenant public URL and exact body are bound; durable replay receipts required. Not a vendor-native header." }
        , ["sosIngressSignature"] = new JsonObject { ["type"] = "apiKey", ["name"] = "X-CivicOps-SOS-Signature", ["in"] = "header", ["description"] = "Simulated SMS/WhatsApp/USSD ingress HMAC-SHA256 over timestamp + exact JSON body, paired with X-CivicOps-SOS-Timestamp. The demo bypasses external delivery; production requires an aggregator contract and replay-bounded timestamp." }
    };
    private static JsonObject Bearer(string description) => new() { ["type"] = "http", ["scheme"] = "bearer", ["bearerFormat"] = "opaque", ["description"] = description };
    private static void LoadPortableSchemas(OpenApiSchemas schemas)
    {
        var assembly = typeof(OpenApiDocument).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("CivicOps.Schemas.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var schema = JsonNode.Parse(stream)!.AsObject(); schema.Remove("$id"); schema.Remove("$schema");
            var name = resource["CivicOps.Schemas.".Length..].Replace(".schema.json", "", StringComparison.Ordinal);
            RewriteReferences(schema, name); schemas.Add(name, schema);
        }
    }
    private static void RewriteReferences(JsonNode node, string schemaName)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var value))
            {
                if (value.Contains(".schema.json", StringComparison.Ordinal)) obj["$ref"] = "#/components/schemas/" + value.Replace(".schema.json#", "", StringComparison.Ordinal).Replace(".schema.json", "", StringComparison.Ordinal);
                else if (value.StartsWith("#/", StringComparison.Ordinal)) obj["$ref"] = "#/components/schemas/" + schemaName + value[1..];
            }
            foreach (var child in obj.Select(pair => pair.Value).OfType<JsonNode>()) RewriteReferences(child, schemaName);
        }
        else if (node is JsonArray array) foreach (var child in array.OfType<JsonNode>()) RewriteReferences(child, schemaName);
    }
}
