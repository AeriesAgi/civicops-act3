using CivicOps.Services;
using Npgsql;

namespace CivicOps.Tests;

/// <summary>The existing storage contracts run unchanged against each configured backend.</summary>
public static class StoreBackends
{
    private static readonly AsyncLocal<string?> Selection = new();
    public static IEnumerable<object[]> Available()
    {
        yield return ["file"];
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CIVICOPS_TEST_DATABASE_URL"))) yield return ["postgres"];
    }
    public static string Current => Selection.Value ?? "file";
    public static IDisposable Use(string backend)
    {
        var previous = Selection.Value; Selection.Value = backend; return new Lease(previous);
    }
    private sealed class Lease(string? previous) : IDisposable
    { public void Dispose() => Selection.Value = previous; }
    public static string TestConnection(string directory)
    {
        var value = Environment.GetEnvironmentVariable("CIVICOPS_TEST_DATABASE_URL") ?? throw new InvalidOperationException("PostgreSQL tests require CIVICOPS_TEST_DATABASE_URL.");
        var builder = DurableStorage.ParseConnectionString(value);
        builder.SearchPath = "civicops_test_" + IncidentStore.Hash(directory)[..24].ToLowerInvariant();
        builder.MaxPoolSize = 3;
        return builder.ConnectionString;
    }
    public static void DeletePartition(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var schema = builder.SearchPath; builder.SearchPath = "public"; builder.Pooling = false;
        using var connection = new NpgsqlConnection(builder.ConnectionString); connection.Open();
        using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection); command.ExecuteNonQuery();
    }
}
