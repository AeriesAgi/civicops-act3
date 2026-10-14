using CivicOps.Models;

namespace CivicOps.Services;

public sealed class RetentionService(IncidentStore store, IntegrationOutbox outbox, IConfiguration configuration, TimeProvider time,
    ILogger<RetentionService> logger, TenantStore? tenants = null, EmailNotificationService? email = null) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> PurgeFailed = LoggerMessage.Define(LogLevel.Error,
        new EventId(4301, "RetentionPurgeFailed"), "Closed-case retention purge failed; inspect verified storage state.");
    public int? Days => int.TryParse(configuration["CIVICOPS_RETENTION_DAYS"], out var n) && n is >= 1 and <= 36500 ? n : null;
    public int? PiiDays => int.TryParse(configuration["CIVICOPS_PII_RETENTION_DAYS"], out var n) && n is >= 1 and <= 36500 ? n : null;
    public async Task<object> PurgeAsync(CancellationToken ct)
    {
        if (Days is null && PiiDays is null) return new { enabled = false, purged = 0, piiPurged = 0 };
        var protectedIds = (await outbox.ListAsync(ct)).Where(item => item.State != IntegrationDeliveryState.Delivered
            && item.LastError != "Privacy anonymisation suppresses further delivery.").Select(item => item.IncidentId).ToHashSet(StringComparer.Ordinal);
        var candidates = (await store.ListAsync(ct)).Where(incident => incident.Status == IncidentStatus.Closed && !protectedIds.Contains(incident.Id)).ToArray();
        var piiPurged = PiiDays is int piiDays ? await store.PurgeContactFieldsAsync(time.GetUtcNow().AddDays(-piiDays), ct, protectedIds) : 0;
        var purged = Days is int days ? await store.PurgeClosedAsync(time.GetUtcNow().AddDays(-days), ct, protectedIds) : 0;
        foreach (var incident in candidates)
        {
            var current = await store.GetAsync(incident.Id, ct);
            if (current is not null && current.ContactRecordHash is not null) continue;
            await outbox.ErasePersonalDataAsync(incident.Id, false, ct);
            if (email is not null) await email.EraseCaseOutboxAsync(incident.ReferenceNumber, ct);
        }
        return new { enabled = true, purged, piiPurged, retained = "Immutable audit, provider-held records and backups require separate Information Officer review; no external deletion is attempted." };
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Days is null && PiiDays is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ids = tenants is null ? new[] { TenantContext.CurrentId }
                    : (await tenants.ListAsync(stoppingToken)).Select(tenant => tenant.Id).ToArray();
                foreach (var id in ids) { using var lease = TenantContext.Use(id); await PurgeAsync(stoppingToken); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { PurgeFailed(logger, null); }
            try { await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
