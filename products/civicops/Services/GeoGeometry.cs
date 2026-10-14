using CivicOps.Models;
using System.Collections.Frozen;

namespace CivicOps.Services;

/// <summary>Pure, antimeridian-free South African geometry. Exterior edges belong; hole edges do not.</summary>
public static class GeoGeofencing
{
    public static readonly double[] SouthAfricaBounds = [16, -35, 33, -22];
    private const double Epsilon = 1e-9;
    public static bool IsValidPoint(GeoPoint? point) => point is not null && double.IsFinite(point.Longitude)
        && double.IsFinite(point.Latitude) && point.Longitude is >= 16 and <= 33 && point.Latitude is >= -35 and <= -22;
    public static bool Contains(GeoPolygon polygon, GeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(polygon); ArgumentNullException.ThrowIfNull(point);
        if (!IsValidPoint(point) || polygon.Coordinates.Length == 0) return false;
        if (RingContains(polygon.Coordinates[0], point) == 0) return false;
        return polygon.Coordinates.Skip(1).All(hole => RingContains(hole, point) == 0);
    }
    private static int RingContains(double[][] ring, GeoPoint point)
    {
        var inside = false;
        for (var i = 0; i < ring.Length - 1; i++)
        {
            var a = ring[i]; var b = ring[i + 1];
            if (OnSegment(a, b, point.Longitude, point.Latitude)) return 2;
            if ((a[1] > point.Latitude) != (b[1] > point.Latitude)
                && point.Longitude < (b[0] - a[0]) * (point.Latitude - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
        }
        return inside ? 1 : 0;
    }
    private static bool OnSegment(double[] a, double[] b, double longitude, double latitude) =>
        Math.Abs((longitude - a[0]) * (b[1] - a[1]) - (latitude - a[1]) * (b[0] - a[0])) < Epsilon
        && longitude >= Math.Min(a[0], b[0]) - Epsilon && longitude <= Math.Max(a[0], b[0]) + Epsilon
        && latitude >= Math.Min(a[1], b[1]) - Epsilon && latitude <= Math.Max(a[1], b[1]) + Epsilon;
    public static bool IsValidPolygon(GeoPolygon? polygon)
    {
        if (polygon?.Coordinates is not { Length: >= 1 and <= 8 } rings) return false;
        foreach (var ring in rings)
        {
            if (ring is not { Length: >= 4 and <= 65 } || ring.Any(point => point is not { Length: 2 }
                || !IsValidPoint(new(point[0], point[1]))) || !ring[0].SequenceEqual(ring[^1])) return false;
            var area = 0.0;
            for (var i = 0; i < ring.Length - 1; i++)
            {
                if (ring[i].SequenceEqual(ring[i + 1])) return false;
                area += ring[i][0] * ring[i + 1][1] - ring[i + 1][0] * ring[i][1];
                for (var j = i + 2; j < ring.Length - 1; j++)
                {
                    if (i == 0 && j == ring.Length - 2) continue;
                    if (Intersects(ring[i], ring[i + 1], ring[j], ring[j + 1])) return false;
                }
            }
            if (Math.Abs(area) < Epsilon) return false;
        }
        for (var hole = 1; hole < rings.Length; hole++)
        {
            if (rings[hole].Take(rings[hole].Length - 1).Any(point => RingContains(rings[0], new(point[0], point[1])) != 1)) return false;
            for (var other = 0; other < hole; other++)
                for (var a = 0; a < rings[hole].Length - 1; a++)
                    for (var b = 0; b < rings[other].Length - 1; b++)
                        if (Intersects(rings[hole][a], rings[hole][a + 1], rings[other][b], rings[other][b + 1])) return false;
            if (Enumerable.Range(1, hole - 1).Any(other => RingContains(rings[other], new(rings[hole][0][0], rings[hole][0][1])) != 0
                || RingContains(rings[hole], new(rings[other][0][0], rings[other][0][1])) != 0)) return false;
        }
        return true;
    }
    private static bool Intersects(double[] a, double[] b, double[] c, double[] d)
    {
        static double Cross(double[] p, double[] q, double[] r) => (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0]);
        return OnSegment(a, b, c[0], c[1]) || OnSegment(a, b, d[0], d[1]) || OnSegment(c, d, a[0], a[1]) || OnSegment(c, d, b[0], b[1])
            || Cross(a, b, c) * Cross(a, b, d) < 0 && Cross(c, d, a) * Cross(c, d, b) < 0;
    }
    public static (string? MunicipalityId, string? WardId) Assign(GeoPoint? point,
        IEnumerable<GeoMunicipality> municipalities, IEnumerable<GeoWard> wards)
    {
        if (!IsValidPoint(point)) return (null, null);
        var municipality = municipalities.OrderBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault(value => Contains(value.Polygon, point!));
        var ward = municipality is null ? null : wards.Where(value => value.MunicipalityId == municipality.Id)
            .OrderBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault(value => Contains(value.Polygon, point!));
        return (municipality?.Id, ward?.Id);
    }
    public static bool ContainsPolygon(GeoPolygon municipality, GeoPolygon ward)
    {
        if (!IsValidPolygon(municipality) || !IsValidPolygon(ward)) return false;
        var exterior = ward.Coordinates[0];
        for (var index = 0; index < exterior.Length - 1; index++)
        {
            var a = exterior[index]; var b = exterior[index + 1];
            if (!Contains(municipality, new(a[0], a[1]))) return false;
            var splits = new List<double> { 0, 1 };
            var dx = b[0] - a[0]; var dy = b[1] - a[1];
            foreach (var ring in municipality.Coordinates)
                for (var edge = 0; edge < ring.Length - 1; edge++)
                {
                    var c = ring[edge]; var d = ring[edge + 1]; var ex = d[0] - c[0]; var ey = d[1] - c[1];
                    var denominator = dx * ey - dy * ex;
                    if (Math.Abs(denominator) > Epsilon)
                    {
                        var t = ((c[0] - a[0]) * ey - (c[1] - a[1]) * ex) / denominator;
                        var u = ((c[0] - a[0]) * dy - (c[1] - a[1]) * dx) / denominator;
                        if (t is > 0 and < 1 && u is >= 0 and <= 1) splits.Add(t);
                    }
                    else
                    {
                        foreach (var point in new[] { c, d })
                            if (OnSegment(a, b, point[0], point[1])) splits.Add(Math.Abs(dx) > Math.Abs(dy) ? (point[0] - a[0]) / dx : (point[1] - a[1]) / dy);
                    }
                }
            var ordered = splits.Distinct().Order().ToArray();
            for (var interval = 0; interval < ordered.Length - 1; interval++)
            {
                var t = (ordered[interval] + ordered[interval + 1]) / 2;
                if (!Contains(municipality, new(a[0] + t * dx, a[1] + t * dy))) return false;
            }
        }
        // A ward may not silently cover a municipal hole entirely enclosed by its exterior.
        return municipality.Coordinates.Skip(1).All(hole => hole.All(point => !Contains(ward, new(point[0], point[1]))));
    }
    public static double DistanceKm(GeoPoint a, GeoPoint b)
    {
        const double radians = Math.PI / 180;
        var deltaLatitude = (b.Latitude - a.Latitude) * radians; var deltaLongitude = (b.Longitude - a.Longitude) * radians;
        var h = Math.Pow(Math.Sin(deltaLatitude / 2), 2) + Math.Cos(a.Latitude * radians) * Math.Cos(b.Latitude * radians) * Math.Pow(Math.Sin(deltaLongitude / 2), 2);
        return 6371.0088 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(h, 0, 1)));
    }
}

