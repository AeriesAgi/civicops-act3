using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Services;

namespace CivicOps.Tests;

public sealed class MapContractTests
{
    [Fact]
    public async Task ActualMapOutlinesCasesAndHeatCellsMatchTheClosedPortableFeatureContract()
    {
        using var app = new TestApp { EnableBackgroundDelivery = false }; using var client = app.CreateClient();
        var point = GeoScenario.Position(0.7, 0.5);
        var response = await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic pipe leak for map contract verification", location = point,
            shareLocation = true, privacyConsentAccepted = true, privacyConsentVersion = PrivacyConsent.Version });
        response.EnsureSuccessStatusCode();
        foreach (var route in new[] { "/api/v1/map/layers", "/api/v1/map/wards", "/api/v1/map/heatmap?metric=cases&window=24h" })
        {
            using var result = await client.GetAsync(route); result.EnsureSuccessStatusCode();
            Assert.Equal("application/geo+json", result.Content.Headers.ContentType!.MediaType);
            SchemaContract.Validate(await result.Content.ReadFromJsonAsync<JsonElement>(), "map-feature-collection.schema.json");
        }
    }

    [Fact]
    public async Task OpenApiDescribesActualMapConfigurationCachingQueryAndHumanWriteFields()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi.json"))!; var paths = document["paths"]!;
        var config = JsonNode.Parse(await client.GetStringAsync("/api/v1/map/config"))!.AsObject();
        var fields = document["components"]!["schemas"]!["MapConfiguration"]!["properties"]!;
        foreach (var field in config) Assert.NotNull(fields[field.Key]);
        Assert.NotNull(paths["/api/v1/map/layers"]!["get"]!["responses"]!["304"]);
        Assert.NotNull(paths["/api/v1/map/wards"]!["get"]!["responses"]!["200"]!["content"]!["application/geo+json"]);
        var layerParameters = paths["/api/v1/map/layers"]!["get"]!["parameters"]!.AsArray().Select(value => value!["name"]!.GetValue<string>()).ToArray();
        foreach (var name in new[] { "bbox", "layers", "since", "page", "limit", "If-None-Match" }) Assert.Contains(name, layerParameters);
        Assert.Contains("five-minute", paths["/api/v1/map/stream"]!["get"]!["description"]!.GetValue<string>());
        Assert.Equal("supervise", paths["/api/v1/map/hazards"]!["post"]!["x-civicops-policy"]!.GetValue<string>());
        Assert.Equal("dispatch", paths["/api/v1/map/cases/{id}/dispatch"]!["post"]!["x-civicops-policy"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("map-feature-collection.schema.json")][InlineData("map-hazard-request.schema.json")]
    public void PublishedSyntheticExamplesMatchTheirPortableSchema(string filename)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(SchemaContract.RepositoryRoot, "docs", "schemas", filename)));
        SchemaContract.Validate(document.RootElement.GetProperty("example"), filename);
    }

    [Fact]
    public void SchemaValidatorRejectsWrongLayerFieldsCoordinatesAndUnreviewedDetectorTypes()
    {
        var root = Path.Combine(SchemaContract.RepositoryRoot, "docs", "schemas");
        var collection = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "map-feature-collection.schema.json")))!["example"]!.DeepClone();
        collection["features"]![0]!["properties"]!["faceEmbedding"] = "synthetic forbidden";
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => SchemaContract.Validate(JsonSerializer.SerializeToElement(collection), "map-feature-collection.schema.json"));
        collection = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "map-feature-collection.schema.json")))!["example"]!.DeepClone();
        collection["features"]![0]!["geometry"]!["coordinates"]![0]![0]![1] = 29.9;
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => SchemaContract.Validate(JsonSerializer.SerializeToElement(collection), "map-feature-collection.schema.json"));
        var hazard = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "map-hazard-request.schema.json")))!["example"]!.DeepClone(); hazard["eventType"] = "person-risk";
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => SchemaContract.Validate(JsonSerializer.SerializeToElement(hazard), "map-hazard-request.schema.json"));
    }
}
