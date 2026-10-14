using Microsoft.Extensions.FileProviders;
using CivicOps.Models;

namespace CivicOps.Services;

public static class StorageCommand
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "--migrate" or "--import-file-store";
    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, string contentRoot)
    {
        try
        {
            if (args is not (["--migrate"] or ["--import-file-store", _])) throw new InvalidOperationException("Usage: --migrate | --import-file-store DIRECTORY");
            var environment = new CommandEnvironment { ContentRootPath = contentRoot };
            await using var storage = new DurableStorage(configuration, environment);
            if (!storage.IsPostgres) throw new InvalidOperationException("Storage commands require CIVICOPS_STORAGE=postgres.");
            await storage.InitializeAsync();
            if (args[0] == "--migrate") { Console.WriteLine("PostgreSQL embedded migrations verified and applied."); return 0; }
            var count = await ImportAsync(storage, args[1], configuration, environment, CancellationToken.None);
            Console.WriteLine($"Imported {count} durable documents; municipal audit chains and authenticated snapshots verified.");
            return 0;
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or System.Text.Json.JsonException or Npgsql.NpgsqlException or ArgumentException)
        { Console.Error.WriteLine("Storage command failed safely (" + error.GetType().Name + "). Check configuration and a stopped, verified source; no credentials or source values are logged."); return 1; }
    }
    public static async Task<int> ImportAsync(DurableStorage storage, string sourceDirectory, IConfiguration configuration,
        IWebHostEnvironment environment, CancellationToken ct)
    {
        if (!storage.IsPostgres) throw new InvalidOperationException("Import target must use PostgreSQL.");
        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Import source is absent.");
        for (var ancestor = new DirectoryInfo(source); ancestor is not null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Import source cannot contain a linked root or ancestor.");
        using var sourceLease = new DataDirectoryLease(new CivicOpsOptions { DataDirectory = source });
        var target = DataPaths.Resolve(configuration, environment);
        var known = new HashSet<string>(StringComparer.Ordinal) { "tenants.json", "incidents.json", "resident-contacts.json", "integration-outbox.json", "workforce.json", "training.json",
            "service-accounts.json", "bootstrap-users.json", "catalogue-revisions.json", "privacy-requests.json", "elevenlabs-webhooks.json", "geo.json" };
        var candidates = new List<string>(); var directories = new Stack<string>(); directories.Push(source);
        while (directories.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Import cannot follow symbolic links.");
                if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry); else candidates.Add(entry);
            }
        var files = candidates.Where(path =>
        {
            var relative = Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/');
            var segments = relative.Split('/'); var local = segments[0] == "tenants" ? segments.Skip(2).ToArray() : segments;
            if (segments[0] == "tenants" && (segments.Length < 3 || !TenantContext.IsValidId(segments[1]))) throw new InvalidDataException("Invalid source municipality partition.");
            return local.Length == 1 && known.Contains(local[0]) || local.Length == 2 && (local[0] == "email-outbox" && local[1].EndsWith(".eml", StringComparison.Ordinal)
                || local[0] == "zendesk-outbox" && local[1].EndsWith(".json", StringComparison.Ordinal));
        }).Order(StringComparer.Ordinal).ToArray();
        return await storage.RunAsync(async () =>
        {
            await using (var count = storage.Command("SELECT count(*) FROM civicops_documents"))
                if ((long)(await count.ExecuteScalarAsync(ct))! != 0) throw new InvalidOperationException("Import requires an empty PostgreSQL target; existing municipal state is never overwritten.");
            foreach (var path in files)
                await storage.WriteAsync(Path.Combine(target, Path.GetRelativePath(source, path)), await File.ReadAllTextAsync(path, ct), ct);
            using var incidents = new IncidentStore(configuration, environment, persistence: storage);
            using var tenants = new TenantStore(configuration, environment, incidents, TimeProvider.System, storage);
            using var workforce = new WorkforceStore(configuration, environment, WorkOrderCatalog.Baseline, TimeProvider.System, storage);
            using var training = new TrainingStore(configuration, environment, WorkOrderCatalog.Baseline, workforce,
                new TrainingAccess(configuration), incidents, new CaseEventStream(), TimeProvider.System, storage);
            using var accounts = new ServiceAccountStore(configuration, environment, TimeProvider.System, incidents, storage);
            using var users = new BootstrapUserStore(configuration, environment, TimeProvider.System, incidents, storage);
            using var catalogue = new CatalogueAdminStore(configuration, environment, incidents, TimeProvider.System, storage);
            using var privacy = new PrivacyService(configuration, environment, incidents, TimeProvider.System, persistence: storage);
            var receipts = new ElevenLabsChannelState(configuration, environment, storage);
            using var geo = new GeoStore(configuration, environment, storage, TimeProvider.System, new CaseEventStream());
            var ids = new HashSet<string>((await tenants.ListAsync(ct)).Select(tenant => tenant.Id), StringComparer.Ordinal) { TenantStore.DefaultTenantId };
            foreach (var path in files)
            {
                var pieces = Path.GetRelativePath(source, path).Split(Path.DirectorySeparatorChar);
                if (pieces.Length > 2 && pieces[0] == "tenants") ids.Add(pieces[1]);
            }
            foreach (var id in ids)
            {
                using var partition = TenantContext.Use(id);
                if (!(await incidents.VerifyAuditAsync(ct)).Valid || !(await workforce.VerifyAsync(ct)).Valid || !(await training.VerifyAsync(ct)).Valid)
                    throw new InvalidDataException("Import source audit integrity failed.");
                _ = await accounts.ListAsync(id, ct); _ = await users.ListAsync(id, ct);
                _ = await catalogue.GetAsync(ct); _ = await privacy.ListAsync(ct); _ = await receipts.ReadAsync(ct);
                _ = await geo.ReadAsync(ct);
            }
            return files.Length;
        }, ct);
    }
    private sealed class CommandEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "CivicOps";
        public string EnvironmentName { get; set; } = "Production";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
