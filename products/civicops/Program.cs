using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.AspNetCore.Mvc;

StartupFailure.Register();
if (AuditExportCommand.IsVerifyCommand(args))
{
    Environment.ExitCode = await AuditExportCommand.RunVerifierAsync(args, new ConfigurationBuilder().AddEnvironmentVariables().Build());
    return;
}
if (StorageCommand.IsCommand(args))
{
    Environment.ExitCode = await StorageCommand.RunAsync(args, new ConfigurationBuilder().AddEnvironmentVariables().Build(), Directory.GetCurrentDirectory());
    return;
}
if (ElevenLabsProvisioner.IsCommand(args))
{
    Environment.ExitCode = await ElevenLabsProvisioner.RunAsync(args, new ConfigurationBuilder().AddEnvironmentVariables().Build(), Directory.GetCurrentDirectory());
    return;
}
if (ZendeskCheck.IsCommand(args))
{
    Environment.ExitCode = await ZendeskCheck.RunAsync(args, new ConfigurationBuilder().AddEnvironmentVariables().Build());
    return;
}
var builder = WebApplication.CreateBuilder(AuditExportCommand.IsExportCommand(args) ? [] : args);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddCivicOpsForwarding(builder.Configuration);
builder.AddCivicOpsObservability();
builder.AddCivicOpsSecrets();
builder.Services.AddCivicOpsStorage();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider => CivicOpsOptions.From(builder.Configuration, provider.GetRequiredService<IWebHostEnvironment>()));
builder.Services.AddSingleton<DataDirectoryLease>();
builder.Services.AddSingleton(provider => SpendCaps.From(builder.Configuration,
    provider.GetRequiredService<CivicOpsOptions>().DataDirectory, provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RetentionService>());
builder.Services.AddSingleton<IncidentStore>();
builder.Services.AddSingleton<AuditExportService>();
builder.Services.AddSingleton<PrivacyService>();
builder.Services.AddSingleton<DeterministicClassifier>();
builder.Services.AddSingleton<EmailNotificationService>();
builder.Services.AddSingleton<DispatcherAccess>();
builder.Services.AddSingleton<TenantStore>();
builder.Services.AddCivicOpsIdentity(builder.Configuration);
builder.Services.AddCivicOpsAuthorization();
builder.Services.AddSingleton<CatalogueAdminStore>();
builder.Services.AddSingleton<CaseEventStream>();
builder.Services.AddCivicOpsMap(builder.Configuration);
builder.Services.AddCivicOpsCrew();
builder.Services.AddSingleton<IncidentCoordinator>();
builder.Services.AddCivicOpsAgents(builder.Configuration);
builder.Services.AddZendeskIntegration();
builder.Services.AddElevenLabs();
builder.Services.AddCivicOpsChannels();
builder.Services.AddCivicOpsSos();
builder.Services.AddCivicOpsAgencies();
builder.Services.AddCivicOpsTraining(builder.Configuration);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, ct) => await Results.Problem("Request quota exceeded. Try again later.", statusCode: 429).ExecuteAsync(context.HttpContext);
    var limits = CivicOpsOptions.From(builder.Configuration, builder.Environment);
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.GetEndpoint()?.Metadata.GetMetadata<TrainingRequired>() is not null && context.Items["accessBlocked"] is not null
            ? RateLimitPartition.GetFixedWindowLimiter(RequestHardening.Partition(context, "training-auth"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.AuthLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })
        : context.GetEndpoint()?.Metadata.GetMetadata<ElevenLabsToolRequired>() is not null
            ? RateLimitPartition.GetFixedWindowLimiter(RequestHardening.Partition(context, "tools-total"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.ToolLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })
            : RateLimitPartition.GetNoLimiter("other"));
    foreach (var name in RequestHardening.Policies)
    {
        options.AddPolicy(name, context =>
        {
            var demo = context.User.Identity?.AuthenticationType == "demo-compat";
            var demoRead = name == "dispatch" && demo && HttpMethods.IsGet(context.Request.Method);
            var limit = name == "dispatch" && (demoRead || context.Items["dispatcherPartition"] is not null || context.User.Identity?.IsAuthenticated is true && !demo)
                ? limits.DispatchLimit : name == "session" ? limits.AuthLimit : limits.IntakeLimit;
            // Demo reads and demo actions get separate counters; a limiter's permit count is fixed when its partition is created.
            var key = RequestHardening.Partition(context, name)
                + (name == "tools" && context.Items["toolAuthenticated"] is true ? ":conversation:" + (context.Items["conversationPartition"] as string ?? "authenticated-ip") : "")
                + (name == "dispatch" && demo ? (demoRead ? ":demo-read" : ":demo-write") : "");
            return RateLimitPartition.GetFixedWindowLimiter(key,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
        });
    }
});
var app = builder.Build();
await app.Services.GetRequiredService<DurableStorage>().InitializeAsync();
_ = app.Services.GetRequiredService<DataDirectoryLease>();
_ = app.Services.GetRequiredService<WorkOrderCatalog>();
await app.Services.GetRequiredService<WorkforceStore>().InitializeAsync();
if (app.Services.GetRequiredService<DispatcherAccess>().DemoMode)
    foreach (var tenant in await app.Services.GetRequiredService<TenantStore>().ListAsync())
        using (TenantContext.Use(tenant.Id)) await app.Services.GetRequiredService<WorkforceStore>().InitializeAsync();
