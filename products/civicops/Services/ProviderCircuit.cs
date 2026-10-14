namespace CivicOps.Services;

public sealed record ProviderHealth(string Status, string Mode, string Circuit, string Reason);

/// <summary>Three consecutive failures open for 30 seconds; a single probe may recover the circuit.</summary>
public sealed class ProviderCircuit(TimeProvider clock, int failureThreshold = 3, TimeSpan? breakDuration = null, Func<Exception, bool>? handlesFailure = null)
{
    private readonly object _gate = new();
    private int _failures;
    private DateTimeOffset? _openUntil;
    private bool _probe;
    private bool _observed;
    private bool _failed;
    public ProviderHealth Describe(bool configured, string simulatedMode = "simulated")
    {
        lock (_gate)
            return !configured ? new("degraded", simulatedMode, "closed", "Provider is simulated or disabled.")
                : new(_failed || !_observed ? "degraded" : "healthy", "live", _openUntil is null ? "closed" : "open",
                    _failed ? "Provider failed or timed out; fallback remains available."
                    : !_observed ? "Configured provider is unverified; no startup network check is performed." : "Last provider operation succeeded; live deployment remains owner-validated.");
    }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout, CancellationToken ct)
    {
        bool probe;
        lock (_gate)
        {
            if (_openUntil is not null && (_openUntil > clock.GetUtcNow() || _probe)) throw new ProviderCircuitOpenException();
            probe = _openUntil is not null;
            if (probe) _probe = true;
        }
        using var budget = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        try
        {
            var result = await operation(linked.Token);
            lock (_gate) { _failures = 0; _openUntil = null; _observed = true; _failed = false; }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            lock (_gate)
            {
                _observed = true; _failed = true;
                var permanent = error is HttpRequestException { StatusCode: not null } http && (int)http.StatusCode.Value is >= 400 and < 500 and not (408 or 429);
                if (!permanent && (handlesFailure?.Invoke(error) ?? true) && ++_failures >= failureThreshold)
                    _openUntil = clock.GetUtcNow() + (breakDuration ?? TimeSpan.FromSeconds(30));
            }
            throw;
        }
        finally { if (probe) lock (_gate) _probe = false; }
    }

    public static TimeSpan TimeoutFrom(IConfiguration configuration) => TimeSpan.FromMilliseconds(
        int.TryParse(configuration["CIVICOPS_PROVIDER_TIMEOUT_MS"], out var milliseconds) ? Math.Clamp(milliseconds, 250, 20_000) : 5_000);
}

public sealed class ProviderCircuitOpenException() : HttpRequestException("Provider circuit is open; operation deferred.");
