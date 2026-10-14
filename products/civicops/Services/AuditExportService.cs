using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CivicOps.Services;

public sealed record AuditExportManifest(string Format, string ExportId, string TenantId, DateTimeOffset ExportedAt,
    int Entries, long? FirstSequence, long? LastSequence, string ChainHead, string Sha256, long ByteLength,
    string SignatureAlgorithm, string SignatureKeyId, string Signature);
public sealed record AuditExportBundle(byte[] Jsonl, AuditExportManifest Manifest);
public sealed record AuditExportVerification(bool Valid, int Entries, string Message);
public sealed class AuditExportUnavailableException() : Exception("Signed audit export is disabled until a valid signing key is configured.");

/// <summary>Tenant-bound signed exports. Local targets reject replacement; host administrators still control the filesystem.</summary>
public sealed class AuditExportService(IncidentStore store, IConfiguration configuration, TimeProvider time)
{
    public const string Format = "CivicOpsAuditExport/v1";
    public const int MaximumBytes = 64 * 1024 * 1024;
    public const int MaximumEntries = 100000;
    public const int MaximumLineBytes = 65536;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public async Task<AuditExportBundle> CreateAsync(CancellationToken ct)
    {
        var key = SigningKey(configuration);
        var configuredKeyId = configuration["CIVICOPS_AUDIT_SIGNING_KEY_ID"];
        var keyId = string.IsNullOrWhiteSpace(configuredKeyId) ? "owner-key" : configuredKeyId;
        if (keyId.Length is < 1 or > 64 || keyId.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_'))
            throw new AuditExportUnavailableException();
        var records = await store.AuditAsync(ct);
        if (records.Count > MaximumEntries || !AuditLog.Verify(records).Valid || !TenantMatches(records, TenantContext.CurrentId))
            throw new InvalidDataException("Audit export source exceeds its bounds or fails integrity verification.");
        using var output = new MemoryStream();
        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            var line = JsonSerializer.SerializeToUtf8Bytes(record, Json);
            if (line.Length > MaximumLineBytes || output.Length + line.Length + 1 > MaximumBytes)
                throw new InvalidDataException("Audit export exceeds its size limit.");
            output.Write(line); output.WriteByte((byte)'\n');
        }
        var bytes = output.ToArray();
        var first = records.Count == 0 ? null : records[0]; var last = records.Count == 0 ? null : records[^1];
        var manifest = new AuditExportManifest(Format, "audit-" + Guid.NewGuid().ToString("N"), TenantContext.CurrentId,
            time.GetUtcNow().ToUniversalTime(), records.Count, first?.Sequence,
            last?.Sequence, last?.Hash ?? AuditLog.Genesis,
            Hash(bytes), bytes.Length, "HMAC-SHA256", keyId, "");
        return new(bytes, manifest with { Signature = Sign(manifest, key) });
    }

    public async Task<(string ExportId, AuditExportManifest Manifest)> WriteConfiguredAsync(CancellationToken ct)
    {
        var target = configuration["CIVICOPS_AUDIT_EXPORT_DIR"];
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
            throw new AuditExportUnavailableException();
        EnsureNoLinks(target);
        Directory.CreateDirectory(target);
        var bundle = await CreateAsync(ct);
        await WriteExclusiveAsync(Path.Combine(target, bundle.Manifest.ExportId), bundle, ct);
        return (bundle.Manifest.ExportId, bundle.Manifest);
    }

