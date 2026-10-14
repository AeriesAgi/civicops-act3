using System.Text.Json;
using System.Text.Json.Serialization;
using CivicOps.Models;

namespace CivicOps.Services;

// Durable file queue or shared PostgreSQL claims, with advisory leadership for hosted polling.
public sealed class IntegrationOutbox(ZendeskClient client, ZendeskOptions options, IncidentStore store, TimeProvider clock, DurableStorage? persistence = null) : IDisposable
{
    private readonly DurableStorage _storage = persistence ?? store.Persistence;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly Dictionary<string, List<IntegrationOutboxItem>> _partitions = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _pendingSnapshots = new(StringComparer.Ordinal);
    public int CachedPendingBacklog => _pendingSnapshots.Values.Sum();
    public async Task<int> ReadPendingSnapshotAsync(CancellationToken ct)
    {
        // Atomic file replacement allows a readiness read without waiting on a slow provider's queue gate.
        var persisted = await _storage.ReadAsync(FilePath, ct);
        if (persisted is null) return 0;
        var items = JsonSerializer.Deserialize<List<IntegrationOutboxItem>>(persisted, _json) ?? [];
        if (items.Any(item => item.TenantId is not null && item.TenantId != TenantContext.CurrentId))
            throw new InvalidDataException("Integration partition municipality mismatch.");
        return items.Count(item => item.State == IntegrationDeliveryState.Pending);
    }
    private List<IntegrationOutboxItem>? _items
    { get => _partitions.GetValueOrDefault(TenantContext.CurrentId); set { if (value is not null) _partitions[TenantContext.CurrentId] = value; } }
    private string FilePath => Path.Combine(TenantContext.DirectoryFor(options.DataDirectory), "integration-outbox.json");

    public async Task<IntegrationOutboxItem> EnqueueAsync(Incident incident, string operation, CancellationToken ct)
    {
        if (operation is not ("create" or "status" or "approved-comment"))
            throw new ArgumentException("Allowed Zendesk operations: create, status, approved-comment.", nameof(operation));
        var approved = operation == "approved-comment" ? incident.CommsDrafts.LastOrDefault(x => x.State == "Approved"
            && x.Text == incident.PublicUpdate && x.ApprovedAt is not null && !string.IsNullOrWhiteSpace(x.ApprovedBy)) : null;
        var key = operation switch
        {
            "create" => $"{incident.Id}:create",
            "status" => StatusKey(incident),
            _ => approved is not null ? $"{incident.Id}:comms:{approved.Id}" : $"{incident.Id}:{incident.Version}:{operation}"
        };
        return await EnqueueIntentAsync(incident, operation, key, approved?.Text, ct);
    }

    public Task<IntegrationOutboxItem> EnqueueApprovedDraftAsync(Incident incident, CommsDraft draft, CancellationToken ct)
    {
        var recorded = incident.CommsDrafts.FirstOrDefault(x => x.Id == draft.Id);
        if (recorded is null || recorded.State != "Approved" || recorded.ApprovedAt is null || string.IsNullOrWhiteSpace(recorded.ApprovedBy))
            throw new ArgumentException("A recorded human-approved draft is required.", nameof(draft));
        return EnqueueIntentAsync(incident, "approved-comment", $"{incident.Id}:comms:{recorded.Id}", recorded.Text, ct);
    }

