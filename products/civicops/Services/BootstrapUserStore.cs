using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace CivicOps.Services;

public sealed record BootstrapUserCreateRequest(string Subject, string DisplayName, string[] Roles, string? CrewId = null);
public sealed record BootstrapUserUpdateRequest(string DisplayName, string[] Roles, string? CrewId = null);
public sealed record BootstrapUserView(string Id, string Subject, string DisplayName, string TenantId, string[] Roles,
    string? CrewId, DateTimeOffset CreatedAt, DateTimeOffset TokenExpiresAt, DateTimeOffset? RevokedAt);
public sealed record BootstrapUserIssued(BootstrapUserView User, string Token);

/// <summary>Explicitly administrator-issued local access for installations without SSO. No passwords or clear tokens are stored.</summary>
public sealed class BootstrapUserStore(IConfiguration configuration, IWebHostEnvironment environment, TimeProvider time, IncidentStore incidents, DurableStorage? persistence = null) : IDisposable
{
    public void Dispose() => _gate.Dispose();
    private readonly string? _configuredAdminHash = ValidateConfiguredAdmin(configuration);
    private readonly string _configuredAdminTenant = configuration["CIVICOPS_BOOTSTRAP_ADMIN_TENANT"]?.Trim() is { Length: > 0 } tenant ? tenant : TenantStore.DefaultTenantId;
    private sealed record User(BootstrapUserView View, string TokenHash, int Generation);
    private sealed record Snapshot(List<User> Users, List<EnterpriseAuditRecord> Audit);
    private readonly string _path = Path.Combine(DataPaths.Resolve(configuration, environment), "bootstrap-users.json");
    private readonly DurableStorage _storage = persistence ?? new DurableStorage(configuration, environment);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private Snapshot? _snapshot;
    public async Task<IReadOnlyList<BootstrapUserView>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Users.Where(value => value.View.TenantId == tenantId).Select(value => Copy(value.View)).ToArray(); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<BootstrapUserIssued> CreateAsync(string tenantId, BootstrapUserCreateRequest request, string actor, CancellationToken ct = default)
    {
        Validate(tenantId, request);
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct);
            if (current.Users.Any(user => user.View.TenantId == tenantId && user.View.Subject == request.Subject)) throw new InvalidOperationException("User subject already exists in this municipality.");
            var token = NewToken();
            var view = new BootstrapUserView("usr-" + Guid.NewGuid().ToString("N"), request.Subject, request.DisplayName, tenantId,
                request.Roles.Order().ToArray(), request.CrewId, time.GetUtcNow(), time.GetUtcNow().AddDays(30), null);
            var next = new Snapshot([.. current.Users, new(view, EnterpriseAudit.Hash(token), 1)], [.. current.Audit]);
            await SaveAsync(next, tenantId, "user.created", actor, view.Id, ct); return new(Copy(view), token);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<BootstrapUserView?> UpdateAsync(string tenantId, string id, BootstrapUserUpdateRequest request, string actor, CancellationToken ct = default)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct); var user = current.Users.SingleOrDefault(value => value.View.Id == id && value.View.TenantId == tenantId);
            if (user is null) return null;
            Validate(tenantId, new(user.View.Subject, request.DisplayName, request.Roles, request.CrewId));
            if (user.View.RevokedAt is not null) throw new InvalidOperationException("Revoked users cannot be changed.");
            var changed = user with { View = user.View with { DisplayName = request.DisplayName, Roles = request.Roles.Order().ToArray(), CrewId = request.CrewId } };
            var next = new Snapshot(current.Users.Select(value => value.View.Id == id ? changed : value).ToList(), [.. current.Audit]);
            await SaveAsync(next, tenantId, "user.roles-updated", actor, id, ct); return Copy(changed.View);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<BootstrapUserIssued?> RotateAsync(string tenantId, string id, string actor, CancellationToken ct = default)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct); var user = current.Users.SingleOrDefault(value => value.View.Id == id && value.View.TenantId == tenantId);
            if (user is null) return null;
            if (user.View.RevokedAt is not null) throw new InvalidOperationException("Revoked users cannot be rotated.");
            var token = NewToken(); var changed = user with { TokenHash = EnterpriseAudit.Hash(token), Generation = user.Generation + 1,
                View = user.View with { TokenExpiresAt = time.GetUtcNow().AddDays(30) } };
            var next = new Snapshot(current.Users.Select(value => value.View.Id == id ? changed : value).ToList(), [.. current.Audit]);
            await SaveAsync(next, tenantId, "user.token-rotated", actor, id, ct); return new(Copy(changed.View), token);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<BootstrapUserView?> RevokeAsync(string tenantId, string id, string actor, CancellationToken ct = default)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct); var user = current.Users.SingleOrDefault(value => value.View.Id == id && value.View.TenantId == tenantId);
            if (user is null) return null;
            if (user.View.RevokedAt is not null) return Copy(user.View);
            var changed = user with { View = user.View with { RevokedAt = time.GetUtcNow() } };
            var next = new Snapshot(current.Users.Select(value => value.View.Id == id ? changed : value).ToList(), [.. current.Audit]);
            await SaveAsync(next, tenantId, "user.revoked", actor, id, ct); return Copy(changed.View);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<ClaimsPrincipal?> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        if (_configuredAdminHash is not null && token.Length is >= 16 and <= 1024 && EnterpriseAudit.EqualHash(_configuredAdminHash, EnterpriseAudit.Hash(token)))
        {
            var identity = new ClaimsIdentity(EnterpriseIdentity.SessionScheme, ClaimTypes.Name, ClaimTypes.Role);
            identity.AddClaim(new(ClaimTypes.NameIdentifier, "configured-bootstrap-admin")); identity.AddClaim(new(ClaimTypes.Name, "Configured bootstrap administrator"));
            identity.AddClaim(new(ClaimTypes.Role, "admin")); identity.AddClaim(new("civicops:tenant", _configuredAdminTenant));
            identity.AddClaim(new("civicops:bootstrap-config-admin", "true")); return new(identity);
        }
        if (!token.StartsWith("co_usr_", StringComparison.Ordinal) || token.Length is < 40 or > 128) return null;
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var hash = EnterpriseAudit.Hash(token); var user = (await LoadAsync(ct)).Users.FirstOrDefault(value => Active(value)
                && EnterpriseAudit.EqualHash(value.TokenHash, hash)); return user is null ? null : Principal(user);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<ClaimsPrincipal?> PrincipalForUserAsync(string id, CancellationToken ct = default)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { var user = (await LoadAsync(ct)).Users.SingleOrDefault(value => value.View.Id == id && Active(value)); return user is null ? null : Principal(user); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private bool Active(User user) => user.View.RevokedAt is null && user.View.TokenExpiresAt > time.GetUtcNow();
    private static ClaimsPrincipal Principal(User user)
    {
        var identity = new ClaimsIdentity(EnterpriseIdentity.SessionScheme, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new(ClaimTypes.NameIdentifier, user.View.Subject)); identity.AddClaim(new(ClaimTypes.Name, user.View.DisplayName));
        identity.AddClaim(new("civicops:tenant", user.View.TenantId)); identity.AddClaim(new("civicops:bootstrap-user", user.View.Id));
        identity.AddClaim(new("civicops:bootstrap-generation", user.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var role in user.View.Roles) identity.AddClaim(new(ClaimTypes.Role, role));
        if (user.View.CrewId is not null) identity.AddClaim(new("civicops:crew", user.View.CrewId));
        return new(identity);
    }
    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        if (!_storage.IsPostgres && _snapshot is not null) return _snapshot;
        var persisted = await _storage.ReadAsync(_path, ct);
        var loaded = persisted is not null ? JsonSerializer.Deserialize<Snapshot>(persisted, _json)
            ?? throw new InvalidOperationException("Invalid bootstrap-user snapshot.") : new([], []);
        if (loaded.Users.Any(user => !loaded.Audit.Any(record => record.TenantId == user.View.TenantId))) throw new InvalidOperationException("Unaudited bootstrap-user state.");
        EnterpriseAudit.Verify(loaded.Audit, tenant => EnterpriseAudit.Hash(JsonSerializer.Serialize(loaded.Users.Where(value => value.View.TenantId == tenant).ToArray())));
        foreach (var record in loaded.Audit) await EnterpriseAudit.MirrorAsync(incidents, record, ct);
        return _snapshot = loaded;
    }
    private async Task SaveAsync(Snapshot next, string tenantId, string action, string actor, string detail, CancellationToken ct)
    {
        var record = EnterpriseAudit.Append(next.Audit, tenantId, action, actor, detail, time.GetUtcNow(),
            EnterpriseAudit.Hash(JsonSerializer.Serialize(next.Users.Where(value => value.View.TenantId == tenantId).ToArray())));
        next.Audit.Add(record); await _storage.WriteAsync(_path, JsonSerializer.Serialize(next, _json), ct); _snapshot = next;
        await EnterpriseAudit.MirrorAsync(incidents, record, ct);
    }
    private static string NewToken() => "co_usr_" + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static BootstrapUserView Copy(BootstrapUserView view) => view with { Roles = [.. view.Roles] };
    private static void Validate(string tenant, BootstrapUserCreateRequest request)
    {
        if (!IdentityOptions.ValidTenantId(tenant) || string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 128 || request.Subject.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 160 || request.DisplayName.Any(char.IsControl)
            || request.Roles is not { Length: > 0 and <= 8 } || request.Roles.Any(role => !IdentityOptions.Roles.Contains(role, StringComparer.Ordinal))
            || request.Roles.Distinct().Count() != request.Roles.Length || (request.Roles.Contains("crew") && string.IsNullOrWhiteSpace(request.CrewId))
            || request.CrewId is { Length: > 80 } || (request.CrewId?.Any(char.IsControl) ?? false))
            throw new InvalidOperationException("Users require bounded subject/display name, supported roles and a crew binding for the crew role.");
    }
    public static string? ValidateConfiguredAdmin(IConfiguration configuration)
    {
        var hash = SecretValues.Read(configuration, "CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH")?.Trim(); var tenant = configuration["CIVICOPS_BOOTSTRAP_ADMIN_TENANT"]?.Trim();
        if (string.IsNullOrWhiteSpace(hash))
        {
            if (!string.IsNullOrWhiteSpace(tenant)) throw new InvalidOperationException("Bootstrap administrator municipality requires CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH.");
            return null;
        }
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !string.IsNullOrWhiteSpace(tenant) && !IdentityOptions.ValidTenantId(tenant))
            throw new InvalidOperationException("Bootstrap administrator requires a SHA256 token hash and valid municipality id.");
        return hash;
    }
}
