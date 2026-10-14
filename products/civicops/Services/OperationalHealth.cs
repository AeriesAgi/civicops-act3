using System.Diagnostics.Metrics;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed class OperationalSignalsWorker(TenantStore tenants, IncidentStore incidents, IntegrationOutbox outbox,
    TimeProvider clock, ILogger<OperationalSignalsWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> MetricsUnavailable = LoggerMessage.Define(LogLevel.Warning,
        new EventId(4602, "MetricsUnavailable"), "Operational storage metrics are unavailable; check durable storage.");
    private readonly Meter _meter = new(CivicOpsTelemetry.Name, "1.0.0");
    private long _backlog;
    private long _breaches;
    private long _integrityFailures;
    private double _oldest;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _meter.CreateObservableGauge("civicops.outbox.backlog", () => Interlocked.Read(ref _backlog), "{delivery}");
        _meter.CreateObservableGauge("civicops.sla.breaches", () => Interlocked.Read(ref _breaches), "{case}", "Open cases beyond the catalogue review target.");
        _meter.CreateObservableGauge("civicops.outbox.oldest_age", () => _oldest, "s");
        _meter.CreateObservableGauge("civicops.audit.integrity_failures", () => Interlocked.Read(ref _integrityFailures), "{failure}");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long backlog = 0, breaches = 0, integrityFailures = 0; double oldest = 0;
                foreach (var tenant in await tenants.ListAsync(stoppingToken))
                {
                    using var lease = TenantContext.Use(tenant.Id);
                    var cases = await incidents.ListAsync(stoppingToken);
                    breaches += cases.Count(incident => incident.Status is not (IncidentStatus.Resolved or IncidentStatus.Closed)
                        && incident.CreatedAt.AddMinutes(WorkOrderCatalog.Default.Find(incident.WorkOrderTypeCode ?? "")?.SlaTargetMinutes ?? 1440) < clock.GetUtcNow());
                    var items = await outbox.ListAsync(stoppingToken);
                    backlog += items.Count(item => item.State == IntegrationDeliveryState.Pending);
                    oldest = Math.Max(oldest, items.Where(item => item.State == IntegrationDeliveryState.Pending)
                        .Select(item => Math.Max(0, (clock.GetUtcNow() - item.CreatedAt).TotalSeconds)).DefaultIfEmpty(0).Max());
                    if (!(await incidents.VerifyAuditAsync(stoppingToken)).Valid) integrityFailures++;
                }
                Interlocked.Exchange(ref _backlog, backlog); Interlocked.Exchange(ref _breaches, breaches);
                Interlocked.Exchange(ref _integrityFailures, integrityFailures); _oldest = oldest;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidDataException or Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
            { MetricsUnavailable(logger, null); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    public override void Dispose() { _meter.Dispose(); base.Dispose(); }
}

public static class OperationalHealth
{
    public static bool IsGlobalProbe(HttpContext context) => HttpMethods.IsGet(context.Request.Method)
        && !context.Request.PathBase.HasValue && context.Request.Path.Value is "/health/live" or "/health/ready" or "/healthz";
    public static IEndpointRouteBuilder MapCivicOpsHealth(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/health/live", () => Results.Ok(new { status = "healthy", service = "CivicOps" }));
        routes.MapGet("/health/ready", async (DurableStorage storage, TenantStore tenants, IntegrationOutbox outbox, GeoStore geo, CrewStore crews, ChannelStore channels, AiReasoningClient ai,
            ElevenLabsClient voice, ZendeskClient zendesk, IConfiguration configuration, CancellationToken ct) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var accessible = false;
            try { accessible = await storage.HealthCheckAsync(timeout.Token); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or Npgsql.NpgsqlException) { }
            var migrations = storage.MigrationsReady;
            var maximum = int.TryParse(configuration["CIVICOPS_OUTBOX_READY_MAX"], out var threshold) ? Math.Clamp(threshold, 0, 10_000) : 1_000;
            var pending = outbox.CachedPendingBacklog;
            var geoStatus = "unavailable";
            var crewStatus = "unavailable";
            var channelStatus = "unavailable";
            if (accessible)
            {
                try
                {
                    var municipalities = await tenants.ListAsync(timeout.Token);
                    if (storage.IsPostgres) pending = await storage.PendingOutboxCountAsync(timeout.Token);
                    else
                    {
                        pending = 0;
                        foreach (var tenant in municipalities)
                        {
                            using var lease = TenantContext.Use(tenant.Id);
                            pending += await outbox.ReadPendingSnapshotAsync(timeout.Token);
                        }
                    }
                    if (migrations)
                    {
                        try
                        {
                            foreach (var tenant in municipalities)
                            {
                                using var lease = TenantContext.Use(tenant.Id);
                                _ = await geo.ReadAsync(timeout.Token);
                            }
                            geoStatus = "healthy";
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
                        { geoStatus = "unhealthy"; }
                        try
                        {
                            foreach (var tenant in municipalities)
                            {
                                using var lease = TenantContext.Use(tenant.Id);
                                // PolicyAsync validates the complete shift/consent/device
                                // state and its audit chain without granting or logging access.
                                _ = await crews.PolicyAsync(timeout.Token);
                            }
                            crewStatus = "healthy";
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
                        { crewStatus = "unhealthy"; }
                        try
                        {
                            foreach (var tenant in municipalities)
                            {
                                using var lease = TenantContext.Use(tenant.Id);
                                await channels.VerifyIntegrityAsync(timeout.Token);
                            }
                            channelStatus = "healthy";
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
                        { channelStatus = "unhealthy"; }
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException or Npgsql.NpgsqlException or TimeoutException or InvalidOperationException) { accessible = false; }
            }
            var providers = new { ai = ai.Health, elevenlabs = voice.Health, zendesk = zendesk.Health };
            var ready = accessible && migrations && geoStatus == "healthy" && crewStatus == "healthy" && channelStatus == "healthy" && pending <= maximum;
            var degraded = providers.ai.Status == "degraded" || providers.elevenlabs.Status == "degraded" || providers.zendesk.Status == "degraded";
            return Results.Json(new { status = !ready ? "unhealthy" : degraded ? "degraded" : "healthy", ready,
                checks = new { storage = accessible ? "healthy" : "unhealthy", migrations = migrations ? "healthy" : "unhealthy",
                    outbox = pending <= maximum ? "healthy" : "unhealthy", geo = geoStatus, crew = crewStatus, channels = channelStatus }, outbox = new { pending, threshold = maximum }, providers }, statusCode: ready ? 200 : 503);
        });
        return routes;
    }
}
