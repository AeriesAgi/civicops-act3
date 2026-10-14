using System.Text;

namespace CivicOps.Services;

// Publish a flushed snapshot by replacing a file on the same filesystem.
public static class AtomicJsonFile
{
    public static async Task WriteAsync(string path, string json, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(json), ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class DataDirectoryLease : IDisposable
{
    private readonly FileStream? _lease;
    public DataDirectoryLease(CivicOpsOptions options, DurableStorage? persistence = null)
    {
        try { Directory.CreateDirectory(options.DataDirectory); }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"CIVICOPS_DATA_DIR '{options.DataDirectory}' is not writable by the container user (uid 1654). Use /app/Data (the image default) or mount a writable volume there.", ex);
        }
        if (persistence?.IsPostgres == true) return;
        try
        {
            _lease = new FileStream(Path.Combine(options.DataDirectory, ".civicops.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"CIVICOPS_DATA_DIR '{options.DataDirectory}' is not writable by the container user (uid 1654). For an Azure Files volume add mount options uid=1654,gid=1654, or remove the volume mount.", ex);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("CIVICOPS_DATA_DIR is already in use by another CivicOps process. Use one process per data directory.");
        }
    }
    public void Dispose() => _lease?.Dispose();
}
