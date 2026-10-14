using System.Text.Json;

namespace CivicOps.Services;

/// <summary>Immutable, tenant-bound revisions; the request uses the latest verified catalogue.</summary>
public sealed class CatalogueAdminStore(IConfiguration configuration, IWebHostEnvironment environment, IncidentStore audit, TimeProvider time, DurableStorage? persistence = null) : IDisposable
{
    public void Dispose() => _gate.Dispose();
    private sealed record Revision(int Sequence, string TenantId, string CatalogueJson, string Actor, DateTimeOffset At, string PreviousHash, string Hash);
    private readonly DurableStorage _storage = persistence ?? new DurableStorage(configuration, environment);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string PathName => Path.Combine(TenantContext.DirectoryFor(DataPaths.Resolve(configuration, environment)), "catalogue-revisions.json");
    public async Task<WorkOrderCatalog> GetAsync(CancellationToken ct = default)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var revisions = await LoadAsync(ct);
            return revisions.Count == 0 ? WorkOrderCatalog.Baseline : WorkOrderCatalog.FromJson(revisions[^1].CatalogueJson);
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public static object View(WorkOrderCatalog catalog) => new { catalog.Version, catalog.WorkOrderTypes, catalog.Skills, catalog.CertificationTypes };
    public async Task<WorkOrderCatalog> EditAsync(string expectedVersion, JsonElement catalogue, string actor, CancellationToken ct)
    {
        // Strict unknown/duplicate-key rejection plus the validator implementing the portable schema's bounds and references.
        var next = WorkOrderCatalog.FromJson(catalogue.GetRawText());
        if (WorkOrderCatalog.Baseline.WorkOrderTypes.Any(type => next.Find(type.Code) is null))
            throw new InvalidDataException("Stable classifier work-order codes must be retained in catalogue revisions.");
        if (next.WorkOrderTypes.GroupBy(type => type.Category).Any(group => group.Select(type => type.Department).Distinct().Count() > 1))
            throw new InvalidDataException("Each classifier category must map to one department.");
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var revisions = await LoadAsync(ct);
            var current = revisions.Count == 0 ? WorkOrderCatalog.Baseline : WorkOrderCatalog.FromJson(revisions[^1].CatalogueJson);
            if (expectedVersion != current.Version) throw new CatalogueConflictException("Catalogue changed. Refresh before editing.");
            if (System.Version.Parse(next.Version) <= System.Version.Parse(current.Version)) throw new InvalidDataException("A catalogue edit requires a higher semantic version.");
            var revision = new Revision(revisions.Count + 1, TenantContext.CurrentId, catalogue.GetRawText(), actor, time.GetUtcNow(), revisions.LastOrDefault()?.Hash ?? AuditLog.Genesis, "");
            revision = revision with { Hash = Hash(revision) };
            revisions.Add(revision);
            await _storage.WriteAsync(PathName, JsonSerializer.Serialize(revisions, _json), ct);
            await audit.RecordSystemEventAsync(new("catalogue.edited", actor, $"Catalogue {current.Version} -> {next.Version}; revision SHA256 {revision.Hash}.", revision.At), ct);
            return next;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<List<Revision>> LoadAsync(CancellationToken ct)
    {
        var persisted = await _storage.ReadAsync(PathName, ct);
        if (persisted is null) return [];
        var revisions = JsonSerializer.Deserialize<List<Revision>>(persisted, _json) ?? throw new InvalidDataException("Invalid catalogue revisions.");
        var previous = AuditLog.Genesis;
        for (var i = 0; i < revisions.Count; i++)
        {
            var revision = revisions[i];
            if (revision.Sequence != i + 1 || revision.TenantId != TenantContext.CurrentId || revision.PreviousHash != previous || revision.Hash != Hash(revision))
                throw new InvalidDataException("Catalogue revision chain verification failed.");
            _ = WorkOrderCatalog.FromJson(revision.CatalogueJson);
            previous = revision.Hash;
        }
        // Recover a crash after the durable revision write and before its shared audit projection.
        var shared = await audit.AuditAsync(ct);
        foreach (var revision in revisions)
            if (!shared.Any(record => record.Action == "catalogue.edited" && record.Detail.Contains(revision.Hash, StringComparison.Ordinal)))
                await audit.RecordSystemEventAsync(new("catalogue.edited", revision.Actor,
                    $"Catalogue {WorkOrderCatalog.FromJson(revision.CatalogueJson).Version}; recovered revision SHA256 {revision.Hash}.", revision.At), ct);
        return revisions;
    }
    private static string Hash(Revision revision) => IncidentStore.Hash(JsonSerializer.Serialize(revision with { Hash = "" }));
}
public sealed class CatalogueConflictException(string message) : Exception(message);
public sealed record CatalogueEdit(string ExpectedVersion, JsonElement Catalogue);