    private async Task<IntegrationOutboxItem> EnqueueIntentAsync(Incident incident, string operation, string key,
        string? approvedText, CancellationToken ct)
    {
        if (incident.TenantId is not null && incident.TenantId != TenantContext.CurrentId)
            throw new InvalidOperationException("Cannot enqueue another municipality case.");
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var items = await LoadAsync(ct);
            var existing = items.FirstOrDefault(x => x.DeduplicationKey == key);
            if (existing is not null) return Clone(existing);
            if (items.Count(x => x.State == IntegrationDeliveryState.Pending) >= 10_000)
                throw new InvalidOperationException("Case record queue is full; dispatcher review required.");
            var item = new IntegrationOutboxItem
            {
                TenantId = TenantContext.CurrentId, DeduplicationKey = key, IncidentId = incident.Id, CaseReference = incident.ReferenceNumber,
                Operation = operation, CreatedAt = clock.GetUtcNow(), NextAttemptAt = clock.GetUtcNow()
            };
            item.RequestJson = ZendeskClient.BuildTicketRequest(incident, operation, item.Id, approvedText);
            var next = items.Append(item).ToList();
            await SaveAsync(next, ct);
            return Clone(item);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<IntegrationOutboxItem>> ListAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Select(Clone).ToArray(); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public async Task<IntegrationOutboxStatus> GetStatusAsync(CancellationToken ct)
    {
        var items = await ListAsync(ct);
        return new(client.Mode, items.Count(x => x.State == IntegrationDeliveryState.Pending),
            items.Count(x => x.State == IntegrationDeliveryState.Delivered), items.Count(x => x.State == IntegrationDeliveryState.DeadLetter));
    }

    public async Task<int> ReconcileMissingCreatesAsync(CancellationToken ct)
    {
        // Recover a crash after the incident snapshot committed but before create delivery was enqueued.
        var known = (await ListAsync(ct)).Select(x => x.DeduplicationKey).ToHashSet(StringComparer.Ordinal);
        var recovered = 0;
        foreach (var incident in await store.ListAsync(ct))
        {
            if (incident.PrivacyAnonymised) continue;
            if (known.Contains($"{incident.Id}:create")) continue;
            await EnqueueAsync(incident, "create", ct);
            recovered++;
        }
        return recovered;
    }

    public async Task<int> ReconcileMissingIntentsAsync(CancellationToken ct)
    {
        var recovered = await ReconcileMissingCreatesAsync(ct);
        var known = (await ListAsync(ct)).Select(x => x.DeduplicationKey).ToHashSet(StringComparer.Ordinal);
        foreach (var incident in await store.ListAsync(ct))
        {
            if (incident.PrivacyAnonymised) continue;
            if (DomainEventIndex(incident) >= 0 && !known.Contains(StatusKey(incident)))
            { await EnqueueAsync(incident, "status", ct); recovered++; }
            foreach (var draft in incident.CommsDrafts.Where(x => x.State == "Approved"
                && x.ApprovedAt is not null && !string.IsNullOrWhiteSpace(x.ApprovedBy)))
            {
                var key = $"{incident.Id}:comms:{draft.Id}";
                if (known.Contains(key)) continue;
                await EnqueueApprovedDraftAsync(incident, draft, ct); recovered++;
            }
        }
        return recovered;
    }

    private static int DomainEventIndex(Incident incident) => incident.Audit.FindLastIndex(x => x.Action is "approved" or "assigned" or "status");
    private static string StatusKey(Incident incident) => DomainEventIndex(incident) is var index && index >= 0
        ? $"{incident.Id}:status:{index}" : $"{incident.Id}:{incident.Version}:status";

    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        if (_storage.IsPostgres) return await ProcessPostgresDueAsync(ct);
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var items = (await LoadAsync(ct)).Select(Clone).ToList();
            var processed = 0;
            foreach (var item in items.Where(x => x.State == IntegrationDeliveryState.Pending && x.NextAttemptAt <= clock.GetUtcNow()).ToArray())
            {
                // Preserve case order: a failed earlier operation blocks later updates on that case.
                if (items.TakeWhile(x => x.Id != item.Id).Any(x => x.IncidentId == item.IncidentId
                    && x.State is IntegrationDeliveryState.Pending or IntegrationDeliveryState.DeadLetter)) continue;
                ct.ThrowIfCancellationRequested();
                var incident = await store.GetAsync(item.IncidentId, ct);
                if (incident is null)
                {
                    item.State = IntegrationDeliveryState.DeadLetter;
                    item.LastError = "Case no longer exists; dispatcher review required.";
                    await SaveAsync(items, ct);
                    continue;
                }
                item.Attempts++;
                // Checkpoint before calling an external service: a restart must use retry/dedupe semantics.
                item.NextAttemptAt = clock.GetUtcNow() + RetryDelay(item.Attempts);
                await SaveAsync(items, ct);
                try
                {
                    using var deliveryActivity = CivicOpsTelemetry.Activities.StartActivity("outbox.delivery");
                    deliveryActivity?.SetTag("outbox.operation", item.Operation);
                    deliveryActivity?.SetTag("outbox.mode", client.Mode);
                    CivicOpsTelemetry.OutboxAttempts.Add(1, new KeyValuePair<string, object?>("mode", client.Mode));
                    var delivered = await client.DeliverAsync(item, incident.ZendeskTicketId, ct);
                    CivicOpsTelemetry.OutboxDeliveryLatency.Record(Math.Max(0, (clock.GetUtcNow() - item.CreatedAt).TotalSeconds), new KeyValuePair<string, object?>("mode", client.Mode));
                    await store.ChangeAsync(item.IncidentId, null, candidate =>
                    {
                        if (candidate.Audit.Any(e => DeliveryReceipt.IsReceipt(e.Action) && e.Detail.Contains(item.Id, StringComparison.Ordinal)))
                            return "Delivery already recorded.";
                        candidate.ZendeskTicketId = delivered.TicketId;
                        candidate.ZendeskTicketUrl = delivered.TicketUrl;
                        candidate.Audit.Add(new(DeliveryReceipt.Action, DeliveryReceipt.Actor,
                            $"{client.Mode}: {item.Operation}; ticket {delivered.TicketId}; event {item.Id}.", clock.GetUtcNow()));
                        return null;
                    }, ct);
                    item.TicketId = delivered.TicketId;
                    item.State = IntegrationDeliveryState.Delivered;
                    item.DeliveredAt = clock.GetUtcNow();
                    item.LastError = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or IOException)
                {
                    // Never persist response bodies, exception text, URLs, auth headers, or resident data.
                    item.LastError = error is HttpRequestException { StatusCode: not null } httpError
                        ? $"Zendesk HTTP {(int)httpError.StatusCode.Value}." : "Zendesk delivery failed or timed out.";
                    if (item.Attempts >= options.MaximumAttempts || PermanentFailure(error))
                        item.State = IntegrationDeliveryState.DeadLetter;
                }
                await SaveAsync(items, ct);
                processed++;
            }
            return processed;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    // Commit a claim before delivery. No snapshot SQL transaction/write lock is held during a provider call.
    private async Task<int> ProcessPostgresDueAsync(CancellationToken ct)
    {
        var processed = 0;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var claim = await _storage.RunAsync(async () =>
            {
                var items = (await LoadAsync(ct)).Select(Clone).ToList();
                var item = items.FirstOrDefault(candidate => candidate.State == IntegrationDeliveryState.Pending && candidate.NextAttemptAt <= clock.GetUtcNow()
                    && !items.TakeWhile(prior => prior.Id != candidate.Id).Any(prior => prior.IncidentId == candidate.IncidentId
                        && prior.State is IntegrationDeliveryState.Pending or IntegrationDeliveryState.DeadLetter));
                if (item is null) return ((IntegrationOutboxItem?)null, (Incident?)null);
                var incident = await store.GetAsync(item.IncidentId, ct);
                if (incident is null)
                { item.State = IntegrationDeliveryState.DeadLetter; item.LastError = "Case no longer exists; dispatcher review required."; await SaveAsync(items, ct); return (item, (Incident?)null); }
                item.Attempts++;
                var retry = RetryDelay(item.Attempts);
                var lease = options.RequestTimeout + TimeSpan.FromSeconds(10);
                item.NextAttemptAt = clock.GetUtcNow() + (retry > lease ? retry : lease);
                await SaveAsync(items, ct);
                return ((IntegrationOutboxItem?)Clone(item), (Incident?)incident);
            }, ct);
            if (claim.Item1 is null) break;
            var claimed = claim.Item1;
            if (claim.Item2 is null) { processed++; continue; }
            ZendeskDelivery? delivered = null; Exception? failure = null;
            try
            {
                using var deliveryActivity = CivicOpsTelemetry.Activities.StartActivity("outbox.delivery");
                deliveryActivity?.SetTag("outbox.operation", claimed.Operation);
                deliveryActivity?.SetTag("outbox.mode", client.Mode);
                CivicOpsTelemetry.OutboxAttempts.Add(1, new KeyValuePair<string, object?>("mode", client.Mode));
                delivered = await client.DeliverAsync(claimed, claim.Item2.ZendeskTicketId, ct);
                CivicOpsTelemetry.OutboxDeliveryLatency.Record(Math.Max(0, (clock.GetUtcNow() - claimed.CreatedAt).TotalSeconds), new KeyValuePair<string, object?>("mode", client.Mode));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or IOException)
            { failure = error; }
            await _storage.RunAsync(async () =>
            {
                var items = (await LoadAsync(ct)).Select(Clone).ToList();
                var item = items.SingleOrDefault(candidate => candidate.Id == claimed.Id);
                // An expired lease or privacy request may already have superseded this claim.
                if (item is null || item.Attempts != claimed.Attempts || item.State != IntegrationDeliveryState.Pending) return;
                if (delivered is not null)
                {
                    await store.ChangeAsync(item.IncidentId, null, candidate =>
                    {
                        if (candidate.Audit.Any(entry => DeliveryReceipt.IsReceipt(entry.Action) && entry.Detail.Contains(item.Id, StringComparison.Ordinal))) return "Delivery already recorded.";
                        candidate.ZendeskTicketId = delivered.TicketId; candidate.ZendeskTicketUrl = delivered.TicketUrl;
                        candidate.Audit.Add(new(DeliveryReceipt.Action, DeliveryReceipt.Actor, $"{client.Mode}: {item.Operation}; ticket {delivered.TicketId}; event {item.Id}.", clock.GetUtcNow())); return null;
                    }, ct);
                    item.TicketId = delivered.TicketId; item.State = IntegrationDeliveryState.Delivered;
                    item.DeliveredAt = clock.GetUtcNow(); item.LastError = null;
                }
                else
                {
                    item.LastError = failure is HttpRequestException { StatusCode: not null } httpError ? $"Zendesk HTTP {(int)httpError.StatusCode.Value}." : "Zendesk delivery failed or timed out.";
                    item.NextAttemptAt = clock.GetUtcNow() + RetryDelay(item.Attempts);
                    if (item.Attempts >= options.MaximumAttempts || PermanentFailure(failure!)) item.State = IntegrationDeliveryState.DeadLetter;
                }
                await SaveAsync(items, ct);
            }, ct);
            processed++;
        }
        return processed;
    }