if (AuditExportCommand.IsExportCommand(args))
{
    Environment.ExitCode = await AuditExportCommand.RunExportAsync(args, app.Services);
    return;
}
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationMiddleware>();
app.UseCivicOpsSecurityHeaders();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var status = exception is BadHttpRequestException bad ? bad.StatusCode : 500;
    await Results.Problem(status == 500 ? "An unexpected error occurred. Contact the municipality with the request trace identifier."
        : "The request could not be processed.", statusCode: status,
        extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
}));
app.UseStatusCodePages(async status => await Results.Problem("The request could not be completed.",
    statusCode: status.HttpContext.Response.StatusCode).ExecuteAsync(status.HttpContext));
app.UseCivicOpsTenantPath();
app.UseCivicOpsHtmlShell();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCivicOpsMediaType();
app.UseRouting();
app.UseAuthentication();
app.UseCivicOpsPrincipal();
app.UseCivicOpsTrainingAuthentication();
// Authenticate before the dispatch limiter creates or charges any principal's quota.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<DispatchRequired>() is not null)
    {
        var blocked = context.RequestServices.GetRequiredService<DispatcherAccess>().Check(context.Request);
        if (blocked is not null) context.Items["accessBlocked"] = blocked;
    }
    if (context.GetEndpoint()?.Metadata.GetMetadata<ElevenLabsToolRequired>() is not null)
    {
        var options = context.RequestServices.GetRequiredService<ElevenLabsOptions>();
        var demo = context.RequestServices.GetRequiredService<DispatcherAccess>().DemoMode;
        if (context.User.HasClaim(claim => claim.Type == "civicops:service-account"))
            context.Items["toolAuthenticated"] = context.User.HasClaim("civicops:scope", "tools:invoke");
        else if (string.IsNullOrWhiteSpace(options.ToolSecret) && !demo)
            context.Items["accessBlocked"] = Results.Problem("Resident tool authentication is not configured.", statusCode: 503);
        else if (!string.IsNullOrWhiteSpace(options.ToolSecret) && !ElevenLabsSignature.EqualSecret(options.ToolSecret, context.Request.Headers["X-CivicOps-Tool-Secret"].ToString()))
            context.Items["accessBlocked"] = Results.Unauthorized();
        else context.Items["toolAuthenticated"] = true;
        if (context.Items["toolAuthenticated"] is true)
        {
            var conversation = context.Request.Headers["X-CivicOps-Conversation-Id"].ToString();
            if (conversation.Length > 128) context.Items["accessBlocked"] = Results.BadRequest(new { error = "Conversation header exceeds its bound." });
            else if (!string.IsNullOrWhiteSpace(conversation)) context.Items["conversationPartition"] = IncidentStore.Hash(conversation);
        }
    }
    // Operational data, including public status lookups and agent sessions, must never be cached.
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/open311"))
        context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next(context);
});
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Items["accessBlocked"] is IResult authenticationBlocked) { await authenticationBlocked.ExecuteAsync(context); return; }
    await next(context);
});
app.UseAuthorization();
app.UseCivicOpsResourceGuards();
app.UseCivicOpsRequestBounds();
var routes = app.MapGroup("").WithCivicOpsProblems();
routes.ApplyPolicies();
routes.MapCivicOpsIdentityEndpoints();
routes.MapCivicOpsAdminEndpoints();
routes.MapPrivacyEndpoints();
routes.MapAuditExportEndpoints();
routes.MapCivicOpsHealth();
routes.MapCivicOpsMapEndpoints();
routes.MapCivicOpsCrewEndpoints();
routes.MapCivicOpsSosEndpoints();
routes.MapCivicOpsAgencyEndpoints();
routes.MapCivicOpsChannelEndpoints();
routes.MapGet("/healthz", (EmailNotificationService email, DispatcherAccess access, DurableStorage storage) => Results.Ok(new
{ status = "ok", service = "civicops", emailMode = email.GetStatus().Mode, demoMode = access.DemoMode,
    dataMode = storage.IsPostgres ? "transactional PostgreSQL" : "single-process durable JSON" }));
