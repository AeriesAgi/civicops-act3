using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CivicOps.Models;
using Npgsql;
using NpgsqlTypes;

namespace CivicOps.Services;

/// <summary>File or PostgreSQL authoritative persistence for all municipal durable records.</summary>
public sealed class DurableStorage : IAsyncDisposable
{
    private const long TransactionLock = 0x43495649434F5053;
    private readonly string _root;
    private readonly NpgsqlDataSource? _source;
    private readonly NpgsqlDataSource? _leadershipSource;
    private readonly string? _schema;
    private readonly AsyncLocal<Session?> _session = new();
    private readonly Dictionary<string, string> _migrationHashes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    public bool IsPostgres => _source is not null;
    public bool MigrationsReady { get; private set; }
    public string Mode => IsPostgres ? "postgres" : "file";
    // Session advisory locks live on a dedicated unpooled connection, separate from snapshot transactions.
    // A failed/terminated connection releases leadership; delivery claims remain the correctness fence.
    public async Task<WorkerLease?> TryAcquireOutboxLeadershipAsync(CancellationToken ct)
    {
        if (!IsPostgres) return new WorkerLease(null, 0);
        var key = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(
            SHA256.HashData(Encoding.UTF8.GetBytes("civicops-outbox-leader:" + (_schema ?? "public"))));
        var connection = _leadershipSource!.CreateConnection();
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(ct) is true) return new WorkerLease(connection, key);
        }
        catch { await connection.DisposeAsync(); throw; }
        await connection.DisposeAsync();
        return null;
    }
    public sealed class WorkerLease : IAsyncDisposable
    {
        private NpgsqlConnection? _connection;
        private readonly long _key;
        internal WorkerLease(NpgsqlConnection? connection, long key) { _connection = connection; _key = key; }
        public async ValueTask DisposeAsync()
        {
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is null) return;
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection) { CommandTimeout = 3 };
                command.Parameters.AddWithValue("key", _key);
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception error) when (error is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                // This connection is never pooled. Closing its physical session releases any
                // uncertain lock ownership without consuming/resetting an application pool slot.
            }
            finally { await connection.DisposeAsync(); }
        }
    }
    public DurableStorage(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _root = DataPaths.Resolve(configuration, environment);
        var mode = configuration["CIVICOPS_STORAGE"]?.Trim().ToLowerInvariant();
        if (mode is not (null or "" or "file" or "postgres")) throw new InvalidOperationException("CIVICOPS_STORAGE must be file or postgres.");
        if (mode != "postgres") { MigrationsReady = true; return; }
        var configured = SecretValues.Read(configuration, "CIVICOPS_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("PostgreSQL storage requires CIVICOPS_DATABASE_URL or CIVICOPS_DATABASE_URL_FILE.");
        try
        {
            var connection = ParseConnectionString(configured);
            _schema = string.IsNullOrWhiteSpace(connection.SearchPath) ? null : connection.SearchPath;
            if (_schema is not null && (_schema.Length > 63 || !_schema.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')))
                throw new ArgumentException("Invalid database schema.");
            connection.IncludeErrorDetail = false;
            connection.LogParameters = false;
            connection.Timeout = Math.Min(connection.Timeout, 10);
            connection.CommandTimeout = Math.Min(connection.CommandTimeout, 30);
            // Transactions and session advisory locks need stable physical connection ownership.
            // Preserve accepted connection strings, but never enable multiplexing for municipal state.
            connection.Multiplexing = false;
            var leadershipConnection = new NpgsqlConnectionStringBuilder(connection.ConnectionString)
            { Pooling = false, Multiplexing = false };
            _source = CreateSource(connection, "civicops-storage");
            _leadershipSource = CreateSource(leadershipConnection, "civicops-outbox-leadership");
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        {
            _source?.Dispose(); _leadershipSource?.Dispose();
            throw new InvalidOperationException("PostgreSQL connection configuration is invalid.");
        }
    }
    private static NpgsqlDataSource CreateSource(NpgsqlConnectionStringBuilder connection, string name)
    {
        var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString) { Name = name };
        builder.ConfigureTracing(options => options.ConfigureCommandSpanNameProvider(_ => "database.command").ConfigureBatchSpanNameProvider(_ => "database.batch"));
        return builder.Build();
    }
    public static NpgsqlConnectionStringBuilder ParseConnectionString(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return new(value);
        var uri = new Uri(value); var credentials = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host, Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : ""
        };
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = pair.Split('=', 2); var setting = Uri.UnescapeDataString(fields[0]);
            var content = fields.Length == 2 ? Uri.UnescapeDataString(fields[1]) : "";
            if (setting.Equals("sslmode", StringComparison.OrdinalIgnoreCase)) builder.SslMode = Enum.Parse<SslMode>(content, true);
            else if (setting.Equals("search_path", StringComparison.OrdinalIgnoreCase)) builder.SearchPath = content;
            else throw new ArgumentException("Unsupported PostgreSQL URL option.");
        }
        return builder;
    }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (MigrationsReady) return;
        await _migrationGate.WaitAsync(ct);
        try
        {
            if (MigrationsReady) return;
            await using var connection = await _source!.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using (var locked = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
            { locked.Parameters.AddWithValue("key", TransactionLock); await locked.ExecuteNonQueryAsync(ct); }
            if (_schema is not null)
            {
                await using var createSchema = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS \"{_schema}\"", connection, transaction);
                await createSchema.ExecuteNonQueryAsync(ct);
            }
            await using (var ledger = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS civicops_schema_migrations (version text PRIMARY KEY, sha256 text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", connection, transaction))
                await ledger.ExecuteNonQueryAsync(ct);
            var assembly = typeof(DurableStorage).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(name => name.Contains("Storage.Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                await using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream); var sql = await reader.ReadToEndAsync(ct);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
                var version = name[(name.LastIndexOf("Migrations.", StringComparison.Ordinal) + "Migrations.".Length)..];
                _migrationHashes[version] = hash;
                await using var check = new NpgsqlCommand("SELECT sha256 FROM civicops_schema_migrations WHERE version=@version", connection, transaction);
                check.Parameters.AddWithValue("version", version); var previous = await check.ExecuteScalarAsync(ct) as string;
                if (previous is not null)
                { if (previous != hash) throw new InvalidDataException("Applied database migration checksum differs from the embedded SQL."); continue; }
                await using var migration = new NpgsqlCommand(sql, connection, transaction); await migration.ExecuteNonQueryAsync(ct);
                await using var record = new NpgsqlCommand("INSERT INTO civicops_schema_migrations(version,sha256) VALUES (@version,@hash)", connection, transaction);
                record.Parameters.AddWithValue("version", version); record.Parameters.AddWithValue("hash", hash); await record.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct); MigrationsReady = true;
        }
        finally { _migrationGate.Release(); }
    }
    // Begin is synchronous so the ambient holder is installed in the calling execution context.
    public TransactionScope Begin()
    {
        if (!IsPostgres) return new(this, null, false);
        if (_session.Value is { Closed: false } current) return new(this, current, false);
        var session = new Session(); _session.Value = session; return new(this, session, true);
    }
    private async Task StartAsync(Session session, CancellationToken ct)
    {
        if (session.Connection is not null) return;
        await InitializeAsync(ct);
        session.Connection = await _source!.OpenConnectionAsync(ct);
        session.Transaction = await session.Connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", session.Connection, session.Transaction);
        command.Parameters.AddWithValue("key", TransactionLock); await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await using var scope = Begin(); await scope.InitializeAsync(ct);
        try { return await action(); } catch { scope.Abort(); throw; }
    }
    public async Task RunAsync(Func<Task> action, CancellationToken ct)
    {
        await using var scope = Begin(); await scope.InitializeAsync(ct);
        try { await action(); } catch { scope.Abort(); throw; }
    }
    public async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        if (!IsPostgres) return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
        return await RunAsync(async () =>
        {
            await using var command = Command("SELECT payload FROM civicops_documents WHERE path=@path");
            command.Parameters.AddWithValue("path", Key(path)); return await command.ExecuteScalarAsync(ct) as string;
        }, ct);
    }
    public async Task WriteAsync(string path, string payload, CancellationToken ct)
    {
        if (!IsPostgres) { await AtomicJsonFile.WriteAsync(path, payload, ct); return; }
        await RunAsync(async () =>
        {
            var key = Key(path); var kind = Kind(key); var tenant = TenantFor(key);
            await using var command = Command("INSERT INTO civicops_documents(path,entity_kind,tenant_id,payload) VALUES (@path,@kind,@tenant,@payload) ON CONFLICT(path) DO UPDATE SET payload=excluded.payload,revision=civicops_documents.revision+1,updated_at=now()");
            command.Parameters.AddWithValue("path", key); command.Parameters.AddWithValue("kind", kind);
            command.Parameters.AddWithValue("tenant", tenant); command.Parameters.AddWithValue("payload", payload); await command.ExecuteNonQueryAsync(ct);
            if (kind == "incidents") await ProjectIncidentsAsync(tenant, payload, ct);
            else if (kind == "integration-outbox") await ProjectOutboxAsync(tenant, payload, ct);
        }, ct);
    }
    public async Task<IReadOnlyList<string>> ListPathsAsync(string directory, string suffix, CancellationToken ct)
    {
        if (!IsPostgres) return Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Where(path => path.EndsWith(suffix, StringComparison.Ordinal)).ToArray() : [];
        return await RunAsync<IReadOnlyList<string>>(async () =>
        {
            await using var command = Command("SELECT path FROM civicops_documents WHERE starts_with(path,@prefix) AND right(path,length(@suffix))=@suffix ORDER BY path");
            command.Parameters.AddWithValue("prefix", Key(directory) + "/"); command.Parameters.AddWithValue("suffix", suffix);
            var paths = new List<string>(); await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) paths.Add(Path.Combine(_root, reader.GetString(0))); return paths;
        }, ct);
    }
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        if (!IsPostgres) { File.Delete(path); return; }
        await RunAsync(async () => { await using var command = Command("DELETE FROM civicops_documents WHERE path=@path"); command.Parameters.AddWithValue("path", Key(path)); await command.ExecuteNonQueryAsync(ct); }, ct);
    }
    public async Task<int> PendingOutboxCountAsync(CancellationToken ct)
    {
        if (!IsPostgres) return 0;
        await using var connection = await _source!.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM civicops_outbox WHERE state='Pending'", connection);
        return checked((int)(long)(await command.ExecuteScalarAsync(ct))!);
    }
    public async Task<bool> HealthCheckAsync(CancellationToken ct)
    {
        if (!MigrationsReady) return false;
        if (!IsPostgres)
        {
            var probe = Path.Combine(_root, ".readiness-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                { await stream.WriteAsync(new byte[] { 0 }, ct); await stream.FlushAsync(ct); stream.Flush(flushToDisk: true); }
                File.Delete(probe); return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
            finally
            {
                try { if (File.Exists(probe)) File.Delete(probe); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        try
        {
            await using var connection = await _source!.OpenConnectionAsync(ct);
            foreach (var expected in _migrationHashes)
            {
                await using var migration = new NpgsqlCommand("SELECT sha256 FROM civicops_schema_migrations WHERE version=@version", connection);
                migration.Parameters.AddWithValue("version", expected.Key);
                if (await migration.ExecuteScalarAsync(ct) as string != expected.Value) return false;
            }
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var probe = new NpgsqlCommand("INSERT INTO civicops_documents(path,entity_kind,tenant_id,payload) VALUES (@path,'readiness-probe','default','{}')", connection, transaction);
            probe.Parameters.AddWithValue("path", ".readiness/" + Guid.NewGuid().ToString("N")); await probe.ExecuteNonQueryAsync(ct);
            await transaction.RollbackAsync(ct); return true;
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or InvalidOperationException) { return false; }
    }
    internal NpgsqlCommand Command(string sql)
    {
        var session = _session.Value ?? throw new InvalidOperationException("A storage transaction is required.");
        return new(sql, session.Connection, session.Transaction);
    }
    private string Key(string path)
    {
        var relative = Path.GetRelativePath(_root, Path.GetFullPath(path)).Replace(Path.DirectorySeparatorChar, '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) throw new InvalidOperationException("Durable paths must remain inside the data directory.");
        return relative;
    }
    private static string Kind(string key) => key.Contains("/email-outbox/", StringComparison.Ordinal) || key.StartsWith("email-outbox/", StringComparison.Ordinal) ? "email-outbox"
        : key.Contains("/zendesk-outbox/", StringComparison.Ordinal) || key.StartsWith("zendesk-outbox/", StringComparison.Ordinal) ? "zendesk-outbox" : Path.GetFileNameWithoutExtension(key);
    private static string TenantFor(string key) => key.StartsWith("tenants/", StringComparison.Ordinal) ? key.Split('/')[1] : TenantStore.DefaultTenantId;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private async Task ProjectIncidentsAsync(string tenant, string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload); var root = doc.RootElement;
        var incidents = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("incidents");
        var auditCount = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("audit", out var snapshotAudit) ? snapshotAudit.GetArrayLength() : 0;
        await using (var prior = Command("SELECT coalesce(max(sequence),0) FROM civicops_audit WHERE tenant_id=@tenant"))
        {
            prior.Parameters.AddWithValue("tenant", tenant);
            if ((long)(await prior.ExecuteScalarAsync(ct))! > auditCount) throw new InvalidDataException("Durable audit history cannot be truncated or replaced by a legacy snapshot.");
        }
        await using (var clear = Command("DELETE FROM civicops_incidents WHERE tenant_id=@tenant")) { clear.Parameters.AddWithValue("tenant", tenant); await clear.ExecuteNonQueryAsync(ct); }
        // One server-side projection per collection, instead of one round trip per historical row.
        // These statements still join the snapshot transaction and its cross-instance write lock.
        await using (var insert = Command("INSERT INTO civicops_incidents(tenant_id,id,version,intake_key_hash,body) SELECT @tenant, item->>'id', (item->>'version')::integer, CASE WHEN jsonb_typeof(item->'intakeKeyHash')='string' THEN item->>'intakeKeyHash' END, item FROM jsonb_array_elements(@items) AS item"))
        {
            insert.Parameters.AddWithValue("tenant", tenant);
            insert.Parameters.AddWithValue("items", NpgsqlDbType.Jsonb, incidents.GetRawText());
            await insert.ExecuteNonQueryAsync(ct);
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("audit", out var audit)) return;
        await using (var insert = Command("INSERT INTO civicops_audit(tenant_id,sequence,hash,previous_hash,body) SELECT @tenant, (item->>'sequence')::bigint, item->>'hash', item->>'previousHash', item FROM jsonb_array_elements(@items) AS item ON CONFLICT(tenant_id,sequence) DO UPDATE SET hash=civicops_audit.hash WHERE civicops_audit.hash=excluded.hash AND civicops_audit.previous_hash=excluded.previous_hash AND civicops_audit.body=excluded.body"))
        {
            insert.Parameters.AddWithValue("tenant", tenant);
            insert.Parameters.AddWithValue("items", NpgsqlDbType.Jsonb, audit.GetRawText());
            if (await insert.ExecuteNonQueryAsync(ct) != auditCount) throw new InvalidDataException("Durable audit sequence cannot be rewritten.");
        }
    }
    private async Task ProjectOutboxAsync(string tenant, string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        await using (var clear = Command("DELETE FROM civicops_outbox WHERE tenant_id=@tenant")) { clear.Parameters.AddWithValue("tenant", tenant); await clear.ExecuteNonQueryAsync(ct); }
        await using (var insert = Command("INSERT INTO civicops_outbox(tenant_id,id,deduplication_key,state,next_attempt_at,body) SELECT @tenant, item->>'id', item->>'deduplicationKey', item->>'state', (item->>'nextAttemptAt')::timestamptz, item FROM jsonb_array_elements(@items) AS item"))
        {
            insert.Parameters.AddWithValue("tenant", tenant);
            insert.Parameters.AddWithValue("items", NpgsqlDbType.Jsonb, payload);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }
    public async Task StageIncidentIntentsAsync(string outboxPath, IReadOnlyList<Incident> incidents, DateTimeOffset now, CancellationToken ct)
    {
        if (!IsPostgres) return;
        var text = await ReadAsync(outboxPath, ct); var items = text is null ? [] : JsonSerializer.Deserialize<List<IntegrationOutboxItem>>(text, Json)!;
        var changed = false;
        foreach (var incident in incidents.Where(incident => !incident.PrivacyAnonymised))
        {
            var intents = new List<(string Operation, string Key, string? Text)> { ("create", $"{incident.Id}:create", null) };
            var index = incident.Audit.FindLastIndex(entry => entry.Action is "approved" or "assigned" or "status");
            if (index >= 0) intents.Add(("status", $"{incident.Id}:status:{index}", null));
            intents.AddRange(incident.CommsDrafts.Where(draft => draft.State == "Approved" && draft.ApprovedAt is not null && !string.IsNullOrWhiteSpace(draft.ApprovedBy))
                .Select(draft => ("approved-comment", $"{incident.Id}:comms:{draft.Id}", (string?)draft.Text)));
            foreach (var intent in intents)
            {
                if (items.Any(item => item.DeduplicationKey == intent.Key)) continue;
                if (items.Count(item => item.State == IntegrationDeliveryState.Pending) >= 10_000) throw new InvalidOperationException("Case record queue is full; dispatcher review required.");
                var item = new IntegrationOutboxItem { TenantId = TenantContext.CurrentId, DeduplicationKey = intent.Key, IncidentId = incident.Id, CaseReference = incident.ReferenceNumber,
                    Operation = intent.Operation, CreatedAt = now, NextAttemptAt = now };
                item.RequestJson = ZendeskClient.BuildTicketRequest(incident, intent.Operation, item.Id, intent.Text); items.Add(item); changed = true;
            }
        }
        if (changed) await WriteAsync(outboxPath, JsonSerializer.Serialize(items, Json), ct);
    }
    internal sealed class Session
    { public NpgsqlConnection? Connection; public NpgsqlTransaction? Transaction; public bool Aborted; public bool Closed; }
    public sealed class TransactionScope : IAsyncDisposable
    {
        private readonly DurableStorage _owner; private readonly Session? _session; private readonly bool _root;
        internal TransactionScope(DurableStorage owner, Session? session, bool root) { _owner = owner; _session = session; _root = root; }
        public Task InitializeAsync(CancellationToken ct) => _session is null ? Task.CompletedTask : _owner.StartAsync(_session, ct);
        public void Abort() { if (_session is not null) _session.Aborted = true; }
        public async ValueTask DisposeAsync()
        {
            if (!_root || _session is null || _session.Closed) return;
            try
            {
                if (_session.Transaction is not null)
                {
                    if (_session.Aborted)
                    {
                        // Broken connections may already have rolled back server-side. Preserve the original failure.
                        try { await _session.Transaction.RollbackAsync(); }
                        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or TimeoutException) { }
                    }
                    else await _session.Transaction.CommitAsync();
                }
            }
            finally
            {
                _session.Closed = true;
                try { if (_session.Transaction is not null) await _session.Transaction.DisposeAsync(); }
                catch (Exception error) when (_session.Aborted && error is NpgsqlException or InvalidOperationException or TimeoutException) { }
                finally
                {
                    try { if (_session.Connection is not null) await _session.Connection.DisposeAsync(); }
                    catch (Exception error) when (_session.Aborted && error is NpgsqlException or InvalidOperationException or TimeoutException) { }
                }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        try { if (_leadershipSource is not null) await _leadershipSource.DisposeAsync(); }
        finally
        {
            try { if (_source is not null) await _source.DisposeAsync(); }
            finally { _migrationGate.Dispose(); }
        }
    }
}

public static class DurableStorageRegistration
{
    public static IServiceCollection AddCivicOpsStorage(this IServiceCollection services)
    { services.AddSingleton<DurableStorage>(); return services; }
}
