using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CivicOps.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace CivicOps.Services;

public sealed record CrewActor(string WorkerId, string CrewId, string OwnerId, string Name, string? DeviceId);
public sealed class CrewRequestException(int status, string message) : InvalidOperationException(message)
{ public int Status { get; } = status; }

/// <summary>Authoritative shift/consent gates; raw positions live in separate tenant/worker/day documents.</summary>
public sealed class CrewStore(IConfiguration configuration, IWebHostEnvironment environment, DurableStorage storage,
    TimeProvider time, WorkforceStore workforce, IncidentStore incidents, CaseEventStream events) : IDisposable
{
    public const string NoticeVersion = "civicops-crew-location-v1";
    public const string PrivacyNotice = "Your municipality uses your location only to coordinate your assigned field work and worker safety while you have an active shift and explicitly choose to share. Only authorised municipal supervisors see your latest sharing position; you can see and export your own history. Raw location records are automatically deleted after the municipality's retention window (30 days by default, at most 30 days). Anonymous municipality-day sample counts may remain. Sharing has a visible indicator and stops when you end your shift, withdraw consent, or reach the maximum shift length (12 hours by default, at most 12 hours). You can withdraw at any time. No off-shift monitoring, biometric identification, person scoring or automatic police/dispatch decision is permitted. Ask your municipal Information Officer about the lawful basis, rights and access review before live deployment. CivicOps is not an emergency service.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string _root = DataPaths.Resolve(configuration, environment);
    private readonly bool _demo = bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demo) && demo;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Partition => TenantContext.DirectoryFor(_root);
    private string StatePath => Path.Combine(Partition, "crew-app.json");
    private string HistoryDirectory => Path.Combine(Partition, "crew-locations");
    private sealed record Pairing(string Id, string WorkerId, string CrewId, string DeviceName, string CodeHash, DateTimeOffset ExpiresAt, DateTimeOffset? ConsumedAt);
    private sealed record Device(CrewDeviceView View, string TokenHash);
    private sealed record Receipt(string Hash, DateTimeOffset At);
    private sealed record Latest(string WorkerId, string CrewId, CrewLocationPoint Point, bool Synthetic);
    private sealed record Snapshot(string TenantId, CrewPolicy Policy, List<CrewShift> Shifts, List<CrewConsent> Consents,
        List<Pairing> Pairings, List<Device> Devices, Dictionary<string, Receipt> Receipts, List<Latest> Latest,
        Dictionary<string, long> PurgedSamplesByDay, List<EnterpriseAuditRecord> Audit);
    private sealed record HistoryDay(string TenantId, string WorkerId, string Day, List<CrewLocationPoint> Points,
        Dictionary<string, Receipt> Receipts, string StateHash);
    private static Snapshot Empty() => new(TenantContext.CurrentId, new(), [], [], [], [], new(StringComparer.Ordinal), [], new(StringComparer.Ordinal), []);
    public void Dispose() => _gate.Dispose();

    public async Task<CrewActor> ActorAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var crewId = principal.FindFirstValue("civicops:crew");
        var workerId = principal.FindFirstValue("civicops:worker");
        if (workerId is null && _demo && principal.HasClaim("civicops:demo", "true") && crewId is not null)
            workerId = "synthetic-" + crewId + "-a";
        var worker = workerId is null ? null : await workforce.GetWorkerAsync(workerId, ct);
        if (!principal.IsInRole("crew") || worker is null || worker.CrewId != crewId)
            throw new CrewRequestException(403, "A current municipality worker and crew binding is required. Ask a supervisor to pair this device or map the issuer worker_id claim.");
        var deviceId = principal.FindFirstValue("civicops:crew-device");
        return new(worker.Id, worker.CrewId, deviceId ?? "staff:" + principal.FindFirstValue(ClaimTypes.NameIdentifier),
            principal.Identity?.Name ?? worker.DisplayName, deviceId);
    }
    public Task<CrewMe> MeAsync(CrewActor actor, CancellationToken ct) => ReadAsync(snapshot => new CrewMe(actor.WorkerId, actor.CrewId,
        snapshot.TenantId, actor.DeviceId, ActiveShift(snapshot, actor), ActiveConsent(snapshot, actor.WorkerId), snapshot.Policy,
        NoticeVersion, PrivacyNotice, _demo), ct);
    public Task<CrewPolicy> PolicyAsync(CancellationToken ct) => ReadAsync(snapshot => snapshot.Policy, ct);
    public Task<CrewPolicy> SetPolicyAsync(CrewPolicyRequest request, string actor, CancellationToken ct)
    {
        ValidatePolicy(new(request.IntervalSeconds, request.AccuracyMeters, request.RetentionDays, request.MaxShiftHours));
        Text(request.Reason, "reason", 2000, 5);
        return WriteAsync(async snapshot =>
        {
            var next = snapshot with { Policy = new(request.IntervalSeconds, request.AccuracyMeters, request.RetentionDays, request.MaxShiftHours) };
            await SaveAsync(next, "crew.policy-reviewed", actor, "A named supervisor reviewed bounded interval, accuracy, retention and maximum shift policy. Reason: " + request.Reason, ct);
            return next.Policy;
        }, ct);
    }
    public Task<CrewMe> StartAsync(CrewActor actor, CrewOperationRequest request, CancellationToken ct) => WriteAsync(async snapshot =>
    {
        var key = Key(actor.WorkerId, request.OperationId); var hash = Hash(new { action = "shift.start", request });
        if (!Replay(snapshot.Receipts, key, hash))
        {
            if (snapshot.Shifts.Any(value => value.WorkerId == actor.WorkerId && ShiftActive(value, snapshot.Policy)))
                throw new CrewRequestException(409, "This worker already has an active shift. End it before starting another device shift.");
            var now = time.GetUtcNow(); var expires = now.AddHours(snapshot.Policy.MaxShiftHours);
            var device = snapshot.Devices.FirstOrDefault(value => value.View.Id == actor.OwnerId);
            if (device is not null && device.View.ExpiresAt < expires) expires = device.View.ExpiresAt;
            snapshot.Shifts.Add(new(NewId("shift"), actor.WorkerId, actor.CrewId, now, expires, null, actor.OwnerId));
            snapshot.Receipts[key] = new(hash, now);
            await SaveAsync(snapshot, "crew.shift-started", actor.Name, "Worker " + actor.WorkerId + " explicitly started a bounded shift; location sharing remains subject to separate revocable consent.", ct);
        }
        return Me(snapshot, actor);
    }, ct);
    public Task<CrewMe> EndAsync(CrewActor actor, CrewShiftEndRequest request, CancellationToken ct) => WriteAsync(async snapshot =>
    {
        var key = Key(actor.WorkerId, request.OperationId); var hash = Hash(new { action = "shift.end", request });
        if (!Replay(snapshot.Receipts, key, hash))
        {
            var index = snapshot.Shifts.FindIndex(value => value.Id == request.ShiftId && value.WorkerId == actor.WorkerId && value.OwnerId == actor.OwnerId);
            if (index < 0) throw new CrewRequestException(404, "Shift is unavailable for this worker and device.");
            var shift = snapshot.Shifts[index]; snapshot.Shifts[index] = shift with { EndedAt = shift.EndedAt ?? time.GetUtcNow() };
            snapshot.Latest.RemoveAll(value => value.WorkerId == actor.WorkerId && value.Point.ShiftId == shift.Id);
            snapshot.Receipts[key] = new(hash, time.GetUtcNow());
            await SaveAsync(snapshot, "crew.shift-ended", actor.Name, "Worker " + actor.WorkerId + " ended a shift. Latest position removed; all further location uploads for that shift are forbidden.", ct);
        }
        return Me(snapshot, actor);
    }, ct);
    public Task<CrewMe> ConsentAsync(CrewActor actor, CrewConsentRequest request, CancellationToken ct) => WriteAsync(async snapshot =>
    {
        if (!request.Accepted || request.NoticeVersion != NoticeVersion)
            throw new CrewRequestException(400, "Explicit acceptance of the current worker location purpose notice is required.");
        var key = Key(actor.WorkerId, request.OperationId); var hash = Hash(new { action = "consent.grant", request });
        if (!Replay(snapshot.Receipts, key, hash))
        {
            var now = time.GetUtcNow();
            for (var index = 0; index < snapshot.Consents.Count; index++)
                if (snapshot.Consents[index].WorkerId == actor.WorkerId && snapshot.Consents[index].WithdrawnAt is null)
                    snapshot.Consents[index] = snapshot.Consents[index] with { WithdrawnAt = now };
            snapshot.Consents.Add(new(NewId("consent"), actor.WorkerId, NoticeVersion, PrivacyNotice, now, null));
            snapshot.Latest.RemoveAll(value => value.WorkerId == actor.WorkerId);
            snapshot.Receipts[key] = new(hash, now);
            await SaveAsync(snapshot, "crew.location-consent-granted", actor.Name, "Worker " + actor.WorkerId + " explicitly accepted " + NoticeVersion + "; no automatic dispatch or off-shift monitoring.", ct);
        }
        return Me(snapshot, actor);
    }, ct);
    public Task<CrewMe> WithdrawAsync(CrewActor actor, CrewOperationRequest request, CancellationToken ct) => WriteAsync(async snapshot =>
    {
        var key = Key(actor.WorkerId, request.OperationId); var hash = Hash(new { action = "consent.withdraw", request });
        if (!Replay(snapshot.Receipts, key, hash))
        {
            for (var index = 0; index < snapshot.Consents.Count; index++)
                if (snapshot.Consents[index].WorkerId == actor.WorkerId && snapshot.Consents[index].WithdrawnAt is null)
                    snapshot.Consents[index] = snapshot.Consents[index] with { WithdrawnAt = time.GetUtcNow() };
            snapshot.Latest.RemoveAll(value => value.WorkerId == actor.WorkerId);
            snapshot.Receipts[key] = new(hash, time.GetUtcNow());
            await SaveAsync(snapshot, "crew.location-consent-withdrawn", actor.Name, "Worker " + actor.WorkerId + " withdrew sharing. Latest position removed; all later uploads, including old buffered points, are forbidden.", ct);
        }
        return Me(snapshot, actor);
    }, ct);
    public Task<CrewLocationResult> LocationsAsync(CrewActor actor, CrewLocationRequest request, CancellationToken ct) => WriteAsync(async snapshot =>
    {
        var shift = ActiveShift(snapshot, actor); var consent = ActiveConsent(snapshot, actor.WorkerId);
        if (shift is null || consent is null || !request.VisibleIndicator || shift.Id != request.ShiftId || consent.Id != request.ConsentId)
        {
            await SaveAsync(snapshot, "crew.location-rejected", actor.Name,
                "Rejected worker location without the current device-owned active shift, current revocable consent and visible indicator. No supplied coordinates retained.", ct);
            return new CrewLocationResult(-1, false); // Commit the value-free denial audit before endpoint returns 403.
        }
        Key(actor.WorkerId, request.OperationId);
        if (request.Points is null || request.Points.Count is < 1 or > 100 || request.Points.Any(value => value is null))
            throw new CrewRequestException(400, "Provide 1–100 bounded location points.");
        var now = time.GetUtcNow(); var sorted = request.Points.OrderBy(value => value.CapturedAt).ToArray();
        foreach (var point in sorted)
            if (!GeoGeofencing.IsValidPoint(new(point.Longitude, point.Latitude)) || !double.IsFinite(point.AccuracyMeters)
                || point.AccuracyMeters is < 1 or > 1000 || point.AccuracyMeters > snapshot.Policy.AccuracyMeters
                || point.CapturedAt < shift.StartedAt || point.CapturedAt < consent.GrantedAt || point.CapturedAt >= shift.ExpiresAt
                || point.CapturedAt > now.AddSeconds(30) || point.CapturedAt < now.AddDays(-snapshot.Policy.RetentionDays))
                throw new CrewRequestException(400, "Location must be a finite South African coordinate with policy accuracy, captured during the current consented shift and retained purpose window.");
        // A batch remains within one UTC day; clients split midnight batches to keep daily receipts atomic.
        var day = Day(sorted[0].CapturedAt);
        if (sorted.Any(value => Day(value.CapturedAt) != day)) throw new CrewRequestException(400, "Split location batches at UTC midnight.");
        var path = HistoryPath(actor.WorkerId, day); var history = await LoadHistoryAsync(path, actor.WorkerId, day, ct);
        var key = Key(actor.WorkerId, request.OperationId); var hash = Hash(request);
        if (Replay(history.Receipts, key, hash)) return new CrewLocationResult(sorted.Length, true);
        foreach (var adjacentDay in new[] { Day(sorted[0].CapturedAt.AddDays(-1)), Day(sorted[0].CapturedAt.AddDays(1)) })
        {
            var adjacent = await LoadHistoryAsync(HistoryPath(actor.WorkerId, adjacentDay), actor.WorkerId, adjacentDay, ct);
            if (adjacent.Receipts.ContainsKey(key)) throw new CrewRequestException(409, "This location operation key already belongs to another UTC-day batch.");
        }
        var prior = snapshot.Latest.FirstOrDefault(value => value.WorkerId == actor.WorkerId)?.Point.CapturedAt;
        foreach (var point in sorted)
        {
            if (prior is not null && point.CapturedAt - prior < TimeSpan.FromSeconds(Math.Max(15, snapshot.Policy.IntervalSeconds - 1)))
                throw new CrewRequestException(409, "Location points must arrive in capture order at the tenant policy interval (one-second timer tolerance); refresh the shift before retrying.");
            prior = point.CapturedAt;
        }
        if (history.Points.Count + sorted.Length > 6000) throw new CrewRequestException(409, "Daily location capacity reached; further sharing is disabled for this worker today.");
        var points = sorted.Select((point, index) => new CrewLocationPoint("point-" + key[..16] + "-" + index.ToString(CultureInfo.InvariantCulture), shift.Id,
            point.Longitude, point.Latitude, point.AccuracyMeters, point.CapturedAt)).ToArray();
        history.Points.AddRange(points); history.Receipts[key] = new(hash, now); await SaveHistoryAsync(path, history, ct);
        snapshot.Latest.RemoveAll(value => value.WorkerId == actor.WorkerId);
        snapshot.Latest.Add(new(actor.WorkerId, actor.CrewId, points[^1], _demo));
        await SaveAsync(snapshot, "crew.location-accepted", actor.Name, $"Accepted {points.Length} purpose-limited on-shift points. Coordinate values omitted from audit; human supervisors retain dispatch authority.", ct);
        return new CrewLocationResult(points.Length, false);
    }, ct);
    public Task<CrewLocationHistory> HistoryAsync(CrewActor actor, int page, int limit, bool export, CancellationToken ct) => WriteAsync(async snapshot =>
    {
        if (page < 1 || page > 1000 || limit is < 1 or > 1000) throw new CrewRequestException(400, "History page is 1–1000; limit is 1–1000.");
        var points = new List<CrewLocationPoint>(); var cutoff = time.GetUtcNow().AddDays(-snapshot.Policy.RetentionDays);
        foreach (var path in await storage.ListPathsAsync(HistoryDirectory, ".json", ct))
        {
            var file = Path.GetFileName(path);
            if (!file.StartsWith(actor.WorkerId + ".", StringComparison.Ordinal)) continue;
            var day = file[(actor.WorkerId.Length + 1)..^5];
            var history = await LoadHistoryAsync(path, actor.WorkerId, day, ct);
            points.AddRange(history.Points.Where(value => value.CapturedAt >= cutoff));
        }
        var ordered = points.OrderByDescending(value => value.CapturedAt).ToArray();
        await SaveAsync(snapshot, export ? "crew.location-history-exported" : "crew.location-history-accessed", actor.Name,
            "Worker " + actor.WorkerId + " accessed their own retained history. Coordinates omitted from access audit.", ct);
        return new CrewLocationHistory(export ? ordered : ordered.Skip((page - 1) * limit).Take(limit).ToArray(), snapshot.Policy.RetentionDays,
            time.GetUtcNow(), ordered.Length, export || page * limit >= ordered.Length ? null : page + 1);
    }, ct);
    public Task<IReadOnlyList<GeoCrewPosition>> PositionsAsync(CancellationToken ct) => WriteAsync<IReadOnlyList<GeoCrewPosition>>(async snapshot =>
    {
        var workers = await workforce.ListWorkersAsync(ct);
        return snapshot.Latest.Where(value => value.Point.CapturedAt >= time.GetUtcNow().AddDays(-snapshot.Policy.RetentionDays)
            && workers.Any(worker => worker.Id == value.WorkerId && worker.CrewId == value.CrewId)
            && snapshot.Shifts.Any(shift => shift.Id == value.Point.ShiftId && shift.WorkerId == value.WorkerId && ShiftActive(shift, snapshot.Policy)
                && (shift.OwnerId.StartsWith("staff:", StringComparison.Ordinal) || !shift.OwnerId.StartsWith("device-", StringComparison.Ordinal)
                    || snapshot.Devices.Any(device => device.View.Id == shift.OwnerId && device.View.RevokedAt is null && device.View.ExpiresAt > time.GetUtcNow())))
            && ActiveConsent(snapshot, value.WorkerId) is not null)
        .GroupBy(value => value.CrewId).Select(group => group.OrderByDescending(value => value.Point.CapturedAt).First())
        .Select(value => new GeoCrewPosition(value.CrewId, new(value.Point.Longitude, value.Point.Latitude), value.Point.CapturedAt, "on-shift / sharing", value.Synthetic, snapshot.TenantId)).ToArray();
    }, ct);

    public Task<CrewPairingIssued> PairAsync(CrewPairingRequest request, string actor, CancellationToken ct) => WriteAsync(async snapshot =>
    {
        Text(request.DeviceName, "deviceName", 100, 1); Text(request.Reason, "reason", 2000, 5);
        var worker = await workforce.GetWorkerAsync(request.WorkerId, ct) ?? throw new CrewRequestException(400, "Choose a worker in this municipality.");
        var id = NewId("pair"); var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)); var expires = time.GetUtcNow().AddMinutes(10);
        snapshot.Pairings.Add(new(id, worker.Id, worker.CrewId, PiiRedactor.Redact(request.DeviceName), IncidentStore.Hash(code), expires, null));
        await SaveAsync(snapshot, "crew.pairing-created", actor, "Supervisor generated a one-use ten-minute pairing for worker " + worker.Id + ". Reason: " + request.Reason, ct);
        return new CrewPairingIssued(id, code, snapshot.TenantId, expires);
    }, ct);
    public Task<CrewTokenIssued?> ExchangeAsync(CrewPairingExchangeRequest request, CancellationToken ct) => WriteAsync(async snapshot =>
    {
        var index = snapshot.Pairings.FindIndex(value => value.Id == request.PairingId);
        var pairing = index < 0 ? null : snapshot.Pairings[index];
        if (request.TenantId != snapshot.TenantId || pairing is null || pairing.ConsumedAt is not null || pairing.ExpiresAt <= time.GetUtcNow()
            || request.Code is null || request.Code.Length != 10 || !EqualHash(pairing.CodeHash, IncidentStore.Hash(request.Code)))
        {
            await SaveAsync(snapshot, "crew.pairing-rejected", "Unauthenticated pairing client", "Rejected unknown, expired, consumed or invalid pairing. No code, token or supplied worker identity retained.", ct);
            return null;
        }
        var worker = await workforce.GetWorkerAsync(pairing.WorkerId, ct);
        if (worker is null || worker.CrewId != pairing.CrewId) return null;
        snapshot.Pairings[index] = pairing with { ConsumedAt = time.GetUtcNow(), CodeHash = "" };
        return await IssueAsync(snapshot, worker, pairing.DeviceName, "Supervisor-authorised device pairing", null, ct);
    }, ct);
    public Task<CrewTokenIssued> IssueOidcAsync(Worker worker, DateTimeOffset tokenExpiry, CancellationToken ct) => WriteAsync(snapshot =>
        IssueAsync(snapshot, worker, "OIDC crew device", "Trusted OIDC crew identity " + worker.Id, tokenExpiry, ct), ct);
    private async Task<CrewTokenIssued> IssueAsync(Snapshot snapshot, Worker worker, string deviceName, string actor, DateTimeOffset? tokenExpiry, CancellationToken ct)
    {
        var now = time.GetUtcNow(); var expires = now.AddHours(8); if (tokenExpiry < expires) expires = tokenExpiry.Value;
        var id = NewId("device"); var token = "cc1." + snapshot.TenantId + "." + id + "." + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        snapshot.Devices.Add(new(new(id, worker.Id, worker.CrewId, deviceName, now, expires, null), IncidentStore.Hash(token)));
        await SaveAsync(snapshot, "crew.device-issued", actor, "Issued hashed, crew-self-only, expiring and revocable credential " + id + " for worker " + worker.Id + ". No token retained in audit.", ct);
        return new(token, id, snapshot.TenantId, worker.Id, worker.CrewId, expires);
    }
    public async Task<ClaimsPrincipal?> AuthenticateAsync(string token, CancellationToken ct)
    {
        var parts = token.Split('.'); if (parts.Length != 4 || parts[0] != "cc1" || !TenantContext.IsValidId(parts[1]) || !TenantContext.IsValidId(parts[2]) || parts[3].Length != 43) return null;
        using var lease = TenantContext.Use(parts[1]);
        return await ReadAsync(snapshot =>
        {
            var device = snapshot.Devices.FirstOrDefault(value => value.View.Id == parts[2]);
            if (device is null || device.View.RevokedAt is not null || device.View.ExpiresAt <= time.GetUtcNow() || !EqualHash(device.TokenHash, IncidentStore.Hash(token))) return null;
            return new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "crew-device:" + device.View.Id), new(ClaimTypes.Name, "Crew worker " + device.View.WorkerId),
                new(ClaimTypes.Role, "crew"), new("civicops:tenant", parts[1]), new("civicops:crew", device.View.CrewId), new("civicops:worker", device.View.WorkerId),
                new("civicops:crew-device", device.View.Id), new("civicops:scope", "crew:self")], "crew-device"));
        }, ct);
    }
    public Task<IReadOnlyList<CrewDeviceView>> DevicesAsync(CancellationToken ct) => ReadAsync<IReadOnlyList<CrewDeviceView>>(snapshot => snapshot.Devices.Select(value => value.View).ToArray(), ct);
    public Task<bool> RevokeAsync(string id, string reason, string actor, CancellationToken ct) => WriteAsync(async snapshot =>
    {
        Text(reason, "reason", 2000, 5); var index = snapshot.Devices.FindIndex(value => value.View.Id == id); if (index < 0) return false;
        var device = snapshot.Devices[index]; snapshot.Devices[index] = device with { View = device.View with { RevokedAt = device.View.RevokedAt ?? time.GetUtcNow() } };
        for (var shiftIndex = 0; shiftIndex < snapshot.Shifts.Count; shiftIndex++)
            if (snapshot.Shifts[shiftIndex].OwnerId == id && snapshot.Shifts[shiftIndex].EndedAt is null)
                snapshot.Shifts[shiftIndex] = snapshot.Shifts[shiftIndex] with { EndedAt = time.GetUtcNow() };
        snapshot.Latest.RemoveAll(value => value.WorkerId == device.View.WorkerId);
        await SaveAsync(snapshot, "crew.device-revoked", actor, "Supervisor revoked " + id + " and stopped its active shift. Reason: " + reason, ct); return true;
    }, ct);
    public Task<int> PurgeAsync(CancellationToken ct) => WriteAsync(async snapshot =>
    {
        var priorState = StateHash(snapshot);
        var now = time.GetUtcNow(); var cutoff = now.AddDays(-snapshot.Policy.RetentionDays); var removed = 0;
        foreach (var path in await storage.ListPathsAsync(HistoryDirectory, ".json", ct))
        {
            var persisted = await storage.ReadAsync(path, ct); if (persisted is null) continue;
            var history = JsonSerializer.Deserialize<HistoryDay>(persisted, Json) ?? throw new InvalidDataException("Invalid crew history."); ValidateHistory(history);
            var expired = history.Points.Count(value => value.CapturedAt < cutoff);
            if (expired == 0) continue;
            snapshot.PurgedSamplesByDay[history.Day] = snapshot.PurgedSamplesByDay.GetValueOrDefault(history.Day) + expired;
            removed += expired;
            var remaining = history.Points.Where(value => value.CapturedAt >= cutoff).ToList();
            if (remaining.Count == 0) await storage.DeleteAsync(path, ct);
            else await SaveHistoryAsync(path, history with { Points = remaining, Receipts = new(StringComparer.Ordinal) }, ct);
        }
        var stopped = 0;
        for (var index = 0; index < snapshot.Shifts.Count; index++)
            if (snapshot.Shifts[index].EndedAt is null && !ShiftActive(snapshot.Shifts[index], snapshot.Policy))
            { snapshot.Shifts[index] = snapshot.Shifts[index] with { EndedAt = EffectiveExpiry(snapshot.Shifts[index], snapshot.Policy) }; stopped++; }
        snapshot.Latest.RemoveAll(value => value.Point.CapturedAt < cutoff || !snapshot.Shifts.Any(shift => shift.Id == value.Point.ShiftId && ShiftActive(shift, snapshot.Policy)) || ActiveConsent(snapshot, value.WorkerId) is null);
        snapshot.Shifts.RemoveAll(value => value.EndedAt < cutoff); snapshot.Consents.RemoveAll(value => value.WithdrawnAt < cutoff);
        snapshot.Pairings.RemoveAll(value => value.ExpiresAt < now.AddDays(-1)); snapshot.Devices.RemoveAll(value => value.View.ExpiresAt < cutoff || value.View.RevokedAt < cutoff);
        foreach (var key in snapshot.Receipts.Where(value => value.Value.At < cutoff).Select(value => value.Key).ToArray()) snapshot.Receipts.Remove(key);
        if (removed > 0 || stopped > 0 || StateHash(snapshot) != priorState) await SaveAsync(snapshot, "crew.retention-purged", "CivicOps automatic retention",
            $"Purged {removed} raw points and expired {stopped} shifts. Retained counts are municipality-day totals without worker/device/person association.", ct);
        return removed;
    }, ct);
    public Task<IReadOnlyDictionary<string, long>> AggregateStatsAsync(CancellationToken ct) => ReadAsync<IReadOnlyDictionary<string, long>>(snapshot => snapshot.PurgedSamplesByDay, ct);
    private CrewMe Me(Snapshot snapshot, CrewActor actor) => new(actor.WorkerId, actor.CrewId, snapshot.TenantId, actor.DeviceId,
        ActiveShift(snapshot, actor), ActiveConsent(snapshot, actor.WorkerId), snapshot.Policy, NoticeVersion, PrivacyNotice, _demo);
    private CrewShift? ActiveShift(Snapshot snapshot, CrewActor actor) => snapshot.Shifts.LastOrDefault(value => value.WorkerId == actor.WorkerId && value.CrewId == actor.CrewId && value.OwnerId == actor.OwnerId && ShiftActive(value, snapshot.Policy)) is { } shift
        ? shift with { ExpiresAt = EffectiveExpiry(shift, snapshot.Policy) } : null;
    private bool ShiftActive(CrewShift shift, CrewPolicy policy) => shift.EndedAt is null && shift.StartedAt <= time.GetUtcNow() && EffectiveExpiry(shift, policy) > time.GetUtcNow();
    private static DateTimeOffset EffectiveExpiry(CrewShift shift, CrewPolicy policy) => shift.ExpiresAt < shift.StartedAt.AddHours(policy.MaxShiftHours) ? shift.ExpiresAt : shift.StartedAt.AddHours(policy.MaxShiftHours);
    private static CrewConsent? ActiveConsent(Snapshot snapshot, string workerId) => snapshot.Consents.LastOrDefault(value => value.WorkerId == workerId && value.WithdrawnAt is null && value.NoticeVersion == NoticeVersion);
    public static void ValidatePolicy(CrewPolicy policy)
    {
        if (policy.IntervalSeconds is < 15 or > 600 || policy.AccuracyMeters is < 5 or > 1000 || policy.RetentionDays is < 1 or > 30 || policy.MaxShiftHours is < 1 or > 12)
            throw new CrewRequestException(400, "Crew policy bounds: 15–600 seconds, 5–1000 metres, 1–30 retention days and 1–12 shift hours.");
    }
    public static void Text(string? value, string name, int maximum, int minimum) { if (RequestValidation.Text(value, name, maximum, minimum) is { } error) throw new CrewRequestException(400, error); }
    public static string Key(string workerId, string operationId) { Text(operationId, "operationId", 128, 8); return IncidentStore.Hash(workerId + ":" + operationId); }
    public static string Hash<T>(T request) => IncidentStore.Hash(JsonSerializer.Serialize(request, Json));
    private static bool Replay(Dictionary<string, Receipt> receipts, string key, string hash)
    { if (!receipts.TryGetValue(key, out var receipt)) return false; if (receipt.Hash != hash) throw new CrewRequestException(409, "This operation key already belongs to another request."); return true; }
    private static string NewId(string kind) => kind + "-" + Guid.NewGuid().ToString("N");
    private static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    private string HistoryPath(string workerId, string day) => Path.Combine(HistoryDirectory, workerId + "." + day + ".json");
    private static bool EqualHash(string first, string second) => first.Length == 64 && second.Length == 64 && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second));
    private Task<T> ReadAsync<T>(Func<Snapshot, T> read, CancellationToken ct) => WriteAsync(snapshot => Task.FromResult(read(snapshot)), ct);
    private async Task<T> WriteAsync<T>(Func<Snapshot, Task<T>> action, CancellationToken ct)
    {
        await using var transaction = storage.Begin(); await transaction.InitializeAsync(ct); await _gate.WaitAsync(ct);
        try { return await action(await LoadAsync(ct)); } catch { transaction.Abort(); throw; } finally { _gate.Release(); }
    }
    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        var persisted = await storage.ReadAsync(StatePath, ct); if (persisted is null) return Empty();
        var snapshot = JsonSerializer.Deserialize<Snapshot>(persisted, Json) ?? throw new InvalidDataException("Invalid crew state.");
        if (snapshot.TenantId != TenantContext.CurrentId) throw new InvalidDataException("Crew municipality mismatch."); ValidatePolicy(snapshot.Policy);
        var previous = AuditLog.Genesis;
        for (var index = 0; index < snapshot.Audit.Count; index++)
        {
            var record = snapshot.Audit[index];
            if (record.TenantId != snapshot.TenantId || record.Sequence != index + 1L || record.PreviousHash != previous || record.Hash != Hash(record with { Hash = "" }))
                throw new InvalidDataException("Crew audit integrity failed.");
            previous = record.Hash;
        }
        if (snapshot.Audit.Count == 0 || snapshot.Audit[^1].StateHash != StateHash(snapshot)) throw new InvalidDataException("Crew state differs from authenticated audit.");
        return snapshot;
    }
    private async Task SaveAsync(Snapshot snapshot, string action, string actor, string detail, CancellationToken ct)
    {
        var record = new EnterpriseAuditRecord(snapshot.TenantId, snapshot.Audit.Count + 1L, action, PiiRedactor.Redact(actor), PiiRedactor.Redact(detail), time.GetUtcNow(),
            snapshot.Audit.LastOrDefault()?.Hash ?? AuditLog.Genesis, StateHash(snapshot), "");
        snapshot.Audit.Add(record with { Hash = Hash(record) });
        await storage.WriteAsync(StatePath, JsonSerializer.Serialize(snapshot, Json), ct);
        await incidents.RecordSystemEventAsync(new(action, actor, detail, time.GetUtcNow()), ct); events.Publish();
    }
    private static string StateHash(Snapshot snapshot) => Hash(snapshot with { Audit = [] });
    private async Task<HistoryDay> LoadHistoryAsync(string path, string workerId, string day, CancellationToken ct)
    {
        var persisted = await storage.ReadAsync(path, ct);
        var history = persisted is null ? new(TenantContext.CurrentId, workerId, day, [], new(StringComparer.Ordinal), "") : JsonSerializer.Deserialize<HistoryDay>(persisted, Json) ?? throw new InvalidDataException("Invalid crew location history.");
        if (persisted is not null) ValidateHistory(history);
        if (history.WorkerId != workerId || history.Day != day) throw new InvalidDataException("Crew history worker/day mismatch."); return history;
    }
    private static void ValidateHistory(HistoryDay history)
    {
        if (history.TenantId != TenantContext.CurrentId || !TenantContext.IsValidId(history.WorkerId) || !DateTime.TryParseExact(history.Day, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || history.Points.Count > 6000 || history.Points.Any(value => Day(value.CapturedAt) != history.Day || !GeoGeofencing.IsValidPoint(new(value.Longitude, value.Latitude)))
            || history.StateHash != Hash(history with { StateHash = "" })) throw new InvalidDataException("Crew history integrity or partition validation failed.");
    }
    private Task SaveHistoryAsync(string path, HistoryDay history, CancellationToken ct) => storage.WriteAsync(path, JsonSerializer.Serialize(history with { StateHash = Hash(history with { StateHash = "" }) }, Json), ct);
}

public sealed class CrewRetentionService(CrewStore store, CrewJobService jobs, TenantStore tenants, ILogger<CrewRetentionService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> Failed = LoggerMessage.Define(LogLevel.Error, new EventId(4701, "CrewRetentionFailed"), "Crew retention failed; inspect authenticated storage. No coordinates logged.");
    public async Task PurgeOnceAsync(CancellationToken ct)
    {
        foreach (var tenant in await tenants.ListAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = TenantContext.Use(tenant.Id);
                await store.PurgeAsync(ct); await jobs.PurgePhotosAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { Failed(logger, null); }
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PurgeOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { Failed(logger, null); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