routes.MapGet("/api/email/status", (EmailNotificationService email) =>
{
    var status = email.GetStatus();
    return Results.Ok(new { status.Enabled, status.Mode, status.SmtpConfigured, recipientConfigured = status.NotifyTo is not null });
});
routes.MapGet("/api/ai/triage-contract", () => Results.Ok(new { systemPrompt = AgentSchemas.Triage.SystemPrompt, schema = AgentSchemas.Triage.Schema }));
routes.MapGet("/api/spend-caps", (SpendCaps caps) =>
{
    var llm = caps.Reached(SpendCaps.Llm); var voice = caps.Reached(SpendCaps.Voice);
    return Results.Ok(new { reached = llm || voice, llmReached = llm, voiceReached = voice, label = llm || voice ? SpendCaps.Label : null });
});
routes.MapGet("/api/ai/status", (AiReasoningClient ai) => Results.Ok(ai.GetStatus()));
routes.MapGet("/api/crews", async (HttpContext http, WorkforceStore workforce, CancellationToken ct) => Results.Ok((await workforce.ListCrewsAsync(ct)).Where(crew => !EnterpriseAccessMiddleware.CrewOnly(http.User) || crew.Id == http.User.FindFirst("civicops:crew")?.Value)));
routes.MapGet("/api/work-order-types", (WorkOrderCatalog catalog) => Results.Ok(new { version = catalog.Version, workOrderTypes = catalog.WorkOrderTypes }))
    .WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/api/incidents/{id}/crew-eligibility", async (string id, IncidentStore store, CrewEligibilityService eligibility, CancellationToken ct) =>
{
    var incident = await store.GetAsync(id, ct);
    return incident is null ? Results.NotFound() : Results.Ok(await eligibility.ListAsync(incident, ct));
}).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/api/metrics", async (IncidentStore store, DispatcherAccess access, CancellationToken ct) =>
    Results.Ok(AuditMetrics.Compute(await store.AuditAsync(ct), access.DemoMode)))
    .WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/api/integrations/readiness", (IConfiguration config, DispatcherAccess access) => Results.Ok(IntegrationReadiness.Describe(config, access.DemoMode)))
    .WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapPost("/api/retention/purge", async (RetentionService retention, CancellationToken ct) => Results.Ok(await retention.PurgeAsync(ct)))
    .WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/api/dispatcher", (HttpContext http, DispatcherAccess access) => Results.Ok(new { identity = access.Identity(http), access.DemoMode })).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/api/incidents", async (HttpContext http, IncidentStore store, CancellationToken ct) => Results.Ok((await store.ListAsync(ct)).Where(incident => EnterpriseAccessMiddleware.AssignedTo(http.User, incident)).Select(IncidentView.Dispatcher)))
    .WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/api/incidents/{id}", async (string id, IncidentStore store, CancellationToken ct) =>
{
    var incident = await store.GetAsync(id, ct); return incident is null ? Results.NotFound() : Results.Ok(IncidentView.Public(incident));
}).RequireRateLimiting("intake");
routes.MapGet("/api/dispatch/incidents/{id}", async (string id, IncidentStore store, CancellationToken ct) =>
{
    var incident = await store.GetAsync(id, ct); return incident is null ? Results.NotFound() : Results.Ok(IncidentView.Dispatcher(incident));
}).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapPost("/api/incidents", async (HttpRequest http, IncidentRequest request, IncidentCoordinator coordinator, CivicOpsOptions options, CancellationToken ct) =>
{
    var key = http.Headers["Idempotency-Key"].FirstOrDefault();
    var error = RequestValidation.Intake(request, key) ?? PrivacyConsent.Validate(request, !options.DemoMode);
    if (error is not null) return Results.BadRequest(new { error });
    var result = await coordinator.CreateAsync(request with { Source = "web", SourceLabel = "Resident form" }, key, ct);
    if (result.Error is not null) return Results.Conflict(new { error = result.Error, incident = IncidentView.Public(result.Incident) });
    var response = new { incident = IncidentView.Public(result.Incident), replayed = !result.Created };
    return result.Created ? Results.Created($"/api/incidents/{result.Incident.Id}", response) : Results.Ok(response);
}).RequireRateLimiting("intake");
routes.MapPost("/api/incidents/{id}/work-order-type", async (HttpContext http, string id, WorkOrderOverrideRequest request, IncidentCoordinator coordinator, DispatcherAccess access, CancellationToken ct) =>
{
    var error = RequestValidation.Action(null, request.Note, request.ExpectedVersion) ?? RequestValidation.Text(request.WorkOrderTypeCode, "workOrderTypeCode", 80, 1);
    return error is not null ? Results.BadRequest(new { error }) : Changed(await coordinator.OverrideWorkOrderTypeAsync(id, request, access.Identity(http), ct));
}).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/openapi.json", (Microsoft.AspNetCore.Routing.EndpointDataSource endpoints) => Results.Json(OpenApiDocument.Create(endpoints)));
routes.MapPost("/api/incidents/{id}/approve", async (HttpContext http, string id, ApprovalRequest request, IncidentCoordinator coordinator, DispatcherAccess access, CancellationToken ct) =>
{
    var error = RequestValidation.Action(request.ApprovedBy, request.Note, request.ExpectedVersion);
    return error is not null ? Results.BadRequest(new { error }) : Changed(await coordinator.ApproveAsync(id, request, access.Identity(http), ct));
}).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapPost("/api/incidents/{id}/assign", async (HttpContext http, string id, AssignmentRequest request, IncidentCoordinator coordinator, DispatcherAccess access, CancellationToken ct) =>
{
    var error = RequestValidation.Action(request.ApprovedBy, request.Note, request.ExpectedVersion) ?? RequestValidation.Text(request.CrewId, "crewId", 80, 1);
    return error is not null ? Results.BadRequest(new { error }) : Changed(await coordinator.AssignAsync(id, request, access.Identity(http), ct));
}).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapPatch("/api/incidents/{id}/status", async (HttpContext http, string id, StatusRequest request, IncidentCoordinator coordinator, DispatcherAccess access, CancellationToken ct) =>
{
    if (EnterpriseAccessMiddleware.CrewWritesRestricted(http.User) && request.Status is not ("InProgress" or "WaitingForCitizen" or "Resolved")) return Results.StatusCode(403);
    var error = RequestValidation.Action(request.ApprovedBy, request.PublicUpdate, request.ExpectedVersion) ?? RequestValidation.Text(request.Status, "status", 32, 1) ?? RequestValidation.Text(request.ApprovalNote, "approvalNote", 2000);
    return error is not null ? Results.BadRequest(new { error }) : Changed(await coordinator.StatusAsync(id, request, access.Identity(http), ct));
}).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapPost("/api/incidents/{id}/comms/{draftId}/approve", async (HttpContext http, string id, string draftId, CommsApproval request, IncidentStore store, DispatcherAccess access, IntegrationOutbox outbox, EmailNotificationService email, TimeProvider time, CancellationToken ct) =>
{
    var invalid = RequestValidation.Action(null, null, request.ExpectedVersion);
    if (invalid is not null) return Results.BadRequest(new { error = invalid });
    var changed = await store.ChangeAsync(id, request.ExpectedVersion, incident =>
    {
        var draft = incident.CommsDrafts.FirstOrDefault(x => x.Id == draftId);
        if (draft is null) return "Unknown draft.";
        if (draft.State != "PendingReview") return "This draft has already been reviewed.";
        if (draft.Status != incident.Status.ToString()) return "This draft describes an earlier status. Refresh and review the current update.";
        draft.State = "Approved"; draft.ApprovedBy = access.Identity(http); draft.ApprovedAt = time.GetUtcNow();
        incident.PublicUpdate = draft.Text;
        incident.Audit.Add(new("comms-approved", access.Identity(http), $"Approved {draft.Language} draft for {draft.Status}.", time.GetUtcNow()));
        return null;
    }, ct);
    if (changed.Error is null && changed.Incident is not null)
    {
        await outbox.EnqueueApprovedDraftAsync(changed.Incident, changed.Incident.CommsDrafts.Single(x => x.Id == draftId), ct);
        // Resident drafts are outbox-only; SMTP delivery is not part of draft approval.
        if (email.GetStatus().Mode == "outbox" && changed.Incident.ConsentToContact) await email.NotifyAsync(changed.Incident, changed.Incident.PublicUpdate, ct);
    }
    return Changed(changed);
}).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/open311/v2/services", () => Results.Ok(ServiceCatalog.All.Select(x => new
{ service_code = x.ServiceCode, service_name = x.ServiceName, description = x.Department, metadata = false, type = "realtime", keywords = x.ServiceName }))).RequireRateLimiting("intake");
routes.MapPost("/open311/v2/requests", async (JsonElement payload, HttpRequest http, IncidentCoordinator coordinator, CivicOpsOptions options, CancellationToken ct) =>
{
    if (payload.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { error = "An Open311 JSON object is required." });
    string[][] fields = [["service_code", "serviceCode"], ["description"], ["address_string", "addressString"], ["email"], ["location_provenance", "locationProvenance"]];
    var values = new string?[fields.Length];
    for (var i = 0; i < fields.Length; i++) if (!RequestValidation.Read(payload, fields[i], out values[i], out var error)) return Results.BadRequest(new { error });
    var service = ServiceCatalog.Find(values[0]);
    if (service is null) return Results.BadRequest(new { error = "Unknown service_code. Discover codes at /open311/v2/services." });
    var request = new IncidentRequest(values[1] ?? $"Open311 {service.ServiceName} report", values[2], values[3], "open311", "Open311", values[4], ServiceCode: service.ServiceCode,
        PrivacyConsentAccepted: payload.TryGetProperty("privacy_consent_accepted", out var accepted) && accepted.ValueKind is JsonValueKind.True or JsonValueKind.False ? accepted.GetBoolean() : null,
        PrivacyConsentVersion: payload.TryGetProperty("privacy_consent_version", out var consentVersion) && consentVersion.ValueKind == JsonValueKind.String ? consentVersion.GetString() : null);
    var key = http.Headers["Idempotency-Key"].FirstOrDefault();
    var invalid = RequestValidation.Intake(request, key) ?? PrivacyConsent.Validate(request, !options.DemoMode);
    if (invalid is not null) return Results.BadRequest(new { error = invalid });
    var created = await coordinator.CreateAsync(request, key, ct);
    if (created.Error is not null) return Results.Conflict(new { error = created.Error });
    return Results.Ok(new[] { new { service_request_id = created.Incident.ReferenceNumber, status = "open", status_notes = "Received for human review; no response time promised.", service_code = service.ServiceCode } });
}).RequireRateLimiting("intake");
routes.MapGet("/api/evidence/export", async (IncidentStore store, AiReasoningClient ai, TimeProvider time, CancellationToken ct) =>
{
    var cases = await store.ListAsync(ct);
    var audit = await store.AuditAsync(ct);
    return Results.Ok(new
    {
        generatedAt = time.GetUtcNow(), privacy = "No resident contact, descriptions, transcripts or internal notes included.",
        ai = ai.GetStatus(), verification = await store.VerifyAuditAsync(ct), lastAuditHash = audit.Count > 0 ? audit[^1].Hash : null,
        cases = cases.Select(incident => new
        {
            incident.ReferenceNumber, incident.Category, incident.Department, incident.Priority, incident.Status,
            agentRuns = incident.AgentRuns.Select(run => new { run.AgentName, run.InputHash, run.Provider, run.Model, run.LatencyMs, run.Confidence, run.FallbackUsed }),
            humanApprovalRecorded = incident.Approval is not null, incident.AssignedCrewId, incident.ZendeskTicketId
        })
    });
}).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/api/audit", async (IncidentStore store, CancellationToken ct) => Results.Ok(await store.AuditAsync(ct))).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/api/audit/verify", async (IncidentStore store, CancellationToken ct) => Results.Ok(await store.VerifyAuditAsync(ct))).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapGet("/api/stream", async (HttpContext context, CaseEventStream events) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-store";
    var subscription = events.Subscribe();
    try
    {
        await context.Response.WriteAsync("event: ready\ndata: {}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        await foreach (var change in subscription.Reader.ReadAllAsync(context.RequestAborted))
        {
            await context.Response.WriteAsync($"event: {change.Type}\ndata: {{}}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    finally { events.Unsubscribe(subscription.Id); }
}).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapPost("/api/demo/seed", async (DispatcherAccess access, IncidentCoordinator coordinator, CancellationToken ct) =>
{
    if (!access.DemoMode) return Results.NotFound();
    var references = new List<string>();
    foreach (var request in DemoData.Cases.Select((value, index) => (value, index)))
        references.Add((await coordinator.CreateAsync(request.value, $"demo-seed-v1-{request.index}", ct)).Incident.ReferenceNumber);
    return Results.Ok(new { count = references.Count, references, simulated = true });
}).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
routes.MapCivicOpsTrainingEndpoints();
routes.MapZendeskEndpoints();
routes.MapElevenLabsEndpoints();
routes.MapGet("/api/channels/status", (ElevenLabsOptions eleven, ZendeskClient zendesk, DispatcherAccess access, EmailNotificationService email) => Results.Ok(new
{
    demoMode = access.DemoMode,
    web = new { mode = access.DemoMode ? "simulated" : "live" },
    open311 = new { mode = access.DemoMode ? "simulated" : "live", protocol = "bounded JSON subset" },
    elevenlabs = ElevenLabsEndpoints.Status(eleven), zendesk = new { mode = zendesk.Mode },
    email = new { mode = email.GetStatus().Mode == "outbox" ? "simulated" : "live", deliveryMode = email.GetStatus().Mode }
}));
app.Run();
static IResult Changed(ChangeResult result) => result.Incident is null ? Results.NotFound()
    : result.Conflict ? Results.Conflict(new { error = result.Error, incident = IncidentView.Dispatcher(result.Incident) })
    : result.Error is not null ? Results.BadRequest(new { error = result.Error })
    : Results.Ok(new { incident = IncidentView.Dispatcher(result.Incident) });
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1050:Declare types in namespaces", Justification = "Global entry point is required by WebApplicationFactory<Program> integration tests.")]
public partial class Program;
