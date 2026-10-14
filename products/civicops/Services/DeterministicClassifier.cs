using CivicOps.Models;
using System.Text.RegularExpressions;

namespace CivicOps.Services;

public sealed record ClassificationResult(string Category, string Department, string Priority, string WorkOrderTypeCode);

public sealed record DuplicateMatch(string ReferenceNumber, double Confidence);

public sealed class DeterministicClassifier(WorkOrderCatalog? catalog = null)
{
    private readonly WorkOrderCatalog _catalog = catalog ?? WorkOrderCatalog.Default;
    public ClassificationResult Classify(string description)
    {
        var text = description.ToLowerInvariant();
        var code = ContainsAny(text, "live wire", "exposed wire", "electrocution", "lewendige draad") ? "ELEC.LIVE_WIRE_DOWN"
            : ContainsAny(text, "flood response", "flooding home", "flooding my home", "flooding inside", "flooding my house", "huis oorstroom") ? "EMERG.FLOOD_RESPONSE"
            : ContainsAny(text, "stormwater", "storm water", "drain") ? "ROADS.BLOCKED_DRAIN"
            : ContainsAny(text, "burst pipe", "water", "leak") ? "WATER.BURST_MAIN"
            : ContainsAny(text, "traffic light", "traffic signal") ? "ROADS.TRAFFIC_SIGNAL_FAULT"
            : ContainsAny(text, "pothole", "road") ? "ROADS.POTHOLE_REPAIR"
            : ContainsAny(text, "power", "electric", "streetlight", "street light") ? "ELEC.STREETLIGHT_FAULT"
            : ContainsAny(text, "rubbish", "waste", "dumping") ? "WASTE.ILLEGAL_DUMPING"
            : "GENERAL.SERVICE_INSPECTION";
        var type = _catalog.Find(code) ?? throw new InvalidDataException("Required classifier work-order code is absent from the catalogue: " + code);
        return new(type.Category, type.Department, type.HazardClass == "critical" ? "Urgent" : Priority(text), type.Code);
    }

    public DuplicateMatch? FindDuplicate(IEnumerable<Incident> existing, Incident candidate)
    {
        var candidateWords = Words(candidate.Description);
        if (candidateWords.Count == 0) return null;

        DuplicateMatch? best = null;
        foreach (var incident in existing)
        {
            if (incident.ReferenceNumber == candidate.ReferenceNumber
                || !string.Equals(incident.NormalizedArea, candidate.NormalizedArea, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(incident.Department, candidate.Department, StringComparison.OrdinalIgnoreCase)
                || incident.Status is IncidentStatus.Resolved or IncidentStatus.Closed)
                continue;

            var words = Words(incident.Description);
            var overlap = candidateWords.Intersect(words, StringComparer.OrdinalIgnoreCase).Count();
            var confidence = (double)overlap / Math.Max(candidateWords.Count, words.Count);
            if (confidence >= 0.55 && (best is null || confidence > best.Confidence))
                best = new DuplicateMatch(incident.ReferenceNumber, Math.Round(confidence, 2));
        }
        return best;
    }

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '!', '?', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length > 3)
            .Where(word => !new[] { "there", "this", "that", "please", "nearby", "same" }.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Priority(string text)
    {
        text = text.ToLowerInvariant();
        return ContainsAny(text, "urgent", "danger", "trapped", "fire", "life threatening", "live wire", "flooding home", "flooding my home", "electrocution",
            "vasgekeer", "lewendige draad", "huis oorstroom")
            || Regex.IsMatch(text, @"\bbrand\b(?!\s+new\b)", RegexOptions.CultureInvariant)
            ? "Urgent"
            : ContainsAny(text, "flood", "outage", "blocked")
                ? "High"
                : "Medium";
    }

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(text.Contains);
}
