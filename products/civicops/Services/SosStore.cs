using System.Globalization;
using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

/// <summary>Durable, tenant-bound SOS queue. It records alerts and human decisions only; it never contacts responders.</summary>
public sealed class SosStore : IDisposable
{
    public const string EmergencyNotice = "CivicOps SOS alerts your municipality's control room. In a life-threatening emergency always call 10111 (police), 10177 (ambulance/fire) or 112 from a cell phone.";
    private const string FileName = "sos-alerts.json";
    private const string OwnersFileName = "sos-owner-capabilities.json";
    private const string AccessFileName = "sos-access-log.json";
    private readonly DurableStorage storage;
    private readonly IConfiguration configuration;
    private readonly IWebHostEnvironment environment;
    private readonly TimeProvider time;
    private readonly CaseEventStream events;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly int windowMinutes;
    private readonly int ackSeconds;
    private readonly int cancelSeconds;

    public SosStore(IConfiguration configuration, IWebHostEnvironment environment, DurableStorage storage, TimeProvider time, CaseEventStream events)
    {
        this.configuration = configuration; this.environment = environment; this.storage = storage; this.time = time; this.events = events;
        windowMinutes = Bounded(configuration["CIVICOPS_SOS_LOCATION_WINDOW_MINUTES"], 30, 1, 120, "CIVICOPS_SOS_LOCATION_WINDOW_MINUTES");
        ackSeconds = Bounded(configuration["CIVICOPS_SOS_ACK_TARGET_SECONDS"], 60, 10, 3600, "CIVICOPS_SOS_ACK_TARGET_SECONDS");
        cancelSeconds = Bounded(configuration["CIVICOPS_SOS_CANCEL_WINDOW_SECONDS"], 10, 3, 60, "CIVICOPS_SOS_CANCEL_WINDOW_SECONDS");
    }
    private static int Bounded(string? value, int fallback, int min, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
            throw new InvalidOperationException($"{name} must be an integer between {min} and {max}.");
        return number;
    }
    private string Path => System.IO.Path.Combine(TenantContext.DirectoryFor(DataPaths.Resolve(configuration, environment)), FileName);
    private async Task<List<SosAlert>> ReadAsync(CancellationToken ct)
    {
        var payload = await storage.ReadAsync(Path, ct);
        return string.IsNullOrWhiteSpace(payload) ? [] : JsonSerializer.Deserialize<List<SosAlert>>(payload, json) ?? [];
    }
    private Task SaveAsync(List<SosAlert> values, CancellationToken ct) => storage.WriteAsync(Path, JsonSerializer.Serialize(values, json), ct);
    private string OwnersPath => System.IO.Path.Combine(TenantContext.DirectoryFor(DataPaths.Resolve(configuration, environment)), OwnersFileName);
    private string AccessPath => System.IO.Path.Combine(TenantContext.DirectoryFor(DataPaths.Resolve(configuration, environment)), AccessFileName);
    private async Task<Dictionary<string, string>> ReadOwnersAsync(CancellationToken ct)
    {
        var payload = await storage.ReadAsync(OwnersPath, ct);
        return string.IsNullOrWhiteSpace(payload)
            ? new(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(payload, json) ?? new(StringComparer.Ordinal);
    }
    private Task SaveOwnersAsync(Dictionary<string, string> values, CancellationToken ct) =>
        storage.WriteAsync(OwnersPath, JsonSerializer.Serialize(values, json), ct);
    private sealed record SosAccessRecord(string Id, string Action, string Actor, DateTimeOffset At);
    private async Task RecordAccessAsync(IEnumerable<string> ids, string action, string actor, CancellationToken ct)
    {
        var payload = await storage.ReadAsync(AccessPath, ct);
        var values = string.IsNullOrWhiteSpace(payload)
            ? new List<SosAccessRecord>()
            : JsonSerializer.Deserialize<List<SosAccessRecord>>(payload, json) ?? [];
        var now = time.GetUtcNow();
        foreach (var id in ids.Take(64)) values.Add(new SosAccessRecord(id, action, actor, now));
        // Access records are operational evidence, bounded to avoid an unbounded append-only file.
        if (values.Count > 10_000) values = values[^10_000..];
        await storage.WriteAsync(AccessPath, JsonSerializer.Serialize(values, json), ct);
    }
    private static string HashContact(string? contact) => string.IsNullOrWhiteSpace(contact) ? "anonymous" : IncidentStore.Hash(TenantContext.CurrentId + ":contact:" + contact.Trim().ToLowerInvariant());
    private static void Text(string? value, string name, int max, int min = 0)
    { if (RequestValidation.Text(value, name, max, min) is { } error) throw new SosRequestException(400, error); }
    private static string Operation(string operationId) { Text(operationId, "operationId", 128, 8); return operationId; }
    private static string RequestHash<T>(T request) => IncidentStore.Hash(JsonSerializer.Serialize(request));
    private static string OwnerHash(string ownerKey) => IncidentStore.Hash(TenantContext.CurrentId + ":sos-owner:" + ownerKey);
    private static bool Active(SosAlert alert, DateTimeOffset now) => alert.Status is not (SosStatus.Resolved or SosStatus.FalseAlarm or SosStatus.Cancelled) && alert.FollowUpUntil > now;
    private static bool Terminal(SosStatus status) => status is SosStatus.Resolved or SosStatus.FalseAlarm or SosStatus.Cancelled;

    public async Task<SosResult> CreateAsync(SosCreateRequest request, string? staffName, string? crewName, CancellationToken ct, string? ownerKey = null)
    {
        Operation(request.OperationId); Text(request.Channel, "channel", 32, 2); Text(request.Note, "note", 2000);
        Text(request.Contact, "contact", 320); Text(request.AddressOrLandmark, "addressOrLandmark", 240);
        if (request.Location is not null && !GeoGeofencing.IsValidPoint(request.Location)) throw new SosRequestException(400, "Location is outside the supported geographic envelope.");
        if (request.AccuracyMeters is < 0 or > 10000 || request.AccuracyMeters is double.NaN or double.PositiveInfinity or double.NegativeInfinity) throw new SosRequestException(400, "Accuracy must be a finite bounded value.");
        if (request.BatteryPercent is < 0 or > 100) throw new SosRequestException(400, "Battery percentage must be between 0 and 100.");
        var requestHash = RequestHash(request);
        await gate.WaitAsync(ct);
        try
        {
            var values = await ReadAsync(ct);
            var existing = values.FirstOrDefault(x => x.TenantId == TenantContext.CurrentId && x.Audit.Any(a => a.Action == "triggered" && a.Note == "operation:" + request.OperationId + ":" + requestHash));
            var conflicting = values.Any(x => x.TenantId == TenantContext.CurrentId && x.Audit.Any(a => a.Action == "triggered" && a.Note?.StartsWith("operation:" + request.OperationId + ":", StringComparison.Ordinal) is true));
            if (conflicting && existing is null) throw new SosRequestException(409, "This SOS operation key already belongs to another request.");
            if (existing is not null) return new(existing, true, existing.Status == SosStatus.AwaitingLocation, "This SOS operation was already recorded.", EmergencyNotice);
            var now = time.GetUtcNow(); var needs = request.Location is null && string.IsNullOrWhiteSpace(request.AddressOrLandmark);
            var status = needs ? SosStatus.AwaitingLocation : SosStatus.Triggered;
            var id = "sos-" + Guid.NewGuid().ToString("N");
            var audit = new List<SosAuditEntry> { new("triggered", staffName ?? "resident", "operation:" + request.OperationId + ":" + requestHash, now) };
            var alert = new SosAlert(id, TenantContext.CurrentId, status, request.Channel, HashContact(request.Contact), staffName, crewName,
                request.Silent, now, now.AddMinutes(windowMinutes), now.AddSeconds(cancelSeconds), request.Location, request.AccuracyMeters, request.BatteryPercent,
                request.AddressOrLandmark, null, null, null, null, audit, false, true);
            values.Add(alert);
            if (!string.IsNullOrWhiteSpace(ownerKey))
            {
                var owners = await ReadOwnersAsync(ct); owners[id] = OwnerHash(ownerKey); await SaveOwnersAsync(owners, ct);
            }
            await SaveAsync(values, ct); events.Publish("sos.triggered");
            return new(alert, false, needs, needs ? "SOS recorded. Please provide an address or landmark so the control room can locate you." : "SOS alert sent to the municipal control room. A human operator must acknowledge it.", EmergencyNotice);
        }
        finally { gate.Release(); }
    }

    public async Task<SosResult> AddLocationAsync(string id, SosLocationRequest request, string actor, CancellationToken ct,
        string? ownerKey = null, bool requireOwner = false)
    {
        Operation(request.OperationId); Text(request.AddressOrLandmark, "addressOrLandmark", 240);
        if (request.Location is null && string.IsNullOrWhiteSpace(request.AddressOrLandmark)) throw new SosRequestException(400, "Share a location or provide an address or landmark.");
        if (request.Location is not null && !GeoGeofencing.IsValidPoint(request.Location)) throw new SosRequestException(400, "Location is outside the supported geographic envelope.");
        if (request.AccuracyMeters is < 0 or > 10000 || request.AccuracyMeters is double.NaN or double.PositiveInfinity or double.NegativeInfinity) throw new SosRequestException(400, "Accuracy must be a finite bounded value.");
        var requestHash = RequestHash(request);
        await gate.WaitAsync(ct);
        try
        {
            var values = await ReadAsync(ct); var index = values.FindIndex(x => x.Id == id && x.TenantId == TenantContext.CurrentId); if (index < 0) throw new SosRequestException(404, "SOS alert not found.");
            var alert = values[index]; if (!Active(alert, time.GetUtcNow())) throw new SosRequestException(409, "Location sharing window has ended; no coordinates were retained.");
            if (requireOwner)
            {
                var owners = await ReadOwnersAsync(ct);
                if (string.IsNullOrWhiteSpace(ownerKey) || !owners.TryGetValue(id, out var expected) || !string.Equals(expected, OwnerHash(ownerKey), StringComparison.Ordinal))
                    throw new SosRequestException(403, "Only the person who triggered this SOS may share its location.");
            }
            var duplicate = alert.Audit.FirstOrDefault(a => a.Action == "location-shared" && a.Note?.StartsWith("operation:" + request.OperationId + ":", StringComparison.Ordinal) is true);
            if (duplicate is not null)
            {
                if (duplicate.Note == "operation:" + request.OperationId + ":" + requestHash) return new(alert, true, false, "This location operation was already recorded.", EmergencyNotice);
                throw new SosRequestException(409, "This location operation key already belongs to another request.");
            }
            var now = time.GetUtcNow(); var audit = alert.Audit.Append(new SosAuditEntry("location-shared", actor, "operation:" + request.OperationId + ":" + requestHash, now)).ToArray();
            alert = alert with { Status = alert.Status == SosStatus.AwaitingLocation ? SosStatus.Triggered : alert.Status,
                Location = request.Location, AccuracyMeters = request.AccuracyMeters, AddressOrLandmark = request.AddressOrLandmark ?? alert.AddressOrLandmark, Audit = audit };
            values[index] = alert; await SaveAsync(values, ct); events.Publish("sos.location");
            return new(alert, false, false, "Location shared for this active SOS window only.", EmergencyNotice);
        }
        finally { gate.Release(); }
    }

    public async Task<SosAlert?> ActAsync(string id, SosStatus target, SosActionRequest request, string actor, CancellationToken ct)
    {
        Operation(request.OperationId); Text(request.Note, "note", 2000, 2); Text(actor, "actor", 160, 2);
        var requestHash = RequestHash(request);
        await gate.WaitAsync(ct);
        try
        {
            var values = await ReadAsync(ct); var index = values.FindIndex(x => x.Id == id && x.TenantId == TenantContext.CurrentId); if (index < 0) return null;
            var alert = values[index];
            var marker = "operation:" + request.OperationId + ":" + requestHash;
            var targetAction = target.ToString().ToLowerInvariant();
            var replay = alert.Audit.FirstOrDefault(a => String.Equals(a.Action, targetAction, StringComparison.Ordinal)
                && a.Note?.StartsWith("operation:" + request.OperationId + ":", StringComparison.Ordinal) is true);
            if (replay is not null)
            {
                if (replay.Note?.StartsWith(marker, StringComparison.Ordinal) is true) return alert;
                throw new SosRequestException(409, "This SOS action operation key already belongs to another request.");
            }
            if (!ValidTransition(alert.Status, target)) throw new SosRequestException(409, $"Cannot move SOS from {alert.Status} to {target}.");
            var now = time.GetUtcNow();
            var review = target == SosStatus.FalseAlarm && values.Any(other => other.Id != alert.Id && SameRequester(other, alert) && other.Status == SosStatus.FalseAlarm);
            var audit = new List<SosAuditEntry>(alert.Audit)
            {
                new(target.ToString().ToLowerInvariant(), actor, "operation:" + request.OperationId + ":" + requestHash + " " + request.Note, now)
            };
            if (target == SosStatus.FalseAlarm)
            {
                audit.Add(new("user-notified-simulated", "CivicOps simulated notification",
                    "SIMULATED notice: a named operator marked this SOS a false alarm. No SMS, WhatsApp, push, call or penalty was sent.", now));
                if (review) audit.Add(new("false-alarm-human-review", actor,
                    "Repeated false alarm flagged for human review only. No score, penalty, restriction or person indicator was applied.", now));
            }
            alert = alert with { Status = target, LastHumanActor = actor, AcknowledgedAt = target == SosStatus.Acknowledged ? now : alert.AcknowledgedAt,
                ResolvedAt = Terminal(target) ? now : alert.ResolvedAt, EscalationLevel = target == SosStatus.Acknowledged ? null : alert.EscalationLevel,
                RepeatedFalseAlarmReview = review, Audit = audit };
            values[index] = alert; await SaveAsync(values, ct); events.Publish("sos." + target.ToString().ToLowerInvariant()); return alert;
        }
        finally { gate.Release(); }
    }

    public async Task<SosAlert?> CancelAsync(string id, SosActionRequest request, string actor, CancellationToken ct,
        string? ownerKey = null, bool requireOwner = false)
    {
        Operation(request.OperationId); Text(request.Note, "note", 2000);
        await gate.WaitAsync(ct);
        try
        {
            var values = await ReadAsync(ct); var index = values.FindIndex(x => x.Id == id && x.TenantId == TenantContext.CurrentId); if (index < 0) return null;
            var alert = values[index];
            if (requireOwner)
            {
                var owners = await ReadOwnersAsync(ct);
                if (string.IsNullOrWhiteSpace(ownerKey) || !owners.TryGetValue(id, out var expected) || !string.Equals(expected, OwnerHash(ownerKey), StringComparison.Ordinal))
                    throw new SosRequestException(403, "Only the person who triggered this SOS may cancel it.");
            }
            var hash = RequestHash(request); var marker = "operation:" + request.OperationId + ":" + hash;
            var replay = alert.Audit.FirstOrDefault(a => a.Action == "cancelled" && a.Note?.StartsWith("operation:" + request.OperationId + ":", StringComparison.Ordinal) is true);
            if (replay is not null)
            {
                if (replay.Note?.StartsWith(marker, StringComparison.Ordinal) is true) return alert;
                throw new SosRequestException(409, "This SOS cancel operation key already belongs to another request.");
            }
            if (alert.Status is not (SosStatus.AwaitingLocation or SosStatus.Triggered) || time.GetUtcNow() > alert.CancelUntil) throw new SosRequestException(409, "The SOS cancel window has ended.");
            var note = marker + " " + request.Note;
            var updated = alert with { Status = SosStatus.Cancelled, ResolvedAt = time.GetUtcNow(), LastHumanActor = actor, Audit = alert.Audit.Append(new("cancelled", actor, note, time.GetUtcNow())).ToArray() };
            values[index] = updated; await SaveAsync(values, ct); events.Publish("sos.cancelled"); return updated;
        }
        finally { gate.Release(); }
    }

    private static bool SameRequester(SosAlert prior, SosAlert current) =>
        (current.ContactHash != "anonymous" && prior.ContactHash == current.ContactHash)
        || (current.ContactHash == "anonymous" && current.StaffName is not null && prior.StaffName == current.StaffName && prior.CrewName == current.CrewName);
    private static bool ValidTransition(SosStatus from, SosStatus to) => (from, to) switch
    {
        (SosStatus.AwaitingLocation, SosStatus.Triggered or SosStatus.Acknowledged or SosStatus.FalseAlarm) => true,
        (SosStatus.Triggered, SosStatus.Acknowledged or SosStatus.FalseAlarm) => true,
        (SosStatus.Acknowledged, SosStatus.Assessing or SosStatus.FalseAlarm) => true,
        (SosStatus.Assessing, SosStatus.Dispatched or SosStatus.FalseAlarm) => true,
        (SosStatus.Dispatched, SosStatus.Resolved or SosStatus.FalseAlarm) => true,
        _ => false
    };

    public async Task<SosAlert?> GetAsync(string id, CancellationToken ct) => await GetAsync(id, "control-room-reader", ct);
    public async Task<SosAlert?> GetAsync(string id, string actor, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var result = (await ReadAsync(ct)).FirstOrDefault(x => x.Id == id && x.TenantId == TenantContext.CurrentId); await RecordAccessAsync(result is null ? [] : [id], "viewed", actor, ct); return result; }
        finally { gate.Release(); }
    }
    public async Task<SosQueue> QueueAsync(CancellationToken ct) => await QueueAsync("control-room-reader", ct);
    public async Task<SosQueue> QueueAsync(string actor, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow(); var values = (await ReadAsync(ct)).Where(x => x.TenantId == TenantContext.CurrentId).ToList(); var changed = false;
            for (var i = 0; i < values.Count; i++)
            {
                var alert = values[i];
                if ((Terminal(alert.Status) || alert.FollowUpUntil <= now) && alert.Location is not null)
                {
                    values[i] = alert with { Location = null, AccuracyMeters = null,
                        Audit = alert.Audit.Append(new SosAuditEntry("location-purged", "CivicOps automatic retention", "Follow-up window ended or alert resolved; coordinates removed.", now)).ToArray() };
                    changed = true; alert = values[i];
                }
                if (!Active(alert, now) || alert.AcknowledgedAt is not null) continue;
                var elapsed = now - alert.TriggeredAt; var level = elapsed >= TimeSpan.FromSeconds(ackSeconds * 2) ? "duty-manager" : elapsed >= TimeSpan.FromSeconds(ackSeconds) ? "supervisor" : null;
                if (level is not null && alert.EscalationLevel != level)
                {
                    values[i] = alert with { EscalationLevel = level, Audit = alert.Audit.Append(new("escalated-" + level, "CivicOps simulated notification",
                        "SIMULATED SMS/push to the " + level + ". No message was sent and no person was scored.", now)).ToArray() }; changed = true;
                }
            }
            if (changed) { await SaveAsync(values, ct); events.Publish("sos.queue"); }
            await RecordAccessAsync(values.Where(x => Active(x, now)).Select(x => x.Id), "queue-viewed", actor, ct);
            var queue = values.Where(x => Active(x, now)).OrderByDescending(x => x.TriggeredAt).ToArray();
            return new(queue, now, queue.Count(x => x.AcknowledgedAt is null), ackSeconds);
        }
        finally { gate.Release(); }
    }
    public async IAsyncEnumerable<SosAlert> StreamAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { while (!ct.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(2), ct); var queue = await QueueAsync(ct); foreach (var alert in queue.Alerts.Take(8)) yield return alert; } }
    public int AckTargetSeconds => ackSeconds;
    public int CancelWindowSeconds => cancelSeconds;
    public void Dispose() => gate.Dispose();
}
public sealed class SosRequestException(int status, string message) : Exception(message) { public int Status { get; } = status; }
