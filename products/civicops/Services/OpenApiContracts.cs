using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Models;
using static CivicOps.Services.OpenApiSchemas;

namespace CivicOps.Services;

/// <summary>Explicit projection contracts for handlers returning IResult and for bounded manually parsed JSON.</summary>
internal sealed class OpenApiContracts
{
    private readonly Dictionary<string, JsonObject> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _requests = new(StringComparer.Ordinal);
    internal JsonObject Response(string method, string path) => _responses.TryGetValue(method + " " + path, out var schema)
        ? (JsonObject)schema.DeepClone() : throw new InvalidOperationException($"OpenAPI response contract missing for {method} {path}.");
    internal JsonObject? Request(string method, string path) => _requests.GetValueOrDefault(method + " " + path)?.DeepClone() as JsonObject;
    internal OpenApiContracts(OpenApiSchemas s)
    {
        JsonObject Model<T>() => s.Type(typeof(T));
        JsonObject List<T>() => Array(Model<T>());
        JsonObject Shape(string name, params (string Name, JsonObject Schema)[] fields) => s.Add(name, Object(fields));
        void Get(string path, JsonObject response) => _responses.Add("GET " + path, response);
        void Write(string method, string path, JsonObject response, JsonObject? request = null)
        { _responses.Add(method + " " + path, response); if (request is not null) _requests.Add(method + " " + path, request); }
        var publicCase = s.Projection("PublicIncident", IncidentView.Public(new Incident()));
        var staffCase = s.Projection("DispatcherIncident", IncidentView.Dispatcher(new Incident()));
        var changed = Shape("ChangedIncident", ("incident", staffCase));
        var catalogue = s.Projection("Catalogue", CatalogueAdminStore.View(WorkOrderCatalog.Baseline));
        var trainingCatalogue = Ref("work-order-types-response");
        var authenticated = Shape("AuthenticationResult", ("authenticated", Boolean()));
        var health = Shape("Liveness", ("status", String()), ("service", String()));
        Get("/health/live", health);
        Get("/healthz", Shape("LegacyHealth", ("status", String()), ("service", String()), ("emailMode", String()), ("demoMode", Boolean()), ("dataMode", String())));
        var providerHealth = Model<ProviderHealth>();
        Get("/health/ready", Shape("Readiness", ("status", String()), ("ready", Boolean()),
            ("checks", Object(("storage", String()), ("migrations", String()), ("outbox", String()), ("geo", String()), ("crew", String()), ("channels", String()))),
            ("outbox", Object(("pending", Integer()), ("threshold", Integer()))),
            ("providers", Object(("ai", providerHealth), ("elevenlabs", Model<ProviderHealth>()), ("zendesk", Model<ProviderHealth>())))));
        Get("/openapi.json", Shape("OpenApiResponse", ("openapi", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("3.1.0") }),
            ("jsonSchemaDialect", String("uri")),
            ("info", new JsonObject { ["type"] = "object", ["additionalProperties"] = true }),
            ("servers", Array(Object(("url", String()), ("description", String())))),
            ("tags", Array(Object(("name", String()), ("description", String())))),
            ("paths", new JsonObject { ["type"] = "object", ["additionalProperties"] = true }),
            ("components", new JsonObject { ["type"] = "object", ["additionalProperties"] = true })));
        Get("/api/email/status", Shape("EmailStatus", ("enabled", Boolean()), ("mode", String()), ("smtpConfigured", Boolean()), ("recipientConfigured", Boolean())));
        Get("/api/ai/status", Model<AiStatus>());
        Get("/api/spend-caps", Shape("SpendCapStatus", ("reached", Boolean()), ("llmReached", Boolean()), ("voiceReached", Boolean()), ("label", Nullable(String()))));
        Get("/api/ai/triage-contract", Shape("TriageContract", ("systemPrompt", String()), ("schema", new JsonObject { ["type"] = "object", ["additionalProperties"] = true })));
        Get("/api/privacy/notice", Shape("PrivacyNotice", ("version", String()), ("purpose", String()), ("text", String()), ("language", String()),
            ("informationOfficerContact", Nullable(String())), ("informationOfficerState", String()), ("contactOptional", Boolean()),
            ("locationOptional", Boolean()), ("locationNoticeVersion", String()), ("locationText", String()), ("minimisation", String())));
        Get("/auth/status", Shape("IdentityStatus", ("authenticated", Boolean()), ("demoMode", Boolean()), ("oidcConfigured", Boolean()),
            ("oidcSession", Boolean()), ("provider", String()), ("name", Nullable(String())), ("roles", Array(String())), ("permissions", Array(String())),
            ("tenantId", Nullable(String())), ("csrfToken", Nullable(String())),
            ("tenants", Nullable(Array(Object(("id", String()), ("name", String()), ("slug", String())))))));
        Get("/auth/login", authenticated); // Redirect response is configured by the document builder.
        Write("POST", "/auth/demo-login", Shape("DemoAuthenticationResult", ("authenticated", Boolean()), ("mode", String()), ("synthetic", Boolean())), Model<DemoLoginRequest>());
        Write("POST", "/auth/bootstrap-login", authenticated, Model<BootstrapLoginRequest>());
        Write("POST", "/auth/logout", authenticated);
        Get("/api/dispatcher", Shape("DispatcherIdentity", ("identity", String()), ("demoMode", Boolean())));
        Get("/api/crews", List<Crew>());
        Get("/api/work-order-types", trainingCatalogue);
        Get("/api/incidents", Array(staffCase));
        Get("/api/incidents/{id}", publicCase);
        Get("/api/dispatch/incidents/{id}", Ref("DispatcherIncident"));
        Get("/api/incidents/{id}/crew-eligibility", List<CrewEligibility>());
        Get("/api/metrics", Model<OperationalMetrics>());
        Get("/api/integrations/readiness", Shape("IntegrationReadiness", ("demoMode", Boolean()), ("providerChecksPerformed", Boolean()), ("checklist", List<ReadinessItem>())));
        Write("POST", "/api/retention/purge", Shape("RetentionResult", ("enabled", Boolean()), ("purged", Integer()), ("piiPurged", Integer()), ("retained", String())));
        // Disabled retention omits the retained explanation.
        ((JsonArray)s.Definitions["RetentionResult"]!["required"]!).RemoveAt(3);
        Write("POST", "/api/incidents", Shape("IntakeResult", ("incident", Ref("PublicIncident")), ("replayed", Boolean())), Model<IncidentRequest>());
        Write("POST", "/api/incidents/{id}/approve", changed, Model<ApprovalRequest>());
        Write("POST", "/api/incidents/{id}/assign", Ref("ChangedIncident"), Model<AssignmentRequest>());
        Write("POST", "/api/incidents/{id}/work-order-type", Ref("ChangedIncident"), Model<WorkOrderOverrideRequest>());
        Write("PATCH", "/api/incidents/{id}/status", Ref("ChangedIncident"), Model<StatusRequest>());
        Write("POST", "/api/incidents/{id}/comms/{draftId}/approve", Ref("ChangedIncident"), Model<CommsApproval>());
        Write("POST", "/api/incidents/{id}/callback", Flexible(Shape("CallbackResult", ("mode", String()), ("referenceNumber", String()), ("conversationId", Nullable(String())), ("message", String()), ("replayed", Boolean()))));
        Get("/open311/v2/services", Array(Shape("Open311Service", ("service_code", String()), ("service_name", String()), ("description", String()), ("metadata", Boolean()), ("type", String()), ("keywords", String()))));
        Write("POST", "/open311/v2/requests", Array(Shape("Open311Result", ("service_request_id", String()), ("status", String()), ("status_notes", String()), ("service_code", String()))), Model<Open311Request>());
        Get("/api/audit", List<AuditRecord>()); Get("/api/audit/verify", Model<AuditVerification>());
        Get("/api/training/audit", List<TrainingAuditRecord>()); Get("/api/training/audit/verify", Model<AuditVerification>());
        Get("/api/audit/export", new JsonObject { ["type"] = "string", ["format"] = "binary" });
        Write("POST", "/api/audit/export/local", Shape("LocalAuditExport", ("exportId", String()), ("manifest", Model<AuditExportManifest>()), ("target", String())));
        Get("/api/evidence/export", Shape("EvidenceExport", ("generatedAt", String("date-time")), ("privacy", String()), ("ai", Model<AiStatus>()),
            ("verification", Model<AuditVerification>()), ("lastAuditHash", Nullable(String())), ("cases", Array(Object(
                ("referenceNumber", String()), ("category", String()), ("department", String()), ("priority", String()), ("status", s.Type(typeof(IncidentStatus))),
                ("agentRuns", Array(Object(("agentName", String()), ("inputHash", String()), ("provider", String()), ("model", String()), ("latencyMs", Number()), ("confidence", Number()), ("fallbackUsed", Boolean())))),
                ("humanApprovalRecorded", Boolean()), ("assignedCrewId", Nullable(String())), ("zendeskTicketId", Nullable(Integer())))))));
        Get("/api/stream", String()); Get("/api/v1/training/stream", String());
        Get("/api/v1/map/config", Shape("MapConfiguration", ("tileUrl", String()), ("tileEnabled", Boolean()), ("attribution", String()), ("crewStaleMinutes", Integer()),
            ("bounds", Array(Number())), ("center", Array(Number())), ("demoMode", Boolean()), ("synthetic", Boolean()),
            ("geoVersion", Integer()), ("privacyNotice", String()), ("tileMode", String()), ("heatmap", String())));
        Get("/api/v1/map/layers", Ref("map-feature-collection"));
        Get("/api/v1/map/wards", Ref("map-feature-collection"));
        Get("/api/v1/map/heatmap", Ref("map-feature-collection"));
        Get("/api/v1/map/stream", String());
        Get("/api/v1/crew/auth/config", Model<CrewAuthConfiguration>());
        Write("POST", "/api/v1/crew/auth/challenge", Model<CrewOidcChallenge>(), Shape("CrewEmptyChallenge", []));
        Write("POST", "/api/v1/crew/auth/oidc", Model<CrewTokenIssued>(), Model<CrewOidcRequest>());
        Write("POST", "/api/v1/crew/pairing", Model<CrewPairingIssued>(), Model<CrewPairingRequest>());
        Write("POST", "/api/v1/crew/pairing/exchange", Ref(nameof(CrewTokenIssued)), Model<CrewPairingExchangeRequest>());
        Get("/api/v1/crew/devices", List<CrewDeviceView>());
        Write("POST", "/api/v1/crew/devices/{id}/revoke", Shape("CrewDeviceRevoked", ("revoked", Boolean())), Model<CrewRevokeRequest>());
        Get("/api/v1/crew/policy", Model<CrewPolicy>());
        Write("PUT", "/api/v1/crew/policy", Ref(nameof(CrewPolicy)), Model<CrewPolicyRequest>());
        Write("POST", "/api/v1/crew/retention/purge", Shape("CrewPurgeResult", ("pointsPurged", Integer()), ("photosPurged", Integer())));
        Get("/api/v1/crew/me", Model<CrewMe>());
        Get("/api/v1/crew/me/jobs", Model<CrewJobs>());
        Get("/api/v1/crew/me/jobs/{id}", Model<CrewJob>());
        Get("/api/v1/crew/jobs/{id}", Ref(nameof(CrewJob)));
        Get("/api/v1/crew/jobs/{id}/photos/{photoId}", new JsonObject { ["type"] = "string", ["format"] = "binary" });
        Write("POST", "/api/v1/crew/me/jobs/{id}/status", Model<CrewJobResult>(), Model<CrewJobStatusRequest>());
        Write("POST", "/api/v1/crew/me/jobs/{id}/notes", Ref(nameof(CrewJobResult)), Model<CrewJobNoteRequest>());
        Write("POST", "/api/v1/crew/me/jobs/{id}/photos", Ref(nameof(CrewJobResult)), Model<CrewJobPhotoRequest>());
        Get("/api/v1/crew/me/jobs/{id}/photos/{photoId}", new JsonObject { ["type"] = "string", ["format"] = "binary" });
        Write("POST", "/api/v1/crew/shifts/start", Ref(nameof(CrewMe)), Model<CrewOperationRequest>());
        Write("POST", "/api/v1/crew/shifts/end", Ref(nameof(CrewMe)), Model<CrewShiftEndRequest>());
        Write("POST", "/api/v1/crew/consent", Ref(nameof(CrewMe)), Model<CrewConsentRequest>());
        Write("DELETE", "/api/v1/crew/consent", Ref(nameof(CrewMe)), Ref(nameof(CrewOperationRequest)));
        Write("POST", "/api/v1/crew/locations", Model<CrewLocationResult>(), Ref("crew-location-request"));
        Get("/api/v1/crew/me/locations", Model<CrewLocationHistory>());
        Get("/api/v1/crew/me/locations/export", Ref(nameof(CrewLocationHistory)));
        Write("POST", "/api/v1/crew/sos", Model<CrewSosResult>(), Model<CrewSosRequest>());
        Write("POST", "/api/v1/sos", Model<SosResult>(), Model<SosCreateRequest>());
        Write("POST", "/api/v1/sos/{id}/location", Model<SosResult>(), Model<SosLocationRequest>());
        Write("POST", "/api/v1/sos/{id}/cancel", Model<SosAlert>(), Model<SosActionRequest>());
        foreach (var action in new[] { "acknowledge", "assess", "dispatch", "resolve", "false-alarm" })
            Write("POST", "/api/v1/sos/{id}/" + action, Model<SosAlert>(), Model<SosActionRequest>());
        Get("/api/v1/sos/queue", Model<SosQueue>());
        Get("/api/v1/sos/{id}", Model<SosAlert>());
        Get("/api/v1/sos/stream", String());
        var inboundSos = Shape("SosInboundResult", ("triggered", Boolean()), ("alert", Model<SosAlert>()), ("replayed", Boolean()),
            ("locationRequired", Boolean()), ("message", String()), ("emergencyNotice", String()), ("simulated", Boolean()));
        Write("POST", "/api/v1/sos/inbound", inboundSos, Model<SosInboundRequest>());
        var ussdSos = Shape("SosUssdResult", ("menu", String()), ("triggered", Boolean()), ("alert", Nullable(Model<SosAlert>())),
            ("replayed", Boolean()), ("locationRequired", Boolean()), ("message", String()), ("emergencyNotice", String()), ("simulated", Boolean()));
        // Menu-only USSD replies omit the alert fields. A confirmed selection returns them.
        s.Definitions["SosUssdResult"]!["required"] = new JsonArray("menu", "triggered", "emergencyNotice", "simulated");
        Write("POST", "/api/v1/sos/ussd", ussdSos, Model<SosUssdRequest>());
        Get("/api/v1/agencies", List<AgencyView>());
        Get("/api/v1/agencies/catalogue", Model<AgencyCatalogue>());
        Write("PUT", "/api/v1/agencies/{id}", Model<AgencyView>(), Model<AgencyToggleRequest>());
        Get("/api/v1/agency-incidents", Model<AgencyBoard>());
        Write("POST", "/api/v1/agency-incidents", Model<AgencyIncident>(), Model<AgencyIncidentCreateRequest>());
        Get("/api/v1/agency-incidents/{id}", Model<AgencyIncident>());
        Write("POST", "/api/v1/agency-incidents/{id}/commander", Model<AgencyIncident>(), Model<AgencyCommanderRequest>());
        Write("POST", "/api/v1/agency-incidents/{id}/status", Model<AgencyIncident>(), Model<AgencyStatusRequest>());
        Write("POST", "/api/v1/agency-incidents/{id}/mutual-aid", Model<AgencyIncident>(), Model<MutualAidCreateRequest>());
        Write("POST", "/api/v1/agency-incidents/{id}/mutual-aid/{requestId}/decision", Model<AgencyIncident>(), Model<MutualAidDecisionRequest>());
        Get("/api/v1/detections", Model<DetectionFeed>());
        Write("POST", "/api/v1/detections", Model<DetectionEvent>(), Model<DetectionEventRequest>());
        Write("POST", "/api/v1/detections/simulate", Model<DetectionFeed>(), Model<DetectionSimulateRequest>());
        Write("POST", "/api/v1/detections/{id}/confirm", Model<DetectionEvent>(), Model<DetectionDecisionRequest>());
        Write("POST", "/api/v1/detections/{id}/dismiss", Model<DetectionEvent>(), Model<DetectionDecisionRequest>());
        Get("/api/v1/map/crews/{id}", Shape("MapCrewDetail", ("crew", Model<Crew>()), ("position", Nullable(Model<GeoCrewPosition>())),
            ("synthetic", Boolean()), ("qualifications", Array(Object(("code", String()), ("level", Integer())))),
            ("certifications", Array(Object(("code", String()), ("issuedAt", String("date-time")), ("expiresAt", String("date-time")), ("source", String()), ("synthetic", Boolean())))), ("privacy", String())));
        Get("/api/v1/map/cases/{id}/nearest-crews", Shape("NearestCrewSuggestions", ("caseId", String()), ("caseVersion", Integer()),
            ("humanApprovalRequired", Boolean()), ("requiresCaseApproval", Boolean()), ("suggestions", List<GeoCrewSuggestion>()),
            ("distanceBasis", String()), ("emptyReason", Nullable(String()))));
        Write("POST", "/api/v1/map/cases/{id}/dispatch", Ref("ChangedIncident"), Model<MapDispatchRequest>());
        Write("POST", "/api/v1/map/cases/{id}/ward-override", Ref("ChangedIncident"), Model<MapWardOverrideRequest>());
        Write("POST", "/api/v1/map/hazards", Shape("ReviewedMapHazard", ("hazardId", String()), ("version", Integer()), ("humanReviewed", Boolean())), Ref("map-hazard-request"));
        Write("PUT", "/api/v1/map/geographies", Shape("ReviewedMapGeographies", ("version", Integer()), ("municipalityCount", Integer()), ("wardCount", Integer())), Model<MapGeographiesRequest>());
        Write("POST", "/api/v1/map/demo/seed", Shape("MapScenarioSeed", ("count", Integer()), ("created", Integer()), ("crewCount", Integer()),
            ("hazardCount", Integer()), ("simulated", Boolean()), ("synthetic", Boolean()), ("seed", Integer())));
        Write("POST", "/api/demo/seed", Shape("DemoSeed", ("count", Integer()), ("references", Array(String())), ("simulated", Boolean())));
        Get("/api/v1/training/work-order-types", Ref("work-order-types-response"));
        Get("/api/v1/training/skills", Ref("skills-response"));
        Get("/api/v1/training/certification-types", Ref("certification-types-response"));
        Get("/api/v1/training/workers/{id}/qualifications", Ref("worker-qualifications"));
        Get("/api/v1/training/work-order-types/{code}/scenarios-hint", Shape("ScenarioHints", ("version", String()), ("workOrderTypeCode", String()),
            ("synthetic", Boolean()), ("privacy", String()), ("examples", Array(Object(("title", String()), ("prompt", String()), ("expectedSkills", List<SkillRequirement>()), ("hazards", String()))))));
        Write("POST", "/api/v1/training/completions", Ref("training-completion-response"), Ref("training-completion"));
        Write("POST", "/api/v1/training/certifications", Ref("training-certification-response"), Ref("training-certification"));
        var voiceStatus = Shape("ElevenLabsStatus", ("mode", String()), ("voice", String()), ("chat", String()), ("callbacks", String()), ("sessionMode", String()),
            ("languages", Array(String())), ("uiLanguages", Array(String())), ("privacy", String()));
        Get("/api/channels/elevenlabs/status", voiceStatus);
        Get("/api/channels/zendesk/status", Model<IntegrationOutboxStatus>());
        Get("/api/channels/status", Shape("ChannelStatus", ("demoMode", Boolean()), ("web", Object(("mode", String()))), ("open311", Object(("mode", String()), ("protocol", String()))),
            ("elevenlabs", Ref("ElevenLabsStatus")), ("zendesk", Object(("mode", String()))), ("email", Object(("mode", String()), ("deliveryMode", String())))));
        Get("/api/channels/elevenlabs/alerts", Array(Shape("VoiceAlert", ("referenceNumber", Nullable(String())), ("alert", Nullable(String())), ("at", String("date-time")))));
        Get("/api/channels/elevenlabs/session", Flexible(Shape("ResidentSession", ("mode", String()), ("agentId", Nullable(String())), ("sessionMode", String()),
            ("serverLocation", String()), ("signed_url", String()), ("token", String()), ("conversation_id", String()))));
        var toolRequest = Shape("ResidentToolRequest", ("conversation_id", String()), ("turn_id", String()), ("description", String()), ("area", String()),
            ("contact_email", Nullable(String())), ("contact_phone", Nullable(String())), ("language", String()), ("channel", String()), ("contact_preference", String()),
            ("consent_to_contact", Boolean()), ("callback_consent", Boolean()), ("privacy_consent_accepted", Boolean()), ("privacy_consent_version", String()),
            ("reference_number", String()), ("information", String()), ("reason", String()));
        s.Definitions["ResidentToolRequest"]!["required"] = new JsonArray();
        s.Definitions["ResidentToolRequest"]!["description"] = "Tool-dependent fields. report_incident requires conversation_id, turn_id and a bounded description; status/mutation tools require a case reference; list_service_categories accepts an empty object. Snake-case fields shown; compatible camel-case aliases are also accepted.";
        s.Definitions["ResidentToolRequest"]!["additionalProperties"] = true;
        var toolResponse = Flexible(Shape("ResidentToolResult", ("referenceNumber", String()), ("status", String()), ("publicUpdate", String()),
            ("nextStep", String()), ("replayed", Boolean()), ("mode", String()), ("services", List<ServiceCategory>())));
        Write("POST", "/api/channels/elevenlabs/tools/{tool}", toolResponse, toolRequest);
        Write("POST", "/api/channels/elevenlabs/simulated-chat", Ref("ResidentToolResult"), Ref("ResidentToolRequest"));
        var webhook = Shape("ElevenLabsWebhook", ("type", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("post_call_transcription", "post_call_audio", "call_initiation_failure") }),
            ("data", Object(("conversation_id", String()), ("analysis", new JsonObject { ["type"] = "object", ["additionalProperties"] = true }),
                ("transcript", Array(new JsonObject { ["type"] = "object", ["additionalProperties"] = true })), ("failure_reason", String()), ("metadata", new JsonObject { ["type"] = "object", ["additionalProperties"] = true }))));
        s.Definitions["ElevenLabsWebhook"]!["properties"]!["data"]!["required"] = new JsonArray("conversation_id");
        s.Definitions["ElevenLabsWebhook"]!["properties"]!["data"]!["additionalProperties"] = true;
        s.Definitions["ElevenLabsWebhook"]!["additionalProperties"] = true;
        Write("POST", "/api/channels/elevenlabs/post-call", Flexible(Shape("VoiceWebhookResult", ("received", Boolean()), ("replayed", Boolean()), ("ignored", Boolean()),
            ("dispatcherAlert", Boolean()), ("referenceNumber", Nullable(String())), ("needsHumanReview", Boolean()), ("message", String()))), webhook);
        Write("POST", "/api/channels/zendesk/webhook", Flexible(Shape("ZendeskWebhookResult", ("ignored", Boolean()), ("reason", String()), ("replayed", Boolean()),
            ("recorded", Boolean()), ("referenceNumber", String()), ("needsHumanReview", Boolean()))), Model<ZendeskWebhookEvent>());
        Get("/api/integrations/outbox", Shape("OutboxView", ("status", Model<IntegrationOutboxStatus>()), ("items", Array(Shape("DeliveryIntentView",
            ("id", String()), ("caseReference", String()), ("operation", String()), ("state", s.Type(typeof(IntegrationDeliveryState))), ("attempts", Integer()),
            ("createdAt", String("date-time")), ("nextAttemptAt", String("date-time")), ("deliveredAt", Nullable(String("date-time"))), ("lastError", Nullable(String())), ("ticketId", Nullable(Integer())))))));
        Write("POST", "/api/integrations/outbox/{id}/retry", Shape("RetryResult", ("queued", Boolean())));
        Get("/api/admin/tenants", List<Tenant>());
        Write("POST", "/api/admin/tenants", Model<Tenant>(), Model<Tenant>()); Write("PUT", "/api/admin/tenants/{id}", Model<Tenant>(), Model<Tenant>());
        Get("/api/admin/crews", List<Crew>()); Write("POST", "/api/admin/crews", Model<Crew>(), Model<Crew>()); Write("PUT", "/api/admin/crews/{id}", Model<Crew>(), Model<Crew>());
        Get("/api/admin/workers", List<Worker>()); Write("POST", "/api/admin/workers", Model<Worker>(), Model<Worker>()); Write("PUT", "/api/admin/workers/{id}", Model<Worker>(), Model<Worker>());
        Get("/api/admin/catalogue", catalogue); Write("PUT", "/api/admin/catalogue", Ref("Catalogue"), Model<CatalogueEdit>());
        s.Definitions[nameof(CatalogueEdit)]!["properties"]!["catalogue"] = Ref("work-order-catalog");
        Get("/api/admin/overview", Shape("AdminOverview", ("tenant", Nullable(Model<Tenant>())), ("tenants", List<Tenant>()), ("users", List<BootstrapUserView>()),
            ("crews", List<Crew>()), ("workers", List<Worker>()), ("catalogue", Ref("Catalogue"))));
        Get("/api/admin/service-accounts", List<ServiceAccountView>());
        Write("POST", "/api/admin/service-accounts", Model<ServiceAccountIssued>(), Model<ServiceAccountCreateRequest>());
        Write("POST", "/api/admin/service-accounts/{id}/rotate", Model<ServiceAccountIssued>(), Model<ServiceAccountRotateRequest>());
        Write("POST", "/api/admin/service-accounts/{id}/revoke", Model<ServiceAccountView>());
        Get("/api/admin/users", List<BootstrapUserView>());
        Write("POST", "/api/admin/users", Model<BootstrapUserIssued>(), Model<BootstrapUserCreateRequest>());
        Write("PUT", "/api/admin/users/{id}", Model<BootstrapUserView>(), Model<BootstrapUserUpdateRequest>());
        Write("POST", "/api/admin/users/{id}/rotate", Model<BootstrapUserIssued>()); Write("POST", "/api/admin/users/{id}/revoke", Model<BootstrapUserView>());
        Get("/api/admin/privacy/requests", List<DataSubjectRequest>());
        Write("POST", "/api/admin/privacy/requests", Model<DataSubjectRequest>(), Model<DataSubjectRequestCreate>());
        Write("POST", "/api/admin/privacy/requests/{id}/verify", Model<DataSubjectRequest>(), Model<DataSubjectVerification>());
        Write("POST", "/api/admin/privacy/requests/{id}/fulfil", s.Add("PrivacyFulfilment", new JsonObject { ["anyOf"] = new JsonArray(
            Object(("requestId", String()), ("municipality", String()), ("exportedAt", String("date-time")), ("incident", Model<Incident>()),
                ("contact", Object(("contactEmail", Nullable(String())), ("contactPhone", Nullable(String())))), ("boundaries", String())),
            Object(("requestId", String()), ("action", String()), ("status", String()), ("version", Integer()), ("residual", String()))) }), Model<DataSubjectFulfilment>());
        Get("/api/v1/channels/readiness", Model<ChannelReadiness>());
        Get("/api/v1/channels/metrics", List<ChannelMetrics>());
        Get("/api/v1/channels/conversations", List<ChannelConversationView>());
        Get("/api/v1/channels/conversations/{id}", Model<ChannelConversationView>());
        Get("/api/v1/channels/templates", List<ChannelTemplate>());
        Write("POST", "/api/v1/channels/conversations/{id}/reply", Model<ChannelReplyResult>(), Model<ChannelReplyRequest>());
        Write("PUT", "/api/admin/channel-templates/{key}/{language}", Model<ChannelTemplate>(), Model<ChannelTemplateEdit>());
        Write("POST", "/api/admin/channel-templates/preview", Model<ChannelTemplatePreviewResult>(), Model<ChannelTemplatePreview>());
        Write("POST", "/api/v1/channels/simulated/{provider}", Model<ChannelIntakeResult>(), Model<InboundMessage>());
        Write("POST", "/api/v1/channels/demo/seed", Model<ChannelSeedResult>());
        Write("POST", "/api/v1/channels/retention/purge", Model<ChannelPurgeResult>());
        Get("/api/v1/channels/webhooks/whatsapp", String());
        Write("POST", "/api/v1/channels/webhooks/{provider}", s.Add("NativeChannelResult",
            new JsonObject { ["anyOf"] = new JsonArray(Model<ChannelWebhookResult>(), String()) }),
            s.Add("NativeChannelWebhook", new JsonObject { ["anyOf"] = new JsonArray(
                new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
                new JsonObject { ["type"] = "string", ["maxLength"] = 262144 }),
                ["description"] = "Exact signed provider JSON or Twilio form body; native adapters enforce fields, bounds and signature. Unknown metadata is never retained." }));
        ApplyBounds(s);
    }
    private static JsonObject Flexible(JsonObject reference) => reference;
    private static void ApplyBounds(OpenApiSchemas schemas)
    {
        // Variant responses share fields, some of which are omitted by particular outcomes.
        foreach (var name in new[] { "CallbackResult", "ResidentSession", "ResidentToolResult", "VoiceWebhookResult", "ZendeskWebhookResult" })
            schemas.Definitions[name]!["required"] = new JsonArray();
        var fields = schemas.Definitions[nameof(IncidentRequest)]!["properties"]!;
        fields["description"]!["minLength"] = 10; fields["description"]!["maxLength"] = RequestValidation.DescriptionMax;
        void Bound(string name, int maximum)
        {
            var schema = fields[name]!["anyOf"]?[0] ?? fields[name]!;
            schema["maxLength"] = maximum;
        }
        Bound("area", 200); Bound("contactEmail", 254); Bound("contactPhone", 40); Bound("residentLanguage", 12);
        fields["location"]!["description"] = "Optional volunteered report coordinate in the regional South African bounding box. Requires shareLocation=true and current purpose consent outside explicit demo. Never inferred from text and never a person tracking feed; geofencing remains unverified.";
        fields["shareLocation"]!["description"] = "Explicit choice to share this report's coordinate under civicops-location-v1; false by default. Shared notice text/version is retained in the consent receipt. No continuous collection or browser geolocation occurs.";
        schemas.Definitions[nameof(CrewJobStatusRequest)]!["properties"]!["status"]!["enum"] = new JsonArray("en-route", "on-site", "paused", "complete", "cannot-complete");
        schemas.Definitions[nameof(CrewJobPhotoRequest)]!["properties"]!["contentType"]!["enum"] = new JsonArray("image/png", "image/jpeg");
        schemas.Definitions[nameof(CrewJobPhotoRequest)]!["properties"]!["dataBase64"]!["maxLength"] = 2_800_000;
        schemas.Definitions[nameof(CrewConsentRequest)]!["properties"]!["noticeVersion"]!["const"] = CrewStore.NoticeVersion;
        schemas.Definitions[nameof(CrewConsentRequest)]!["properties"]!["accepted"]!["const"] = true;
        foreach (var name in new[] { nameof(CrewJobStatusRequest), nameof(CrewJobNoteRequest), nameof(CrewJobPhotoRequest) }) schemas.Definitions[name]!["properties"]!["expectedVersion"]!["minimum"] = 1;
        var coordinateFields = schemas.Definitions[nameof(GeoPoint)]!["properties"]!;
        coordinateFields["longitude"]!["minimum"] = 16; coordinateFields["longitude"]!["maximum"] = 33;
        coordinateFields["latitude"]!["minimum"] = -35; coordinateFields["latitude"]!["maximum"] = -22;
        var ring = Array(Ref("map-feature-collection/$defs/position")); ring["minItems"] = 4; ring["maxItems"] = 65;
        var polygon = Array(ring); polygon["minItems"] = 1; polygon["maxItems"] = 8;
        schemas.Definitions[nameof(GeoPolygon)]!["properties"]!["coordinates"] = polygon;
        var boundaries = GeoScenario.Boundaries("default");
        schemas.Definitions[nameof(MapGeographiesRequest)]!["example"] = JsonSerializer.SerializeToNode(
            new MapGeographiesRequest(0, boundaries.Municipalities, boundaries.Wards, "Synthetic boundary review example; no real municipality."), JsonSerializerOptions.Web);
        foreach (var name in new[] { nameof(MapDispatchRequest), nameof(MapWardOverrideRequest), nameof(MapGeographiesRequest) })
            schemas.Definitions[name]!["properties"]!["expectedVersion"]!["minimum"] = name == nameof(MapGeographiesRequest) ? 0 : 1;
        fields["residentLanguage"]!["anyOf"]![0]!["enum"] = new JsonArray(AgentSchemas.Languages.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
        fields["contactPreference"]!["anyOf"]![0]!["enum"] = new JsonArray(AgentSchemas.ContactPreferences.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
        foreach (var name in new[] { nameof(ApprovalRequest), nameof(AssignmentRequest), nameof(StatusRequest), nameof(WorkOrderOverrideRequest), nameof(CommsApproval) })
            schemas.Definitions[name]!["properties"]!["expectedVersion"]!["anyOf"]![0]!["minimum"] = 1;
        schemas.Definitions[nameof(DemoLoginRequest)]!["properties"]!["role"]!["enum"] = new JsonArray(PermissionMatrix.Roles.Select(role => (JsonNode)JsonValue.Create(role)!).ToArray());
        foreach (var name in new[] { nameof(BootstrapUserCreateRequest), nameof(BootstrapUserUpdateRequest) })
            schemas.Definitions[name]!["properties"]!["roles"]!["items"]!["enum"] = new JsonArray(PermissionMatrix.Roles.Select(role => (JsonNode)JsonValue.Create(role)!).ToArray());
        schemas.Definitions[nameof(ServiceAccountCreateRequest)]!["properties"]!["scopes"]!["items"]!["enum"] = new JsonArray(ServiceAccountStore.AllowedScopes.Select(scope => (JsonNode)JsonValue.Create(scope)!).ToArray());
    }
}
