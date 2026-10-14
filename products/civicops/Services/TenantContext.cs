namespace CivicOps.Services;

/// <summary>Request-local trusted tenant binding. Only authentication/resident routing may select it.</summary>
public static class TenantContext
{
    public const string DefaultTenantId = TenantStore.DefaultTenantId;
    public const string ClaimType = "civicops:tenant";
    private static readonly AsyncLocal<string?> Current = new();
    public static string CurrentId => Current.Value ?? TenantStore.DefaultTenantId;
    public static IDisposable Use(string tenantId)
    {
        if (!IsValidId(tenantId)) throw new InvalidOperationException("Invalid municipality id.");
        var prior = Current.Value;
        Current.Value = tenantId;
        return new Lease(prior);
    }
    public static bool IsValidId(string? tenantId) => tenantId is { Length: >= 1 and <= 64 }
        && tenantId.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    public static string DirectoryFor(string dataDirectory) => CurrentId == TenantStore.DefaultTenantId
        ? dataDirectory : Path.Combine(dataDirectory, "tenants", CurrentId);
    private sealed class Lease(string? prior) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; Current.Value = prior; _disposed = true; }
    }
}
