using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class AuditExportTests
{
    private const string SyntheticKey = "synthetic-audit-signing-fixture-key-v1";
    private static IConfiguration Config(string? key = SyntheticKey) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_AUDIT_SIGNING_KEY"] = key }).Build();

    private static async Task<AuditExportBundle> Bundle(TestApp app)
    {
        using var client = app.CreateClient();
        await FoundationTests.Create(client);
        return await new AuditExportService(app.Services.GetRequiredService<IncidentStore>(), Config(), TimeProvider.System)
            .CreateAsync(CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task SignedBundleVerifiesWithoutRunningApplicationOrAccessingSourceStore(string backend)
    {
        using var selection = StoreBackends.Use(backend);
        AuditExportBundle bundle;
        using (var app = new TestApp { EnableBackgroundDelivery = false }) bundle = await Bundle(app);
        var result = AuditExportService.Verify(bundle.Jsonl, AuditExportService.ManifestBytes(bundle.Manifest), Config());
        Assert.True(result.Valid); Assert.True(result.Entries > 0);
        Assert.Equal(bundle.Manifest.Entries, result.Entries);
        Assert.Equal(bundle.Jsonl.Length, bundle.Manifest.ByteLength);
        Assert.Equal("HMAC-SHA256", bundle.Manifest.SignatureAlgorithm);
        Assert.DoesNotContain(SyntheticKey, Encoding.UTF8.GetString(AuditExportService.ManifestBytes(bundle.Manifest)));
    }

    [Fact]
    public async Task EmptyAuditAuthenticatesGenesisAndZeroCounts()
    {
        using var app = new TestApp(); _ = app.CreateClient();
        var bundle = await new AuditExportService(app.Services.GetRequiredService<IncidentStore>(), Config(), TimeProvider.System)
            .CreateAsync(CancellationToken.None);
        Assert.Equal(0, bundle.Manifest.Entries); Assert.Empty(bundle.Jsonl);
        Assert.Null(bundle.Manifest.FirstSequence); Assert.Null(bundle.Manifest.LastSequence);
        Assert.Equal(AuditLog.Genesis, bundle.Manifest.ChainHead);
        Assert.True(AuditExportService.Verify(bundle.Jsonl, AuditExportService.ManifestBytes(bundle.Manifest), Config()).Valid);
    }

    [Theory]
    [InlineData("format")] [InlineData("exportId")] [InlineData("tenantId")] [InlineData("exportedAt")]
    [InlineData("entries")] [InlineData("firstSequence")] [InlineData("lastSequence")] [InlineData("chainHead")]
    [InlineData("sha256")] [InlineData("byteLength")] [InlineData("signatureAlgorithm")] [InlineData("signatureKeyId")]
    [InlineData("signature")]
    public async Task EveryManifestClaimIsAuthenticated(string field)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; var bundle = await Bundle(app);
        var manifest = JsonNode.Parse(AuditExportService.ManifestBytes(bundle.Manifest))!;
        if (field is "entries" or "firstSequence" or "lastSequence" or "byteLength") manifest[field] = 999;
        else if (field == "exportedAt") manifest[field] = "2025-01-01T00:00:00+00:00";
        else manifest[field] = "tampered";
        Assert.False(AuditExportService.Verify(bundle.Jsonl, Encoding.UTF8.GetBytes(manifest.ToJsonString()), Config()).Valid);
    }

    [Theory]
    [InlineData("change")] [InlineData("truncate")] [InlineData("remove")] [InlineData("reorder")]
    [InlineData("duplicate")] [InlineData("insert")]
    public async Task ModifiedTruncatedReorderedAndInsertedJsonlCannotVerify(string action)
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; var bundle = await Bundle(app);
        var lines = Encoding.UTF8.GetString(bundle.Jsonl).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        Assert.True(lines.Count >= 2);
        var modified = action switch
        {
            "change" => Encoding.UTF8.GetString(bundle.Jsonl).Replace("CivicOps", "Tampered", StringComparison.Ordinal),
            "truncate" => Encoding.UTF8.GetString(bundle.Jsonl)[..^1],
            "remove" => string.Join('\n', lines.Skip(1)) + "\n",
            "reorder" => string.Join('\n', lines.AsEnumerable().Reverse()) + "\n",
            "duplicate" => string.Join('\n', lines.Prepend(lines[0])) + "\n",
            _ => Encoding.UTF8.GetString(bundle.Jsonl) + "{}\n"
        };
        Assert.NotEqual(Encoding.UTF8.GetString(bundle.Jsonl), modified);
        Assert.False(AuditExportService.Verify(Encoding.UTF8.GetBytes(modified), AuditExportService.ManifestBytes(bundle.Manifest), Config()).Valid);
    }

    [Fact]
    public async Task WrongSigningKeyCannotAuthenticateExport()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; var bundle = await Bundle(app);
        Assert.False(AuditExportService.Verify(bundle.Jsonl, AuditExportService.ManifestBytes(bundle.Manifest),
            Config("synthetic-different-signing-fixture-key-v2")).Valid);
    }

    [Fact]
    public async Task DuplicateManifestPropertiesAreRejected()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; var bundle = await Bundle(app);
        var manifest = Encoding.UTF8.GetString(AuditExportService.ManifestBytes(bundle.Manifest));
        manifest = manifest[..^1] + ",\"entries\":" + bundle.Manifest.Entries + "}";
        Assert.False(AuditExportService.Verify(bundle.Jsonl, Encoding.UTF8.GetBytes(manifest), Config()).Valid);
    }

    [Fact]
    public async Task OfflineManifestIsBoundedBeforeParsing()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; var bundle = await Bundle(app);
        var directory = Path.Combine(app.DataDirectory, "bounded-export"); Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "audit.jsonl"), bundle.Jsonl);
        await File.WriteAllBytesAsync(Path.Combine(directory, "manifest.json"), new byte[AuditExportService.MaximumLineBytes + 1]);
        Assert.False((await AuditExportService.VerifyDirectoryAsync(directory, Config(), CancellationToken.None)).Valid);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("too-short-synthetic-key")]
    public async Task MissingOrShortKeysDisableExport(string? key)
    {
        using var app = new TestApp(); _ = app.CreateClient();
        var service = new AuditExportService(app.Services.GetRequiredService<IncidentStore>(), Config(key), TimeProvider.System);
        await Assert.ThrowsAsync<AuditExportUnavailableException>(() => service.CreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MountedSigningKeyTakesPrecedenceAndSupportsRotationWithoutLeakingKey()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; var bundle = await Bundle(app);
        var keyPath = Path.Combine(app.DataDirectory, "synthetic-key.fixture");
        await File.WriteAllTextAsync(keyPath, SyntheticKey + "\n");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CIVICOPS_AUDIT_SIGNING_KEY"] = "synthetic-invalid-fallback", ["CIVICOPS_AUDIT_SIGNING_KEY_FILE"] = keyPath }).Build();
        Assert.True(AuditExportService.Verify(bundle.Jsonl, AuditExportService.ManifestBytes(bundle.Manifest), config).Valid);
        await File.WriteAllTextAsync(keyPath, SyntheticKey + "-rotated");
        Assert.False(AuditExportService.Verify(bundle.Jsonl, AuditExportService.ManifestBytes(bundle.Manifest), config).Valid);
    }

    [Fact]
    public async Task ExclusiveLocalArtifactsAreReadableOfflineAndCannotBeReplaced()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; var bundle = await Bundle(app);
        var directory = Path.Combine(app.DataDirectory, "exclusive-export");
        await AuditExportService.WriteExclusiveAsync(directory, bundle, CancellationToken.None);
        Assert.True((await AuditExportService.VerifyDirectoryAsync(directory, Config(), CancellationToken.None)).Valid);
        Assert.True(File.Exists(Path.Combine(directory, "COMPLETE")));
        var original = await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json"));
        await Assert.ThrowsAsync<IOException>(() => AuditExportService.WriteExclusiveAsync(directory, bundle, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(Path.Combine(directory, "audit.jsonl")));
    }

    [Fact]
    public async Task LocalTargetRejectsSymbolicLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        using var app = new TestApp { EnableBackgroundDelivery = false }; var bundle = await Bundle(app);
        var link = Path.Combine(app.DataDirectory, "export-link"); Directory.CreateSymbolicLink(link, app.DataDirectory);
        await Assert.ThrowsAsync<IOException>(() => AuditExportService.WriteExclusiveAsync(Path.Combine(link, "new-export"), bundle, CancellationToken.None));
    }

    [Fact]
    public async Task RacingWritersCannotReplaceOrMixArtifacts()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; var bundle = await Bundle(app);
        var directory = Path.Combine(app.DataDirectory, "racing-export");
        async Task<bool> Write()
        {
            try { await AuditExportService.WriteExclusiveAsync(directory, bundle, CancellationToken.None); return true; }
            catch (IOException) { return false; }
        }
        var results = await Task.WhenAll(Write(), Write());
        Assert.Single(results, value => value);
        Assert.True((await AuditExportService.VerifyDirectoryAsync(directory, Config(), CancellationToken.None)).Valid);
    }

    [Theory]
    [InlineData("auditor", true)] [InlineData("supervisor", true)] [InlineData("admin", true)]
    [InlineData("dispatcher", false)] [InlineData("resident", false)] [InlineData("crew", false)]
    [InlineData("agent", false)] [InlineData("training-client", false)]
    public async Task AuditorExportEndpointEnforcesRolePolicy(string role, bool allowed)
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_AUDIT_SIGNING_KEY"] = SyntheticKey;
        using var client = await RbacTests.RoleClient(app, role);
        var response = await client.GetAsync("/api/audit/export");
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        if (!allowed) return;
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.Equal(2, archive.Entries.Count);
        using var jsonl = new MemoryStream(); await archive.GetEntry("audit.jsonl")!.Open().CopyToAsync(jsonl);
        using var manifest = new MemoryStream(); await archive.GetEntry("manifest.json")!.Open().CopyToAsync(manifest);
        Assert.True(AuditExportService.Verify(jsonl.ToArray(), manifest.ToArray(), Config()).Valid);
    }

    [Fact]
    public async Task UnconfiguredDemoExportIsHonestlyDisabled()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_AUDIT_SIGNING_KEY"] = "";
        using var client = await RbacTests.RoleClient(app, "auditor");
        var response = await client.GetAsync("/api/audit/export");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("disabled", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExportIsBoundToAuthenticatedMunicipality()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; app.Settings["CIVICOPS_AUDIT_SIGNING_KEY"] = SyntheticKey;
        using var intake = app.CreateClient(); var incident = await FoundationTests.Create(intake);
        using var auditor = await RbacTests.RoleClient(app, "auditor", tenant: "synthetic-coast");
        var response = await auditor.GetAsync("/api/audit/export"); response.EnsureSuccessStatusCode();
        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        using var manifestReader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        var manifest = JsonSerializer.Deserialize<JsonElement>(await manifestReader.ReadToEndAsync());
        Assert.Equal("synthetic-coast", manifest.GetProperty("tenantId").GetString());
        using var reader = new StreamReader(archive.GetEntry("audit.jsonl")!.Open());
        Assert.DoesNotContain(incident.GetProperty("id").GetString()!, await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task LocalEndpointCreatesDistinctExclusiveExportsAndCookieCsrfApplies()
    {
        using var app = new TestApp(); app.Settings["CIVICOPS_AUDIT_SIGNING_KEY"] = SyntheticKey;
        var directory = Path.Combine(app.DataDirectory, "local-exports"); app.Settings["CIVICOPS_AUDIT_EXPORT_DIR"] = directory;
        using var client = await RbacTests.RoleClient(app, "auditor");
        var first = await client.PostAsync("/api/audit/export/local", null); first.EnsureSuccessStatusCode();
        var second = await client.PostAsync("/api/audit/export/local", null); second.EnsureSuccessStatusCode();
        Assert.Equal(2, Directory.GetDirectories(directory).Length);
        foreach (var export in Directory.GetDirectories(directory))
            Assert.True((await AuditExportService.VerifyDirectoryAsync(export, Config(), CancellationToken.None)).Valid);
        client.DefaultRequestHeaders.Remove("X-CivicOps-CSRF");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/audit/export/local", null)).StatusCode);
    }

    [Fact]
    public async Task OfflineVerifierReturnsFailureAndUsageExitCodesWithoutStartingHost()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; var bundle = await Bundle(app);
        var directory = Path.Combine(app.DataDirectory, "offline-export");
        await AuditExportService.WriteExclusiveAsync(directory, bundle, CancellationToken.None);
        Assert.Equal(0, await AuditExportCommand.RunVerifierAsync(["audit", "verify", directory], Config()));
        Assert.Equal(1, await AuditExportCommand.RunVerifierAsync(["audit", "verify", directory], Config("synthetic-wrong-verifier-key-long-enough")));
        Assert.Equal(2, await AuditExportCommand.RunVerifierAsync(["audit", "verify"], Config()));
    }

    [Fact]
    public async Task ExportCommandSelectsExistingTenantAndRejectsTargetReplacement()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; app.Settings["CIVICOPS_AUDIT_SIGNING_KEY"] = SyntheticKey;
        using var client = app.CreateClient(); await FoundationTests.Create(client);
        var directory = Path.Combine(app.DataDirectory, "cli-export");
        Assert.Equal(0, await AuditExportCommand.RunExportAsync(["audit", "export", directory, "--tenant", "default"], app.Services));
        Assert.True((await AuditExportService.VerifyDirectoryAsync(directory, Config(), CancellationToken.None)).Valid);
        Assert.Equal(2, await AuditExportCommand.RunExportAsync(["audit", "export", directory], app.Services));
        Assert.Equal(2, await AuditExportCommand.RunExportAsync(["audit", "export", directory + "-unknown", "--tenant", "missing-municipality"], app.Services));
    }
}
