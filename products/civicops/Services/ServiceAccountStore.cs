using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CivicOps.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace CivicOps.Services;

public sealed record ServiceAccountCreateRequest(string Name, string[] Scopes, DateTimeOffset ExpiresAt);
public sealed record ServiceAccountRotateRequest(int OverlapMinutes = 5, DateTimeOffset? ExpiresAt = null);
public sealed record ServiceAccountView(string Id, string Name, string TenantId, string[] Scopes,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? PreviousTokenValidUntil);
public sealed record ServiceAccountIssued(ServiceAccountView Account, string Token);
public sealed record EnterpriseAuditRecord(string TenantId, long Sequence, string Action, string Actor, string Detail,
    DateTimeOffset At, string PreviousHash, string StateHash, string Hash);

/// <summary>Tenant-bound, non-interactive credentials. Clear tokens exist only in create/rotate responses.</summary>
public sealed class ServiceAccountStore(IConfiguration configuration, IWebHostEnvironment environment, TimeProvider time, IncidentStore incidents, DurableStorage? persistence = null) : IDisposable
{
    public void Dispose() => _gate.Dispose();
    public static readonly string[] AllowedScopes = ["tools:invoke", "zendesk:webhook", "training:read", "training:write", "incidents:read", "incidents:write", "audit:read", "integrations:write"];
    private sealed record Account(ServiceAccountView View, string TokenHash, string? PreviousTokenHash);
    private sealed record Snapshot(List<Account> Accounts, List<EnterpriseAuditRecord> Audit);
    private readonly string _path = Path.Combine(DataPaths.Resolve(configuration, environment), "service-accounts.json");
    private readonly DurableStorage _storage = persistence ?? new DurableStorage(configuration, environment);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private Snapshot? _snapshot;

    public async Task<IReadOnlyList<ServiceAccountView>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Accounts.Where(account => account.View.TenantId == tenantId).Select(account => Copy(account.View)).ToArray(); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<ServiceAccountIssued> CreateAsync(string tenantId, ServiceAccountCreateRequest request, string actor, CancellationToken ct = default)
    {
        Validate(tenantId, request, time.GetUtcNow());
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct);
            if (current.Accounts.Any(account => account.View.TenantId == tenantId && account.View.Name == request.Name)) throw new InvalidOperationException("Service account name already exists in this municipality.");
            var token = NewToken();
            var view = new ServiceAccountView("sa-" + Guid.NewGuid().ToString("N"), request.Name, tenantId,
                request.Scopes.Distinct().Order().ToArray(), time.GetUtcNow(), request.ExpiresAt, null, null);
            var next = new Snapshot([.. current.Accounts, new(view, EnterpriseAudit.Hash(token), null)], [.. current.Audit]);
            await SaveAsync(next, tenantId, "service-account.created", actor, view.Id, ct);
            return new(Copy(view), token);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<ServiceAccountIssued?> RotateAsync(string tenantId, string id, ServiceAccountRotateRequest request, string actor, CancellationToken ct = default)
    {
        if (request.OverlapMinutes is < 0 or > 60) throw new InvalidOperationException("Rotation overlap must be between 0 and 60 minutes.");
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct); var account = current.Accounts.SingleOrDefault(value => value.View.Id == id && value.View.TenantId == tenantId);
            if (account is null) return null;
            if (account.View.RevokedAt is not null) throw new InvalidOperationException("Revoked service accounts cannot be rotated.");
            var expires = request.ExpiresAt ?? account.View.ExpiresAt;
            Validate(tenantId, new(account.View.Name, account.View.Scopes, expires), time.GetUtcNow());
            var token = NewToken(); var until = time.GetUtcNow().AddMinutes(request.OverlapMinutes);
            if (until > account.View.ExpiresAt) until = account.View.ExpiresAt;
            var rotated = new Account(account.View with { ExpiresAt = expires, PreviousTokenValidUntil = until }, EnterpriseAudit.Hash(token), account.TokenHash);
            var next = new Snapshot(current.Accounts.Select(value => value.View.Id == id ? rotated : value).ToList(), [.. current.Audit]);
            await SaveAsync(next, tenantId, "service-account.rotated", actor, id + ":overlap-minutes=" + request.OverlapMinutes, ct);
            return new(Copy(rotated.View), token);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<ServiceAccountView?> RevokeAsync(string tenantId, string id, string actor, CancellationToken ct = default)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct); var account = current.Accounts.SingleOrDefault(value => value.View.Id == id && value.View.TenantId == tenantId);
            if (account is null) return null;
            if (account.View.RevokedAt is not null) return Copy(account.View);
            var revoked = account with { View = account.View with { RevokedAt = time.GetUtcNow(), PreviousTokenValidUntil = null }, PreviousTokenHash = null };
            var next = new Snapshot(current.Accounts.Select(value => value.View.Id == id ? revoked : value).ToList(), [.. current.Audit]);
            await SaveAsync(next, tenantId, "service-account.revoked", actor, id, ct); return Copy(revoked.View);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<ClaimsPrincipal?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        if (!token.StartsWith("co_sa_", StringComparison.Ordinal) || token.Length is < 40 or > 128) return null;
        var supplied = EnterpriseAudit.Hash(token);
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var account = (await LoadAsync(ct)).Accounts.FirstOrDefault(value => value.View.RevokedAt is null && value.View.ExpiresAt > time.GetUtcNow()
                && (EnterpriseAudit.EqualHash(value.TokenHash, supplied) || (value.View.PreviousTokenValidUntil > time.GetUtcNow()
                    && value.PreviousTokenHash is not null && EnterpriseAudit.EqualHash(value.PreviousTokenHash, supplied))));
            if (account is null) return null;
            var identity = new ClaimsIdentity("civicops-service-account", ClaimTypes.Name, ClaimTypes.Role);
            identity.AddClaim(new(ClaimTypes.NameIdentifier, account.View.Id)); identity.AddClaim(new(ClaimTypes.Name, account.View.Name));
            identity.AddClaim(new("civicops:tenant", account.View.TenantId)); identity.AddClaim(new("civicops:service-account", account.View.Id));
            foreach (var scope in account.View.Scopes) identity.AddClaim(new("civicops:scope", scope));
            if (account.View.Scopes.Any(scope => scope.StartsWith("training:", StringComparison.Ordinal))) identity.AddClaim(new(ClaimTypes.Role, "training-client"));
            if (account.View.Scopes.Contains("audit:read")) identity.AddClaim(new(ClaimTypes.Role, "auditor"));
            if (account.View.Scopes.Any(scope => scope.StartsWith("tools:", StringComparison.Ordinal) || scope.StartsWith("incidents:", StringComparison.Ordinal)
                || scope.StartsWith("integrations:", StringComparison.Ordinal) || scope == "zendesk:webhook")) identity.AddClaim(new(ClaimTypes.Role, "agent"));
            return new(identity);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        if (!_storage.IsPostgres && _snapshot is not null) return _snapshot;
        var persisted = await _storage.ReadAsync(_path, ct);
        var loaded = persisted is not null ? JsonSerializer.Deserialize<Snapshot>(persisted, _json)
            ?? throw new InvalidOperationException("Invalid service-account snapshot.") : new([], []);
        if (loaded.Accounts.Any(account => !loaded.Audit.Any(record => record.TenantId == account.View.TenantId))) throw new InvalidOperationException("Unaudited service-account state.");
        EnterpriseAudit.Verify(loaded.Audit, tenant => EnterpriseAudit.Hash(JsonSerializer.Serialize(loaded.Accounts.Where(value => value.View.TenantId == tenant).ToArray())));
        foreach (var record in loaded.Audit) await EnterpriseAudit.MirrorAsync(incidents, record, ct);
        return _snapshot = loaded;
    }
    private async Task SaveAsync(Snapshot next, string tenantId, string action, string actor, string detail, CancellationToken ct)
    {
        var record = EnterpriseAudit.Append(next.Audit, tenantId, action, actor, detail, time.GetUtcNow(),
            EnterpriseAudit.Hash(JsonSerializer.Serialize(next.Accounts.Where(value => value.View.TenantId == tenantId).ToArray())));
        next.Audit.Add(record);
        await _storage.WriteAsync(_path, JsonSerializer.Serialize(next, _json), ct); _snapshot = next;
        await EnterpriseAudit.MirrorAsync(incidents, record, ct);
    }
    private static string NewToken() => "co_sa_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static ServiceAccountView Copy(ServiceAccountView view) => view with { Scopes = [.. view.Scopes] };
    private static void Validate(string tenantId, ServiceAccountCreateRequest request, DateTimeOffset now)
    {
        if (!IdentityOptions.ValidTenantId(tenantId) || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 || request.Name.Any(char.IsControl)
            || request.Scopes is not { Length: > 0 and <= 8 } || request.Scopes.Any(scope => !AllowedScopes.Contains(scope, StringComparer.Ordinal))
            || request.Scopes.Distinct().Count() != request.Scopes.Length || request.ExpiresAt <= now || request.ExpiresAt > now.AddDays(366))
            throw new InvalidOperationException("Service accounts require a name, explicit supported scopes and expiry within 366 days.");
    }
}