    public async Task<bool> RetryDeadLetterAsync(string id, CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var items = (await LoadAsync(ct)).Select(Clone).ToList();
            var item = items.FirstOrDefault(x => x.Id == id && x.State == IntegrationDeliveryState.DeadLetter);
            if (item is null || item.LastError == "Privacy anonymisation suppresses further delivery.") return false;
            item.State = IntegrationDeliveryState.Pending;
            // Keep Attempts > 0 so lost responses are still checked against event markers.
            item.NextAttemptAt = clock.GetUtcNow();
            await SaveAsync(items, ct);
            return true;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }

    public TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(Math.Min(3_600_000,
        options.RetryBase.TotalMilliseconds * Math.Pow(2, Math.Min(20, Math.Max(0, attempt - 1)))));
    public TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(Math.Min(3_600_000,
        Backoff(attempt).TotalMilliseconds * (1 + Random.Shared.NextDouble() * .2)));
    public async Task<int> ErasePersonalDataAsync(string incidentId, bool suppressDelivery, CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var items = (await LoadAsync(ct)).Select(Clone).ToList(); var count = 0;
            foreach (var item in items.Where(item => item.IncidentId == incidentId))
            {
                var request = System.Text.Json.Nodes.JsonNode.Parse(item.RequestJson);
                if (request?["ticket"] is System.Text.Json.Nodes.JsonObject ticket) ticket.Remove("requester");
                item.RequestJson = suppressDelivery ? "{\"privacy\":\"request payload erased after verified anonymisation\"}"
                    : PiiRedactor.RedactJson(JsonSerializer.SerializeToElement(request)).GetRawText();
                if (suppressDelivery && item.State != IntegrationDeliveryState.Delivered)
                {
                    item.State = IntegrationDeliveryState.DeadLetter;
                    item.LastError = "Privacy anonymisation suppresses further delivery.";
                }
                count++;
            }
            await SaveAsync(items, ct); return count;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private static bool PermanentFailure(Exception error) => error is HttpRequestException { StatusCode: not null } http
        && (int)http.StatusCode.Value is >= 400 and < 500 and not (408 or 409 or 429);
    private async Task<List<IntegrationOutboxItem>> LoadAsync(CancellationToken ct)
    {
        if (!_storage.IsPostgres && _items is not null) return _items;
        var persisted = await _storage.ReadAsync(FilePath, ct);
        if (persisted is null) { _pendingSnapshots[TenantContext.CurrentId] = 0; return _items = []; }
        var items = JsonSerializer.Deserialize<List<IntegrationOutboxItem>>(persisted, _json) ?? [];
        if (items.Any(item => item.TenantId is not null && item.TenantId != TenantContext.CurrentId)
            || TenantContext.CurrentId != TenantStore.DefaultTenantId && items.Any(item => item.TenantId is null))
            throw new InvalidDataException("Integration partition municipality mismatch.");
        foreach (var item in items) item.TenantId ??= TenantContext.CurrentId;
        _pendingSnapshots[TenantContext.CurrentId] = items.Count(item => item.State == IntegrationDeliveryState.Pending);
        return _items = items;
    }
    private async Task SaveAsync(List<IntegrationOutboxItem> items, CancellationToken ct)
    {
        await _storage.WriteAsync(FilePath, JsonSerializer.Serialize(items, _json), ct);
        _items = items.Select(Clone).ToList(); // Publish a snapshot only after the durable write succeeds.
        _pendingSnapshots[TenantContext.CurrentId] = items.Count(item => item.State == IntegrationDeliveryState.Pending);
    }
    private IntegrationOutboxItem Clone(IntegrationOutboxItem item) =>
        JsonSerializer.Deserialize<IntegrationOutboxItem>(JsonSerializer.Serialize(item, _json), _json)!;
    public void Dispose() => _gate.Dispose();
}

public sealed class IntegrationOutboxWorker(IntegrationOutbox outbox, ZendeskOptions options,
    TimeProvider clock, ILogger<IntegrationOutboxWorker> logger, TenantStore? tenants = null,
    DurableStorage? storage = null) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> StorageUnavailable = LoggerMessage.Define(LogLevel.Warning,
        new EventId(4201, "OutboxStorageUnavailable"), "Integration outbox storage unavailable; retrying.");
    private static readonly Action<ILogger, Exception?> DataMalformed = LoggerMessage.Define(LogLevel.Error,
        new EventId(4202, "OutboxDataMalformed"), "Integration outbox data is malformed; restore a verified backup.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastReconciliation = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var leadership = storage is null ? null : await storage.TryAcquireOutboxLeadershipAsync(stoppingToken);
                if (storage is not null && leadership is null)
                {
                    await Task.Delay(options.PollInterval, clock, stoppingToken);
                    continue;
                }
                var reconcile = lastReconciliation is null || clock.GetUtcNow() - lastReconciliation > TimeSpan.FromSeconds(5);
                var ids = tenants is null ? new[] { TenantContext.CurrentId }
                    : (await tenants.ListAsync(stoppingToken)).Select(tenant => tenant.Id).ToArray();
                foreach (var id in ids)
                {
                    using var lease = TenantContext.Use(id);
                    if (reconcile) await outbox.ReconcileMissingIntentsAsync(stoppingToken);
                    await outbox.ProcessDueAsync(stoppingToken);
                }
                if (reconcile) lastReconciliation = clock.GetUtcNow();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (IOException) { StorageUnavailable(logger, null); }
            catch (Npgsql.NpgsqlException) { StorageUnavailable(logger, null); }
            catch (TimeoutException) { StorageUnavailable(logger, null); }
            catch (InvalidOperationException) { StorageUnavailable(logger, null); }
            catch (JsonException) { DataMalformed(logger, null); }
            await Task.Delay(options.PollInterval, clock, stoppingToken);
        }
    }
}

/// <summary>User-visible case-record receipt labels. Legacy stored values stay valid for existing chains.</summary>
internal static class DeliveryReceipt
{
    public const string Action = "case-record-synced";
    public const string Actor = "CivicOps case record agent";
    public const string LegacyAction = "zendesk-delivered";
    public const string LegacyActor = "Zendesk integration";
    public static bool IsReceipt(string action) => action is Action or LegacyAction;
    public static bool IsReceipt(string action, string actor) => action == Action && actor == Actor || action == LegacyAction && actor == LegacyActor;
}