    public static async Task WriteExclusiveAsync(string directory, AuditExportBundle bundle, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(directory) || Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("Audit export requires a new absolute output directory.");
        EnsureNoLinks(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // A reservation also prevents racing creators from publishing into the same directory.
        await using var reservation = new FileStream(Path.Combine(directory, ".reservation"), FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await WriteNewAsync(Path.Combine(directory, "audit.jsonl"), bundle.Jsonl, ct);
        await WriteNewAsync(Path.Combine(directory, "manifest.json"), ManifestBytes(bundle.Manifest), ct);
        await WriteNewAsync(Path.Combine(directory, "COMPLETE"), Utf8.GetBytes(bundle.Manifest.ExportId + "\n"), ct);
        // Read-only files are an additional guard, not a claim of kernel-enforced or cloud WORM storage.
        foreach (var file in new[] { "audit.jsonl", "manifest.json", "COMPLETE" })
        {
            var path = Path.Combine(directory, file);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead);
        }
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            BufferSize = 16384, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(path, options);
        await file.WriteAsync(bytes, ct); await file.FlushAsync(ct); file.Flush(true);
    }

    public static byte[] Archive(AuditExportBundle bundle)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var stream = archive.CreateEntry("audit.jsonl").Open()) stream.Write(bundle.Jsonl);
            using (var stream = archive.CreateEntry("manifest.json").Open()) stream.Write(ManifestBytes(bundle.Manifest));
        }
        return output.ToArray();
    }

    public static byte[] ManifestBytes(AuditExportManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, Json);

    public static AuditExportVerification Verify(byte[] jsonl, byte[] manifestBytes, IConfiguration configuration)
    {
        try
        {
            if (jsonl.Length > MaximumBytes || manifestBytes.Length > MaximumLineBytes) return Invalid();
            CheckDuplicateProperties(manifestBytes);
            var manifest = JsonSerializer.Deserialize<AuditExportManifest>(manifestBytes, Json);
            if (manifest is null || manifest.Format != Format || manifest.SignatureAlgorithm != "HMAC-SHA256"
                || !TenantContext.IsValidId(manifest.TenantId) || manifest.Entries is < 0 or > MaximumEntries
                || manifest.ByteLength != jsonl.Length || manifest.Sha256 != Hash(jsonl)
                || manifest.ExportId is null || manifest.ExportId.Length != 38 || !manifest.ExportId.StartsWith("audit-", StringComparison.Ordinal)
                || !Guid.TryParseExact(manifest.ExportId[6..], "N", out _)
                || manifest.SignatureKeyId is null || manifest.SignatureKeyId.Length is < 1 or > 64
                || manifest.SignatureKeyId.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_')) return Invalid();
            var supplied = Convert.FromBase64String(manifest.Signature);
            var expected = Convert.FromBase64String(Sign(manifest with { Signature = "" }, SigningKey(configuration)));
            if (!CryptographicOperations.FixedTimeEquals(supplied, expected)) return Invalid();
            var records = new List<AuditRecord>();
            var start = 0;
            for (var index = 0; index < jsonl.Length; index++)
            {
                if (index - start > MaximumLineBytes) return Invalid();
                if (jsonl[index] != (byte)'\n') continue;
                if (index == start || records.Count >= MaximumEntries) return Invalid();
                var line = jsonl.AsSpan(start, index - start);
                CheckDuplicateProperties(line);
                var record = JsonSerializer.Deserialize<AuditRecord>(line, Json);
                if (record is null) return Invalid();
                records.Add(record); start = index + 1;
            }
            if (start != jsonl.Length || records.Count != manifest.Entries || !TenantMatches(records, manifest.TenantId)
                || records.FirstOrDefault()?.Sequence != manifest.FirstSequence || records.LastOrDefault()?.Sequence != manifest.LastSequence
                || (records.LastOrDefault()?.Hash ?? AuditLog.Genesis) != manifest.ChainHead || !AuditLog.Verify(records).Valid) return Invalid();
            return new(true, records.Count, "Signature, checksum, tenant binding, counts and audit chain verified. External anchoring is not attested.");
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or InvalidDataException)
        { return Invalid(); }
    }

    public static async Task<AuditExportVerification> VerifyDirectoryAsync(string directory, IConfiguration configuration, CancellationToken ct)
    {
        EnsureNoLinks(directory);
        var jsonlPath = Path.Combine(directory, "audit.jsonl"); var manifestPath = Path.Combine(directory, "manifest.json");
        EnsureNoLinks(jsonlPath); EnsureNoLinks(manifestPath);
        var jsonl = await ReadBoundedAsync(jsonlPath, MaximumBytes, ct);
        var manifest = await ReadBoundedAsync(manifestPath, MaximumLineBytes, ct);
        return jsonl is null || manifest is null ? Invalid() : Verify(jsonl, manifest, configuration);
    }

    private static async Task<byte[]?> ReadBoundedAsync(string path, int maximum, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length > maximum) return null;
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        // Concurrent growth must not make an untrusted artifact allocate beyond the verification limit.
        if (stream.ReadByte() != -1) return null;
        return bytes;
    }

    private static void CheckDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException();
    }

    private static bool TenantMatches(IEnumerable<AuditRecord> records, string tenant) =>
        records.All(record => record.TenantId == tenant || record.TenantId is null && tenant == TenantStore.DefaultTenantId);

    private static string SigningKey(IConfiguration configuration)
    {
        var key = SecretValues.Read(configuration, "CIVICOPS_AUDIT_SIGNING_KEY");
        if (key is null || Utf8.GetByteCount(key) < 32) throw new AuditExportUnavailableException();
        return key;
    }

    // v1 authenticates these exact UTF-8 newline-delimited fields; JSON formatting is not part of the signature.
    private static string Sign(AuditExportManifest manifest, string key)
    {
        var canonical = string.Join('\n', manifest.Format, manifest.ExportId, manifest.TenantId,
            manifest.ExportedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            manifest.Entries.ToString(CultureInfo.InvariantCulture), manifest.FirstSequence?.ToString(CultureInfo.InvariantCulture) ?? "",
            manifest.LastSequence?.ToString(CultureInfo.InvariantCulture) ?? "", manifest.ChainHead, manifest.Sha256,
            manifest.ByteLength.ToString(CultureInfo.InvariantCulture), manifest.SignatureAlgorithm, manifest.SignatureKeyId) + "\n";
        return Convert.ToBase64String(HMACSHA256.HashData(Utf8.GetBytes(key), Utf8.GetBytes(canonical)));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static AuditExportVerification Invalid() => new(false, 0, "Audit export integrity verification failed.");

    private static void EnsureNoLinks(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Audit export paths must be absolute.");
        for (FileSystemInfo? info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            info is not null; info = info is DirectoryInfo directory ? directory.Parent : ((FileInfo)info).Directory)
            if (info.LinkTarget is not null) throw new IOException("Audit export paths must not contain symbolic links.");
    }
}

