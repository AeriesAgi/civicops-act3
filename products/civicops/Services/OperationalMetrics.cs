using System.Text.Json;

namespace CivicOps.Services;

public sealed record MetricDuration(double? MedianSeconds, int Samples);
public sealed record OperationalMetrics(int Reports, MetricDuration ReportToTriage, MetricDuration ReportToApproval,
    MetricDuration ReportToAssignment, MetricDuration ReportToResolution, double? WithoutPhoneQueueShare,
    int AiDecisions, int FallbackDecisions, double? DuplicateRate, bool SyntheticData, string Method);

public static class AuditMetrics
{
    public static OperationalMetrics Compute(IReadOnlyList<AuditRecord> audit, bool demo)
    {
        var cases = audit.Where(x => x.CaseId != "system").GroupBy(x => x.CaseId).ToArray();
        MetricDuration Duration(string action)
        {
            var values = new List<double>();
            foreach (var events in cases)
            {
                var received = events.FirstOrDefault(x => x.Action == "received");
                var end = events.FirstOrDefault(x => x.Action == action);
                if (received is not null && end is not null && end.At >= received.At)
                    values.Add((end.At - received.At).TotalSeconds);
            }
            values.Sort();
            return new(values.Count == 0 ? null : values.Count % 2 == 1 ? values[values.Count / 2]
                : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2, values.Count);
        }
        var measured = 0; var digital = 0; var duplicates = 0; var ai = 0; var fallback = 0;
        foreach (var events in cases)
        {
            var item = events.FirstOrDefault(x => x.Action == "metrics-intake");
            if (item is null) continue;
            try
            {
                using var document = JsonDocument.Parse(item.Detail);
                var value = document.RootElement;
                var source = value.GetProperty("source").GetString();
                if (source is "web" or "open311" or "chat" or "elevenlabs-chat") digital++;
                if (value.GetProperty("duplicate").GetBoolean()) duplicates++;
                ai += value.GetProperty("aiRuns").GetInt32();
                fallback += value.GetProperty("fallbackRuns").GetInt32();
                measured++;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
        }
        return new(cases.Count(x => x.Any(e => e.Action == "received")), Duration("triaged"), Duration("approved"),
            Duration("assigned"), Duration("resolved"), measured == 0 ? null : (double)digital / measured,
            ai, fallback, measured == 0 ? null : (double)duplicates / measured, demo,
            "First recorded milestone per case; medians exclude unfinished milestones. Channel share is web/Open311/chat intake, a proxy for avoiding a phone queue; no queue wait or causal savings measured. Legacy cases without metrics-intake are excluded from shares and decision counts.");
    }
}
