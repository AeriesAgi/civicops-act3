using CivicOps.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CivicOps.Tests;

public sealed class ScalingBoundaryTests
{
    public static IEnumerable<object[]> ConnectionModes()
    {
        foreach (var backend in StoreBackends.Available())
        {
            yield return [backend[0], false];
            yield return [backend[0], true];
        }
    }

    [Theory]
    [MemberData(nameof(ConnectionModes))]
    public async Task LeadershipLeavesSingleApplicationPoolSlotAvailableForStateTransactions(string backend, bool multiplexingConfigured)
    {
        using var selected = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var client = app.CreateClient();
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        if (backend == "postgres")
        {
            var connection = new NpgsqlConnectionStringBuilder(configuration["CIVICOPS_DATABASE_URL"]!)
            { Pooling = true, MaxPoolSize = 1, MinPoolSize = 0, Multiplexing = multiplexingConfigured };
            configuration = new ConfigurationBuilder().AddConfiguration(configuration)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_DATABASE_URL"] = connection.ConnectionString }).Build();
        }
        var environment = app.Services.GetRequiredService<IWebHostEnvironment>();
        await using var storage = new DurableStorage(configuration, environment);
        await using var peer = new DurableStorage(configuration, environment);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await storage.InitializeAsync(timeout.Token);
        await using var lease = await storage.TryAcquireOutboxLeadershipAsync(timeout.Token);
        Assert.NotNull(lease);
        await using var contested = await peer.TryAcquireOutboxLeadershipAsync(timeout.Token);
        if (backend == "postgres") Assert.Null(contested);
        else Assert.NotNull(contested);
        var path = Path.Combine(app.DataDirectory, "synthetic-leadership-pool-contract.json");
        const string payload = "{\"synthetic\":true,\"purpose\":\"single-pool-slot leadership regression\"}";
        // With pooled leadership, MaxPoolSize=1 deadlocks this authoritative transaction/read.
        // Configured multiplexing must also retain transaction and session-lock semantics.
        await storage.RunAsync(async () =>
        {
            await storage.WriteAsync(path, payload, timeout.Token);
            Assert.Equal(payload, await storage.ReadAsync(path, timeout.Token));
        }, timeout.Token);
        Assert.Equal(payload, await storage.ReadAsync(path, timeout.Token));
        Assert.True(await storage.HealthCheckAsync(timeout.Token));
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task AdvisoryLeadershipExcludesPeersAndHandsOverAfterRelease(string backend)
    {
        using var selected = StoreBackends.Use(backend);
        using var app = new TestApp { EnableBackgroundDelivery = false };
        using var client = app.CreateClient();
        var first = app.Services.GetRequiredService<DurableStorage>();
        await using var second = new DurableStorage(app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<IWebHostEnvironment>());
        var lease = await first.TryAcquireOutboxLeadershipAsync(default);
        Assert.NotNull(lease);
        var contested = await second.TryAcquireOutboxLeadershipAsync(default);
        if (backend == "postgres") Assert.Null(contested);
        else { Assert.NotNull(contested); await contested.DisposeAsync(); }
        // Leadership never blocks ordinary state transactions or readiness.
        Assert.True(await first.HealthCheckAsync(default));
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await using var handedOver = await second.TryAcquireOutboxLeadershipAsync(default);
        Assert.NotNull(handedOver);
    }

    [Fact]
    public async Task SingleNodeSseDropsOldRefreshesAndCompletesDisconnectedReaders()
    {
        var stream = new CaseEventStream();
        var listener = stream.Subscribe();
        for (var count = 0; count < 50; count++) stream.Publish();
        stream.Publish("training.completed");
        var buffered = new List<string>();
        while (listener.Reader.TryRead(out var value)) buffered.Add(value.Type);
        Assert.Equal(16, buffered.Count);
        Assert.Equal("training.completed", buffered[^1]);
        Assert.All(buffered, type => Assert.Contains(type, new[] { "refresh", "training.completed" }));
        stream.Unsubscribe(listener.Id);
        Assert.False(await listener.Reader.WaitToReadAsync());
        stream.Unsubscribe(listener.Id);
    }

    [Fact]
    public void TrainingSseFiltersRefreshesAndInstancesDoNotPretendToFanOut()
    {
        var first = new CaseEventStream(); var second = new CaseEventStream();
        var training = first.Subscribe(true); var remote = second.Subscribe();
        first.Publish(); Assert.False(training.Reader.TryRead(out _));
        first.Publish("certification.granted");
        Assert.True(training.Reader.TryRead(out var change)); Assert.Equal("certification.granted", change.Type);
        Assert.False(remote.Reader.TryRead(out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Publish("synthetic-unknown"));
        first.Unsubscribe(training.Id); second.Unsubscribe(remote.Id);
    }
}