internal static class EnterpriseAudit
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool EqualHash(string left, string right) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
    private static string Compute(EnterpriseAuditRecord record) => Hash(JsonSerializer.Serialize(record with { Hash = "" }));
    public static EnterpriseAuditRecord Append(List<EnterpriseAuditRecord> records, string tenant, string action, string actor, string detail, DateTimeOffset at, string state)
    {
        var prior = records.LastOrDefault(record => record.TenantId == tenant);
        var record = new EnterpriseAuditRecord(tenant, (prior?.Sequence ?? 0) + 1, action, actor, detail, at, prior?.Hash ?? AuditLog.Genesis, state, "");
        return record with { Hash = Compute(record) };
    }
    public static void Verify(List<EnterpriseAuditRecord> records, Func<string, string> state)
    {
        foreach (var group in records.GroupBy(record => record.TenantId))
        {
            long sequence = 0; var previous = AuditLog.Genesis;
            foreach (var record in group)
            {
                if (record.Sequence != ++sequence || record.PreviousHash != previous || record.Hash != Compute(record)) throw new InvalidOperationException("Enterprise identity audit integrity check failed.");
                previous = record.Hash;
            }
            if (group.Last().StateHash != state(group.Key)) throw new InvalidOperationException("Enterprise identity snapshot differs from its audit state.");
        }
    }
    public static async Task MirrorAsync(IncidentStore incidents, EnterpriseAuditRecord record, CancellationToken ct)
    {
        using var scope = TenantContext.Use(record.TenantId);
        var marker = "identity-audit:" + record.Hash;
        if (!(await incidents.AuditAsync(ct)).Any(entry => entry.Detail.Contains(marker, StringComparison.Ordinal)))
            await incidents.RecordSystemEventAsync(new AuditEvent(record.Action, record.Actor, record.Detail + ";" + marker, record.At), ct);
    }
}
