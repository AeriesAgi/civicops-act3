using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CivicOps.Models;
using Microsoft.AspNetCore.Mvc;

namespace CivicOps.Services;

public static class TrainingEndpoints
{
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static IServiceCollection AddCivicOpsTraining(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<TrainingAccess>(); services.AddSingleton<TrainingStore>();
        services.AddHostedService<TrainingRecoveryService>();
        services.AddRateLimiter(options => options.AddPolicy("training", context => context.Items["accessBlocked"] is not null
            ? RateLimitPartition.GetNoLimiter("rejected-training") : RateLimitPartition.GetFixedWindowLimiter(
            RequestHardening.Partition(context, "training"),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = context.RequestServices.GetRequiredService<TrainingAccess>().RequestLimit,
                Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            })));
        return services;
    }

    // Must execute after UseRouting and before UseRateLimiter, including on non-training routes.
    public static IApplicationBuilder UseCivicOpsTrainingAuthentication(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var blocked = context.RequestServices.GetRequiredService<TrainingAccess>().Check(context);
        if (blocked is not null) context.Items["accessBlocked"] = blocked;
        await next(context);
    });

    public static IEndpointRouteBuilder MapCivicOpsTrainingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/training").WithMetadata(new TrainingRequired()).RequireRateLimiting("training");
        group.MapGet("/work-order-types", (HttpContext http, WorkOrderCatalog catalog) => Catalogue(http, catalog,
            new { version = catalog.Version, workOrderTypes = catalog.WorkOrderTypes }));
        group.MapGet("/skills", (HttpContext http, WorkOrderCatalog catalog) => Catalogue(http, catalog,
            new { version = catalog.Version, skills = catalog.Skills }));
        group.MapGet("/certification-types", (HttpContext http, WorkOrderCatalog catalog) => Catalogue(http, catalog,
            new { version = catalog.Version, certificationTypes = catalog.CertificationTypes }));
        group.MapGet("/work-order-types/{code}/scenarios-hint", (string code, WorkOrderCatalog catalog) =>
        {
            var type = catalog.Find(code);
            return type is null ? Results.NotFound() : Results.Ok(new
            {
                version = catalog.Version, workOrderTypeCode = type.Code, synthetic = true,
                privacy = "Synthetic case-pattern hints; no incidents, locations, transcripts or resident information are exposed.",
                examples = new[]
                {
                    new { title = "Routine response", prompt = $"A synthetic municipal crew receives a {type.Title.ToLowerInvariant()} work order in a fictitious training area. Assess hazards, confirm qualifications and follow the listed procedure.", expectedSkills = type.RequiredSkills, hazards = type.HazardClass },
                    new { title = "Qualification and hazard escalation", prompt = "During a synthetic shift handover, a required qualification has expired and a hazard changes. Practise stopping work, notifying the supervisor and selecting an eligible crew.", expectedSkills = type.RequiredSkills, hazards = type.HazardClass }
                }
            });
        });
        group.MapGet("/workers/{id}/qualifications", async (string id, WorkforceStore workforce, TrainingStore store, CancellationToken ct) =>
        {
            var worker = await workforce.GetWorkerAsync(id, ct);
            return worker is null ? Results.NotFound() : Results.Ok(new
            {
                workerId = worker.Id, worker.DisplayName, worker.CrewId, worker.Skills, worker.Certifications,
                trainingCompletions = (await store.QualificationsAsync(id, ct)).Select(CompletionView), synthetic = true
            });
        });
        group.MapPost("/completions", async (JsonElement payload, HttpContext http, TrainingStore store, TrainingAccess access, CancellationToken ct) =>
        {
            var request = Read<TrainingCompletionRequest>(payload,
                ["workerId", "moduleId", "workOrderTypeCode", "skillsPractised", "score", "passed", "durationSeconds", "attempt", "deviceClientLabel", "evidenceHash", "completedAt"], out var error);
            if (error is not null) return Results.BadRequest(new { error });
            var result = await store.CompleteAsync(request!, Key(http.Request), access.Identity(http), ct);
            return result.NotFound ? Results.NotFound(new { error = result.Error })
                : result.Conflict ? Results.Conflict(new { error = result.Error })
                : result.Error is not null ? Results.BadRequest(new { error = result.Error })
                : result.Created ? Results.Created($"/api/v1/training/workers/{request!.WorkerId}/qualifications", new { completion = CompletionView(result.Record!), replayed = false })
                : Results.Ok(new { completion = CompletionView(result.Record!), replayed = true });
        }).WithMetadata(new RequestSizeLimitAttribute(16384));
        group.MapPost("/certifications", async (JsonElement payload, HttpContext http, TrainingStore store, TrainingAccess access, CancellationToken ct) =>
        {
            var request = Read<TrainingCertificationRequest>(payload,
                ["workerId", "certificationTypeCode", "completionId", "supervisorSignOff", "signOffReference"], out var error);
            if (error is not null) return Results.BadRequest(new { error });
            var result = await store.GrantAsync(request!, Key(http.Request), access.Identity(http), ct);
            return result.NotFound ? Results.NotFound(new { error = result.Error })
                : result.Conflict ? Results.Conflict(new { error = result.Error })
                : result.Error is not null ? Results.BadRequest(new { error = result.Error })
                : result.Created ? Results.Created($"/api/v1/training/workers/{request!.WorkerId}/qualifications", new { grant = GrantView(result.Record!), replayed = false })
                : Results.Ok(new { grant = GrantView(result.Record!), replayed = true });
        }).WithMetadata(new RequestSizeLimitAttribute(8192));
        group.MapGet("/stream", async (HttpContext http, CaseEventStream events) =>
        {
            http.Response.ContentType = "text/event-stream"; http.Response.Headers.CacheControl = "no-store";
            var subscription = events.Subscribe(trainingOnly: true);
            try
            {
                await http.Response.WriteAsync("event: ready\ndata: {}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
                await foreach (var change in subscription.Reader.ReadAllAsync(http.RequestAborted))
                {
                    await http.Response.WriteAsync($"event: {change.Type}\ndata: {{}}\n\n", http.RequestAborted);
                    await http.Response.Body.FlushAsync(http.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
            finally { events.Unsubscribe(subscription.Id); }
        });
        app.MapGet("/api/training/audit", async (TrainingStore store, CancellationToken ct) => Results.Ok(await store.AuditAsync(ct)))
            .WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
        app.MapGet("/api/training/audit/verify", async (TrainingStore store, CancellationToken ct) => Results.Ok(await store.VerifyAsync(ct)))
            .WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
        return app;
    }

    private static IResult Catalogue(HttpContext http, WorkOrderCatalog catalog, object payload)
    {
        http.Response.Headers.ETag = catalog.ETag;
        http.Response.Headers["X-CivicOps-Catalog-Version"] = catalog.Version;
        return http.Request.Headers.IfNoneMatch.Any(header => header?.Split(',').Any(value => value.Trim() == catalog.ETag || value.Trim() == "*") == true)
            ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Ok(payload);
    }

    private static T? Read<T>(JsonElement payload, string[] required, out string? error)
    {
        error = null;
        if (payload.ValueKind != JsonValueKind.Object) { error = "A JSON object is required."; return default; }
        var properties = payload.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Length != required.Length || properties.Distinct(StringComparer.Ordinal).Count() != properties.Length
            || required.Any(field => !properties.Contains(field, StringComparer.Ordinal)))
        { error = "Provide every schema field exactly once; additional fields are not accepted."; return default; }
        try { return payload.Deserialize<T>(StrictJson); }
        catch (JsonException) { error = "Payload types do not conform to the training JSON schema."; return default; }
    }

    private static object CompletionView(TrainingCompletion record) => new { record.Id, record.Record, record.Client, record.RecordedAt };
    private static object GrantView(TrainingCertificationGrant record) => new { record.Id, record.Record, record.Certification, record.Client, record.RecordedAt };
    private static string Key(HttpRequest request) => request.Headers["Idempotency-Key"].Count == 1 ? request.Headers["Idempotency-Key"].ToString() : "";
}