public static class GeoHeatAggregation
{
    public const double CellSizeDegrees = 0.008;
    public static IReadOnlyList<GeoHeatCell> Aggregate(IEnumerable<GeoGridPoint> points, DateTimeOffset now, TimeSpan window)
    {
        var cells = points.Where(point => GeoGeofencing.IsValidPoint(point.Location) && point.At >= now - window && point.At <= now)
            .GroupBy(point => ((int)Math.Floor(Math.Min(point.Location.Longitude, 33 - 1e-9) / CellSizeDegrees),
                (int)Math.Floor(Math.Min(point.Location.Latitude, -22 - 1e-9) / CellSizeDegrees)))
            .Select(group => new GeoHeatCell(new((group.Key.Item1 + 0.5) * CellSizeDegrees, (group.Key.Item2 + 0.5) * CellSizeDegrees), group.Count(), 0))
            .OrderBy(cell => cell.Location.Longitude).ThenBy(cell => cell.Location.Latitude).ToArray();
        var maximum = cells.Length == 0 ? 1 : cells.Max(cell => cell.Count);
        return cells.Select(cell => cell with { Weight = (double)cell.Count / maximum }).ToArray();
    }
}

public static class GeoNearestCrewRanking
{
    public static IReadOnlyList<GeoCrewSuggestion> Rank(GeoPoint incidentLocation, IEnumerable<GeoCrewCandidate> candidates,
        DateTimeOffset now, TimeSpan staleAfter, int limit = 5) => candidates
        .Where(value => value.CanAssign && value.EligibilityStatus == "eligible" && value.Crew.Availability == "available"
            && value.Crew.Shift != "off-shift" && value.LastSeenAt <= now && now - value.LastSeenAt <= staleAfter && GeoGeofencing.IsValidPoint(value.Location))
        .Select(value => new { Candidate = value, Distance = GeoGeofencing.DistanceKm(incidentLocation, value.Location) })
        .OrderBy(value => value.Distance).ThenBy(value => value.Candidate.Crew.Id, StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 10))
        .Select(value => new GeoCrewSuggestion(value.Candidate.Crew.Id, value.Candidate.Crew.Name, value.Candidate.Crew.Department,
            Math.Round(value.Distance, 2), value.Candidate.EligibilityStatus,
            value.Candidate.Crew.Availability, value.Candidate.Reasons, value.Candidate.Reasons, value.Candidate.Synthetic)).ToArray();
}

public static class GeoPrivacyGuards
{
    public static readonly IReadOnlySet<string> HazardEventTypes = new[]
    { "crash", "stalled-vehicle", "flooding", "fire", "smoke", "blocked-lane", "debris", "downed-line", "open-trench", "water", "burst-main", "road-blocked" }.ToFrozenSet(StringComparer.Ordinal);
    public static bool WorkerConsentAllowsLocation(bool onShift, bool explicitConsent, bool visibleIndicator) => onShift && explicitConsent && visibleIndicator;
    public static string RejectLiveWorkerLocation(bool onShift, bool explicitConsent, bool visibleIndicator) =>
        !WorkerConsentAllowsLocation(onShift, explicitConsent, visibleIndicator)
            ? "Worker location requires an active shift, explicit revocable consent and a visible indicator."
            : "Live worker location ingestion is disabled until the consent-controlled crew application is implemented.";
}
