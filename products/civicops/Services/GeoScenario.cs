using CivicOps.Models;

namespace CivicOps.Services;

/// <summary>Reproducible fictional evening; no real wards, addresses, personnel or resident locations.</summary>
public static class GeoScenario
{
    public const int Seed = 20261008;
    public static readonly double[] Bounds = [30.865, -30.035, 31.085, -29.78];
    public static GeoSnapshot Boundaries(string tenantId)
    {
        var municipality = new GeoMunicipality("demo-metro", "Demo Metro · SYNTHETIC", Polygon(0, 1, 0, 1), true, tenantId);
        string[] names = ["Estuary", "Harbour", "Civic ridge", "Coastal gardens", "North works", "Lighthouse"];
        var wards = Enumerable.Range(0, 6).Select(index => new GeoWard($"demo-ward-{index + 1:00}",
            $"Ward {index + 1:00} · {names[index]} · SYNTHETIC", municipality.Id,
            Polygon(index % 2 * 0.5, (index % 2 + 1) * 0.5, index / 2 / 3.0, (index / 2 + 1) / 3.0), true, tenantId)).ToList();
        return new(tenantId, 0, [municipality], wards, [], [], [], []);
    }
    private static GeoPolygon Polygon(double left, double right, double bottom, double top)
    {
        var a = Position(left, bottom); var b = Position(right, bottom); var c = Position(right, top); var d = Position(left, top);
        return new([[ [a.Longitude, a.Latitude], [b.Longitude, b.Latitude], [c.Longitude, c.Latitude], [d.Longitude, d.Latitude], [a.Longitude, a.Latitude] ]]);
    }
    public static GeoPoint Position(double horizontal, double vertical)
    {
        var latitude = -30.02 + vertical * 0.22;
        return new(30.88 + vertical * 0.088 + horizontal * 0.1, latitude);
    }
    public static IReadOnlyList<Incident> Cases(string tenantId, DateTimeOffset anchor, WorkOrderCatalog catalog, int seed = Seed)
    {
        var random = new Random(seed); var types = catalog.WorkOrderTypes.ToArray();
        var cases = new List<Incident>();
        for (var index = 0; index < 120; index++)
        {
            var type = types[index % types.Length];
            // A few concentrated infrastructure clusters make an evening workload legible.
            var x = index % 5 == 0 ? 0.27 + random.NextDouble() * 0.10 : 0.06 + random.NextDouble() * 0.88;
            var y = index % 5 == 0 ? 0.49 + random.NextDouble() * 0.13 : 0.04 + random.NextDouble() * 0.92;
            var at = anchor.AddMinutes(-(index < 95 ? 12 + random.Next(0, 720) : 1500 + random.Next(0, 7000)));
            var status = (index % 9) switch
            {
                0 => IncidentStatus.Approved, 1 => IncidentStatus.Assigned, 2 => IncidentStatus.InProgress,
                3 => IncidentStatus.Resolved, 4 => IncidentStatus.Closed, _ => IncidentStatus.PendingApproval
            };
            var milestoneMinutes = status switch
            {
                IncidentStatus.Closed => 40, IncidentStatus.Resolved => 30,
                IncidentStatus.Assigned or IncidentStatus.InProgress => 7, IncidentStatus.Approved => 4, _ => 0
            };
            if (at > anchor.AddMinutes(-milestoneMinutes)) at = anchor.AddMinutes(-milestoneMinutes);
            var updatedAt = anchor.AddMinutes(-random.Next(0, 45));
            if (updatedAt < at.AddMinutes(milestoneMinutes)) updatedAt = at.AddMinutes(milestoneMinutes);
            var priority = type.HazardClass == "critical" ? "Urgent" : index % 7 == 0 ? "High" : index % 4 == 0 ? "Low" : "Medium";
            var incident = new Incident
            {
                Id = $"synthetic-map-{index + 1:0000}", ReferenceNumber = $"CIV-DEMO-{index + 1:0000}",
                TenantId = tenantId, CreatedAt = at, UpdatedAt = updatedAt,
                Description = $"SYNTHETIC: {type.Title} at fictional exercise site {index + 1:000} in Demo Metro.",
                RawDescription = $"SYNTHETIC: {type.Title}; no real address or person.",
                Area = "Demo Metro · synthetic exercise site", NormalizedArea = "Demo Metro synthetic exercise site",
                LocationProvenance = "SYNTHETIC scenario coordinate; no resident or worker tracked", Location = Position(x, y),
                LocationShared = true, Synthetic = true, Category = type.Category, Department = type.Department,
                ServiceCode = type.Code, WorkOrderTypeCode = type.Code, Priority = priority, Status = status,
                NeedsHumanReview = status == IncidentStatus.PendingApproval, Source = "demo-scenario", SourceLabel = "Synthetic evening scenario",
                PublicUpdate = IncidentView.SafeUpdate(status), ResidentLanguage = "en",
                Approval = status == IncidentStatus.PendingApproval ? null : new("Synthetic exercise supervisor (SIMULATED)", "Synthetic scenario state; no operational approval.", at.AddMinutes(4))
            };
            incident.Audit.Add(new("scenario.seeded", "CivicOps synthetic scenario", "Deterministic synthetic exercise state; no actual municipal response or person location.", at));
            if (incident.Approval is not null) incident.Audit.Add(new("approved", incident.Approval.ApprovedBy, incident.Approval.Note!, at.AddMinutes(4)));
            if (status is IncidentStatus.Assigned or IncidentStatus.InProgress)
                incident.Audit.Add(new("assigned", "Synthetic exercise supervisor (SIMULATED)", "Synthetic exercise assignment; no live dispatch.", at.AddMinutes(7)));
            if (status is IncidentStatus.Resolved or IncidentStatus.Closed)
                incident.Audit.Add(new("resolved", "Synthetic exercise supervisor (SIMULATED)", "Synthetic exercise resolution.", at.AddMinutes(30)));
            if (status == IncidentStatus.Closed) incident.Audit.Add(new("closed", "Synthetic exercise supervisor (SIMULATED)", "Synthetic exercise closure.", at.AddMinutes(40)));
            cases.Add(incident);
        }
        return cases;
    }
    public static IReadOnlyList<GeoHazard> Hazards(string tenantId, DateTimeOffset anchor)
    {
        var random = new Random(Seed + 1); string[] types = ["downed-line", "burst-main", "open-trench", "flooding", "fire", "road-blocked", "smoke", "debris", "blocked-lane", "water"];
        return types.Select((type, index) => new GeoHazard($"synthetic-hazard-{index + 1:00}", type,
            type.Replace('-', ' ') + " · SYNTHETIC exercise", Position(0.12 + random.NextDouble() * 0.76, 0.08 + random.NextDouble() * 0.84),
            Math.Round(0.82 + random.NextDouble() * 0.15, 2), type is "fire" or "downed-line" ? 160 : 80,
            anchor.AddMinutes(-5 - index * 9), null, true, tenantId)).ToArray();
    }
    public static IReadOnlyList<GeoAsset> Assets(string tenantId) =>
    [
        new("synthetic-reservoir-01", "Civic ridge reservoir · SYNTHETIC", "reservoir", Position(0.15, 0.71), true, tenantId),
        new("synthetic-reservoir-02", "Estuary reservoir · SYNTHETIC", "reservoir", Position(0.18, 0.15), true, tenantId),
        new("synthetic-substation-01", "Harbour substation · SYNTHETIC", "substation", Position(0.78, 0.18), true, tenantId),
        new("synthetic-substation-02", "North works substation · SYNTHETIC", "substation", Position(0.32, 0.89), true, tenantId),
        new("synthetic-depot-01", "Central operations depot · SYNTHETIC", "depot", Position(0.37, 0.48), true, tenantId),
        new("synthetic-depot-02", "Coastal response depot · SYNTHETIC", "depot", Position(0.82, 0.57), true, tenantId)
    ];
}
