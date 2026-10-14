using System.Text.Json;

namespace CivicOps.Services;

/// <summary>
/// Hard spend cut-offs that survive restarts. Counters live in CIVICOPS_DATA_DIR/spend-caps.json and reset per UTC day/month.
/// CIVICOPS_LLM_DAILY_CALL_CAP (2000), CIVICOPS_LLM_MONTHLY_CALL_CAP (20000),
/// CIVICOPS_VOICE_DAILY_SESSION_CAP (100), CIVICOPS_VOICE_MONTHLY_SESSION_CAP (1000). 0 disables the live lane entirely.
/// </summary>
public sealed class SpendCaps
{
    public const string Llm = "llm";
    public const string Voice = "voice";
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Daily, long Monthly)> _caps;
    private State _state = new();

    public SpendCaps(string? dataDirectory, TimeProvider time, long llmDaily = 2000, long llmMonthly = 20000, long voiceDaily = 100, long voiceMonthly = 1000)
    {
        _time = time;
        _caps = new() { [Llm] = (llmDaily, llmMonthly), [Voice] = (voiceDaily, voiceMonthly) };
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            _path = Path.Combine(dataDirectory, "spend-caps.json");
            try { if (File.Exists(_path)) _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new(); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _state = new(); }
        }
    }

    public static SpendCaps From(IConfiguration configuration, string? dataDirectory, TimeProvider time) => new(dataDirectory, time,
        Cap(configuration["CIVICOPS_LLM_DAILY_CALL_CAP"], 2000), Cap(configuration["CIVICOPS_LLM_MONTHLY_CALL_CAP"], 20000),
        Cap(configuration["CIVICOPS_VOICE_DAILY_SESSION_CAP"], 100), Cap(configuration["CIVICOPS_VOICE_MONTHLY_SESSION_CAP"], 1000));

    private static long Cap(string? value, long fallback) => long.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;

    /// <summary>Atomically reserves one unit; false when the daily or monthly cap is reached (nothing is consumed).</summary>
    public bool TryConsume(string lane)
    {
        lock (_gate)
        {
            Roll();
            var (daily, monthly) = _caps[lane];
            var counter = _state.Lanes.TryGetValue(lane, out var existing) ? existing : new Counter();
            if (counter.Day >= daily || counter.Month >= monthly) return false;
            _state.Lanes[lane] = new Counter { Day = counter.Day + 1, Month = counter.Month + 1 };
            Persist();
            return true;
        }
    }

    public const string Label = "Spend cap reached. Simulation mode";

    public bool Reached(string lane)
    {
        lock (_gate)
        {
            Roll();
            var counter = _state.Lanes.TryGetValue(lane, out var existing) ? existing : new Counter();
            return counter.Day >= _caps[lane].Daily || counter.Month >= _caps[lane].Monthly;
        }
    }

    public object Status(string lane)
    {
        lock (_gate)
        {
            Roll();
            var counter = _state.Lanes.TryGetValue(lane, out var existing) ? existing : new Counter();
            return new { used_today = counter.Day, daily_cap = _caps[lane].Daily, used_this_month = counter.Month, monthly_cap = _caps[lane].Monthly };
        }
    }

    private void Roll()
    {
        var now = _time.GetUtcNow();
        var day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var month = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        if (_state.Month != month) { _state = new State { Day = day, Month = month }; return; }
        if (_state.Day != day)
        {
            _state.Day = day;
            foreach (var key in _state.Lanes.Keys.ToList()) _state.Lanes[key] = _state.Lanes[key] with { Day = 0 };
        }
    }

    private void Persist()
    {
        if (_path is null) return;
        try
        {
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in-memory counter still enforces the cap */ }
    }

    private sealed class State
    {
        public string Day { get; set; } = "";
        public string Month { get; set; } = "";
        public Dictionary<string, Counter> Lanes { get; set; } = new();
    }

    private sealed record Counter
    {
        public long Day { get; init; }
        public long Month { get; init; }
    }
}
