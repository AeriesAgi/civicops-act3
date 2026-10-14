using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;

namespace CivicOps.Tests;

public sealed class MapGeometryTests
{
    private static readonly GeoPolygon ExteriorAndHole = new(
    [
        [[30, -30], [31, -30], [31, -29], [30, -29], [30, -30]],
        [[30.4, -29.6], [30.6, -29.6], [30.6, -29.4], [30.4, -29.4], [30.4, -29.6]]
    ]);
    [Theory]
    [InlineData(30.2, -29.8, true)] [InlineData(30, -29.5, true)] [InlineData(31, -29, true)]
    [InlineData(30.5, -29.5, false)] [InlineData(30.4, -29.5, false)] [InlineData(30.4, -29.6, false)]
    [InlineData(29.99, -29.5, false)] [InlineData(31.01, -29.5, false)]
    public void ExteriorIncludesEdgesButHoleExcludesEdges(double longitude, double latitude, bool expected) =>
        Assert.Equal(expected, GeoGeofencing.Contains(ExteriorAndHole, new(longitude, latitude)));
    [Theory]
    [InlineData(16, -35, true)] [InlineData(33, -22, true)] [InlineData(31.01, -29.88, true)]
    [InlineData(180, -30, false)] [InlineData(-180, -30, false)] [InlineData(0, 0, false)]
    [InlineData(double.NaN, -29, false)] [InlineData(31, double.PositiveInfinity, false)]
    public void CountryBoundsRejectNonfiniteAndAntimeridianCoordinates(double longitude, double latitude, bool expected) =>
        Assert.Equal(expected, GeoGeofencing.IsValidPoint(new(longitude, latitude)));
    [Fact]
    public void PolygonsRequireClosedNoncrossingRingsWithInternalHoles()
    {
        Assert.True(GeoGeofencing.IsValidPolygon(ExteriorAndHole));
        Assert.False(GeoGeofencing.IsValidPolygon(new([[[30, -30], [31, -30], [31, -29], [30, -29]]])));
        Assert.False(GeoGeofencing.IsValidPolygon(new([[[30, -30], [31, -29], [30, -29], [31, -30], [30, -30]]])));
        Assert.False(GeoGeofencing.IsValidPolygon(new([[[30, -30], [30, -30], [31, -29], [30, -30]]])));
        Assert.False(GeoGeofencing.IsValidPolygon(new([ExteriorAndHole.Coordinates[0], [[31.2, -29.8], [31.4, -29.8], [31.4, -29.6], [31.2, -29.8]]])));
        Assert.False(GeoGeofencing.IsValidPolygon(new([ExteriorAndHole.Coordinates[0], [[30.9, -29.8], [31.1, -29.8], [30.9, -29.6], [30.9, -29.8]]])));
    }
    [Fact]
    public void SharedBoundaryAssignmentUsesStableOrdinalWardOrder()
    {
        var snapshot = GeoScenario.Boundaries("default"); var point = GeoScenario.Position(0.5, 1.0 / 6);
        var normal = GeoGeofencing.Assign(point, snapshot.Municipalities, snapshot.Wards);
        var reversed = GeoGeofencing.Assign(point, snapshot.Municipalities.AsEnumerable().Reverse(), snapshot.Wards.AsEnumerable().Reverse());
        Assert.Equal(("demo-metro", "demo-ward-01"), normal); Assert.Equal(normal, reversed);
        Assert.Equal((null, null), GeoGeofencing.Assign(new(18, -34), snapshot.Municipalities, snapshot.Wards));
        Assert.Equal((null, null), GeoGeofencing.Assign(null, snapshot.Municipalities, snapshot.Wards));
    }
    [Fact]
    public void ScenarioProducesReproducibleSyntheticCoastalCasesAndPolygons()
    {
        var anchor = new DateTimeOffset(2026, 10, 8, 17, 30, 0, TimeSpan.Zero);
        var left = GeoScenario.Cases("default", anchor, WorkOrderCatalog.Baseline);
        var right = GeoScenario.Cases("default", anchor, WorkOrderCatalog.Baseline);
        Assert.Equal(JsonSerializer.Serialize(left), JsonSerializer.Serialize(right)); Assert.Equal(120, left.Count);
        Assert.Equal(120, left.Select(value => value.Id).Distinct().Count());
        Assert.All(left, value =>
        {
            Assert.True(value.Synthetic); Assert.True(value.LocationShared); Assert.Null(value.ContactEmail); Assert.Null(value.ContactPhone); Assert.StartsWith("SYNTHETIC:", value.Description);
            Assert.InRange(value.UpdatedAt, value.CreatedAt, anchor); Assert.All(value.Audit, audit => Assert.InRange(audit.At, value.CreatedAt, value.UpdatedAt));
        });
        var snapshot = GeoScenario.Boundaries("default");
        Assert.All(snapshot.Municipalities, value => Assert.True(GeoGeofencing.IsValidPolygon(value.Polygon)));
        Assert.All(snapshot.Wards, value => Assert.True(GeoGeofencing.IsValidPolygon(value.Polygon)));
        Assert.All(left, value => Assert.NotNull(GeoGeofencing.Assign(value.Location, snapshot.Municipalities, snapshot.Wards).WardId));
        Assert.Equal(10, GeoScenario.Hazards("default", anchor).Count);
        Assert.True(left.Select(value => value.Department).Distinct().Count() >= 6);
    }
    [Fact]
    public void HeatGridCountsIncidentsAndExcludesOldFutureOrInvalidPoints()
    {
        var now = DateTimeOffset.UtcNow;
        GeoGridPoint[] points = [new(new(31.001, -29.901), now), new(new(31.002, -29.902), now.AddMinutes(-5)),
            new(new(31.08, -29.84), now.AddHours(-2)), new(new(31.02, -29.9), now.AddDays(-2)),
            new(new(31.02, -29.9), now.AddDays(1)), new(new(double.NaN, -29.9), now)];
        var result = GeoHeatAggregation.Aggregate(points, now, TimeSpan.FromHours(24));
        Assert.Equal(3, result.Sum(value => value.Count)); Assert.Equal(2, result.Count);
        Assert.Contains(result, value => value.Count == 2 && value.Weight == 1);
        Assert.Contains(result, value => value.Count == 1 && value.Weight == 0.5);
        Assert.Equal(4, GeoHeatAggregation.Aggregate(points, now, TimeSpan.FromDays(7)).Sum(value => value.Count));
        Assert.Empty(GeoHeatAggregation.Aggregate([], now, TimeSpan.FromHours(24)));
    }
    [Fact]
    public void NearestRankingRequiresFreshAvailabilitySkillsAndCurrentCertificates()
    {
        var now = DateTimeOffset.UtcNow; var location = new GeoPoint(31, -29.9);
        GeoCrewCandidate Candidate(string id, double longitude, string status = "eligible", string availability = "available", int age = 2) =>
            new(new(id, id, "Water and Sanitation", "Synthetic repairs", [], "synthetic-evening", availability), new(longitude, -29.9), now.AddMinutes(-age), status, status != "blocked", ["WATER.PIPE_REPAIR: level 3"], true);
        var result = GeoNearestCrewRanking.Rank(location,
        [Candidate("further", 31.02), Candidate("nearest", 31.001), Candidate("stale", 31, age: 16),
            Candidate("warned-certificate", 31, "warned"), Candidate("missing-skill", 31, "blocked"),
            Candidate("off-shift", 31, availability: "off-shift"), Candidate("unavailable", 31, availability: "unavailable"), Candidate("future", 31, age: -1)],
            now, TimeSpan.FromMinutes(15));
        Assert.Equal(["nearest", "further"], result.Select(value => value.CrewId));
        Assert.True(result[0].DistanceKm < result[1].DistanceKm); Assert.All(result, value => Assert.NotEmpty(value.Qualifications));
        Assert.Equal(0, GeoGeofencing.DistanceKm(location, location));
        Assert.InRange(GeoGeofencing.DistanceKm(new(31, -30), new(31, -29)), 111, 112);
    }
    [Fact]
    public void NewOptionalCaseFieldsPreserveHistoricalEmptyCaseSerialization()
    {
        var serialized = JsonSerializer.Serialize(new Incident());
        foreach (var field in new[] { "Location\"", "LocationShared", "MunicipalityId", "WardId", "Synthetic" }) Assert.DoesNotContain(field, serialized, StringComparison.Ordinal);
        var request = JsonSerializer.Serialize(new IncidentRequest("Synthetic pipe leak at the hall"));
        using var parsed = JsonDocument.Parse(request);
        Assert.False(parsed.RootElement.TryGetProperty("Location", out _)); Assert.False(parsed.RootElement.TryGetProperty("ShareLocation", out _));
    }
    [Fact]
    public void NearestRankingOrdersExactDistancesBeforeRoundingForDisplay()
    {
        var now = DateTimeOffset.UtcNow; var location = new GeoPoint(31, -29.9);
        var farther = new GeoCrewCandidate(new("a-farther", "Synthetic farther", "Water and Sanitation", "Synthetic repairs"), new(31.00002, -29.9), now, "eligible", true, [], true);
        var nearer = new GeoCrewCandidate(new("z-nearer", "Synthetic nearer", "Water and Sanitation", "Synthetic repairs"), new(31.00001, -29.9), now, "eligible", true, [], true);
        var ranked = GeoNearestCrewRanking.Rank(location, [farther, nearer], now, TimeSpan.FromMinutes(15));
        Assert.Equal(0, ranked[0].DistanceKm); Assert.Equal(0, ranked[1].DistanceKm); Assert.Equal("z-nearer", ranked[0].CrewId);
    }
    [Fact]
    public void HeatGridClosedEnvelopeEdgesStayInsideThePublishedCountryBounds()
    {
        var now = DateTimeOffset.UtcNow;
        var cells = GeoHeatAggregation.Aggregate([new(new(33, -22), now), new(new(16, -35), now)], now, TimeSpan.FromHours(24));
        Assert.Equal(2, cells.Count); Assert.All(cells, cell => Assert.True(GeoGeofencing.IsValidPoint(cell.Location)));
        Assert.All(cells, cell => Assert.NotEqual(new GeoPoint(33, -22), cell.Location));
    }
}
