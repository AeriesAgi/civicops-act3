using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

/// <summary>Municipality directory. Operational records live in independent municipality partitions.</summary>
public sealed class TenantStore(IConfiguration configuration, IWebHostEnvironment environment,
    IncidentStore audit, TimeProvider time, DurableStorage? persistence = null) : IDisposable
{
    public void Dispose() => _gate.Dispose();
    public const string DefaultTenantId = "default";
    private readonly string _path = Path.Combine(DataPaths.Resolve(configuration, environment), "tenants.json");
    private readonly DurableStorage _storage = persistence ?? new DurableStorage(configuration, environment);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private List<Tenant>? _tenants;
    public async Task<IReadOnlyList<Tenant>> ListAsync(CancellationToken ct = default)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Select(Clone).ToArray(); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<Tenant?> GetAsync(string id, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(tenant => tenant.Id == id);
    public async Task<Tenant?> ResolveSlugAsync(string slug, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(tenant => tenant.Slug == slug);
    public async Task<Tenant?> ResolveHostAsync(string host, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(tenant => tenant.ResidentHost is not null
            && tenant.ResidentHost.Equals(host, StringComparison.OrdinalIgnoreCase));
    public async Task<Tenant> SaveAsync(Tenant tenant, string actor, CancellationToken ct = default)
    {
        Validate(tenant);
        if (string.IsNullOrWhiteSpace(actor)) throw new InvalidOperationException("An authenticated actor is required.");
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct);
            if (current.Any(value => value.Id != tenant.Id && (value.Slug == tenant.Slug
                || tenant.ResidentHost is not null && string.Equals(value.ResidentHost, tenant.ResidentHost, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Municipality slug and resident host must be unique.");
            var previous = current.FirstOrDefault(value => value.Id == tenant.Id);
            if (previous is not null && previous.Version != tenant.Version) throw new InvalidOperationException("Municipality changed. Refresh before editing.");
            var saved = Clone(tenant with { Version = previous is null ? 1 : previous.Version + 1 });
            var next = current.Where(value => value.Id != tenant.Id).Append(saved).ToList();
            using (TenantContext.Use(saved.Id))
                await audit.RecordSystemEventAsync(new(previous is null ? "tenant.created" : "tenant.updated", actor,
                    $"Municipality {saved.Id}; directory version {saved.Version}.", time.GetUtcNow()), ct);
            await _storage.WriteAsync(_path, JsonSerializer.Serialize(next, _json), ct);
            _tenants = next;
            return Clone(saved);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public static void Validate(Tenant tenant)
    {
        if (!TenantContext.IsValidId(tenant.Id) || !TenantContext.IsValidId(tenant.Slug)
            || InvalidText(tenant.Name, 120) || InvalidText(tenant.ServiceArea, 500)
            || tenant.Departments is null || tenant.Departments.Count is < 1 or > 32
            || tenant.Departments.Any(value => InvalidText(value, 80))
            || tenant.Departments.Distinct(StringComparer.Ordinal).Count() != tenant.Departments.Count
            || tenant.LocaleDefaults is null || tenant.LocaleDefaults.Language is not ("en" or "af" or "zu" or "xh" or "st" or "tn" or "ts" or "ss" or "ve" or "nr" or "nso")
            || tenant.LocaleDefaults.TimeZone != "Africa/Johannesburg" || tenant.Version < 1
            || tenant.ResidentHost is not null && (Uri.CheckHostName(tenant.ResidentHost) != UriHostNameType.Dns
                || tenant.ResidentHost.Length > 253 || tenant.ResidentHost.Contains(':')))
            throw new InvalidOperationException("Invalid municipality: safe id/slug, name, departments, South African locale defaults and service area are required.");
    }
    private async Task<List<Tenant>> LoadAsync(CancellationToken ct)
    {
        if (!_storage.IsPostgres && _tenants is not null) return _tenants;
        var persisted = await _storage.ReadAsync(_path, ct);
        if (persisted is not null)
        {
            var loaded = JsonSerializer.Deserialize<List<Tenant>>(persisted, _json)
                ?? throw new InvalidDataException("Invalid municipality directory.");
            foreach (var tenant in loaded) Validate(tenant);
            if (loaded.Select(value => value.Id).Distinct().Count() != loaded.Count
                || loaded.Select(value => value.Slug).Distinct().Count() != loaded.Count
                || loaded.Where(value => value.ResidentHost is not null).Select(value => value.ResidentHost!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != loaded.Count(value => value.ResidentHost is not null))
                throw new InvalidDataException("Duplicate municipality directory identities.");
            return _tenants = loaded;
        }
        var departments = new[] { "Water and Sanitation", "Electricity", "Roads and Stormwater", "Waste Management", "Emergency Services", "Public Works" };
        var demo = bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var enabled) && enabled;
        var seeded = new List<Tenant> { new(DefaultTenantId, demo ? "Synthetic Inland Municipality" : "Default municipality (owner configuration required)",
            "inland", departments, new(), demo ? "Fictitious inland service area; synthetic reports only." : "Owner service-area configuration pending.", demo) };
        if (demo) seeded.Add(new("synthetic-coast", "Synthetic Coastal Municipality", "coast", departments, new("af"),
            "Fictitious coastal service area; synthetic reports only."));
        await _storage.WriteAsync(_path, JsonSerializer.Serialize(seeded, _json), ct);
        return _tenants = seeded;
    }
    private Tenant Clone(Tenant tenant) => tenant with { Departments = tenant.Departments.ToArray() };
    private static bool InvalidText(string? text, int bound) => string.IsNullOrWhiteSpace(text) || text.Length > bound || text.Any(char.IsControl);
}