public static class AuditExportEndpoints
{
    public static void MapAuditExportEndpoints(this RouteGroupBuilder routes)
    {
        routes.MapGet("/api/audit/export", async (AuditExportService exports, CancellationToken ct) =>
        {
            try
            {
                var bundle = await exports.CreateAsync(ct);
                return Results.File(AuditExportService.Archive(bundle), "application/zip", bundle.Manifest.ExportId + ".zip");
            }
            catch (AuditExportUnavailableException) { return Results.Problem("Signed audit export is disabled until an owner signing key is configured.", statusCode: 503); }
        }).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
        routes.MapPost("/api/audit/export/local", async (AuditExportService exports, CancellationToken ct) =>
        {
            try
            {
                var result = await exports.WriteConfiguredAsync(ct);
                return Results.Ok(new { result.ExportId, result.Manifest, target = "local exclusive files; host-admin immutability is not attested" });
            }
            catch (AuditExportUnavailableException) { return Results.Problem("Local signed audit export requires an owner signing key and absolute CIVICOPS_AUDIT_EXPORT_DIR.", statusCode: 503); }
        }).WithMetadata(new DispatchRequired()).RequireRateLimiting("dispatch");
    }
}

public static class AuditExportCommand
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);
    public static bool IsVerifyCommand(string[] args) => args.Length >= 2 && args[0] == "audit" && args[1] == "verify";
    public static bool IsExportCommand(string[] args) => args.Length >= 2 && args[0] == "audit" && args[1] == "export";

    public static async Task<int> RunVerifierAsync(string[] args, IConfiguration configuration)
    {
        if (args.Length != 3) { Console.Error.WriteLine("Usage: audit verify <absolute-directory>"); return 2; }
        try
        {
            var result = await AuditExportService.VerifyDirectoryAsync(args[2], configuration, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(result, OutputJson));
            return result.Valid ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or AuditExportUnavailableException or SecretConfigurationException)
        { Console.Error.WriteLine("Audit verification could not read the artifact or configured signing key."); return 2; }
    }

    public static async Task<int> RunExportAsync(string[] args, IServiceProvider services)
    {
        if (args.Length is not (3 or 5) || args.Length == 5 && args[3] != "--tenant")
        { Console.Error.WriteLine("Usage: audit export <new-absolute-directory> [--tenant <municipality-id>]"); return 2; }
        try
        {
            using var tenant = TenantContext.Use(args.Length == 5 ? args[4] : TenantStore.DefaultTenantId);
            if (await services.GetRequiredService<TenantStore>().GetAsync(TenantContext.CurrentId) is null)
            { Console.Error.WriteLine("Audit export municipality was not found."); return 2; }
            var bundle = await services.GetRequiredService<AuditExportService>().CreateAsync(CancellationToken.None);
            await AuditExportService.WriteExclusiveAsync(args[2], bundle, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(bundle.Manifest, OutputJson));
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or AuditExportUnavailableException or SecretConfigurationException or InvalidOperationException)
        { Console.Error.WriteLine("Audit export could not verify its source, use the target, or read the configured signing key."); return 2; }
    }
}
