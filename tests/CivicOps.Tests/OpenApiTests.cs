using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using CivicOps.Services;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public class OpenApiTests
{
    [Fact]
    public void RepeatedGenerationProducesTheSameDocumentWithoutSharedMutableNodes()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>();
        Assert.True(JsonNode.DeepEquals(OpenApiDocument.Create(endpoints), OpenApiDocument.Create(endpoints)));
    }

    [Fact]
    public async Task EveryMappedMethodAndEndpointIncludingDocumentHasAContract()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi.json"))!;
        var expected = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(method => method + " " + endpoint.RoutePattern.RawText!.TrimEnd('/'))).Order().ToArray();
        var actual = document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject().Select(method => method.Key.ToUpperInvariant() + " " + path.Key)).Order().ToArray();
        Assert.Equal(expected, actual); Assert.Equal(PermissionMatrix.Endpoints.Keys.Order(), actual);
        Assert.Contains("GET /openapi.json", actual);
    }

    [Fact]
    public async Task CommittedDocumentExactlyMatchesGeneratedContract()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var actual = JsonNode.Parse(await client.GetStringAsync("/openapi.json"));
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/api/openapi.json"));
        Assert.True(File.Exists(path), "Generate docs/api/openapi.json with ./scripts/export_openapi.sh.");
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(path));
        Assert.True(JsonNode.DeepEquals(expected, actual), "OpenAPI changed: regenerate ./scripts/export_openapi.sh and commit the reviewed artifact.");
    }

    [Fact]
    public async Task ContractsHaveUniqueOperationsTagsSuccessSchemasExamplesAndSharedErrors()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi.json"))!; var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in document.AsObject()) Assert.NotNull(document["components"]!["schemas"]!["OpenApiResponse"]!["properties"]![field.Key]);
        foreach (var path in document["paths"]!.AsObject())
            foreach (var method in path.Value!.AsObject())
            {
                var operation = method.Value!; Assert.True(identifiers.Add(operation["operationId"]!.GetValue<string>()));
                Assert.False(string.IsNullOrWhiteSpace(operation["summary"]!.GetValue<string>()));
                Assert.False(string.IsNullOrWhiteSpace(operation["description"]!.GetValue<string>()));
                Assert.NotEmpty(operation["tags"]!.AsArray()); Assert.IsType<JsonArray>(operation["security"]);
                var successes = operation["responses"]!.AsObject().Where(response => response.Key[0] is '2' or '3').ToArray(); Assert.NotEmpty(successes);
                foreach (var response in successes.Where(response => response.Key[0] == '2'))
                    foreach (var media in response.Value!["content"]!.AsObject())
                    { Assert.NotNull(media.Value!["schema"]); Assert.NotNull(media.Value!["example"]); }
                foreach (var status in new[] { "400", "401", "403", "404", "409", "413", "415", "429", "500", "502", "503" })
                {
                    var problem = operation["responses"]![status]!["content"]!["application/problem+json"]!;
                    Assert.Equal("#/components/schemas/ProblemDetails", problem["schema"]!["$ref"]!.GetValue<string>());
                    Assert.Equal(int.Parse(status, System.Globalization.CultureInfo.InvariantCulture), problem["example"]!["status"]!.GetValue<int>());
                }
            }
        CheckReferences(document, document);
    }

    [Fact]
    public async Task SecuritySchemesDescribeRealCookieBearerOidcAndWebhookBoundaries()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var document = JsonNode.Parse(await client.GetStringAsync("/openapi.json"))!;
        var schemes = document["components"]!["securitySchemes"]!;
        Assert.Equal("openIdConnect", schemes["oidc"]!["type"]!.GetValue<string>());
        Assert.Contains("placeholder", schemes["oidc"]!["description"]!.GetValue<string>());
        Assert.Equal("cookie", schemes["staffSession"]!["in"]!.GetValue<string>());
        Assert.Equal("__Host-CivicOps-Session", schemes["staffSession"]!["name"]!.GetValue<string>());
        Assert.Equal("bearer", schemes["serviceAccountBearer"]!["scheme"]!.GetValue<string>());
        Assert.Equal("X-Zendesk-Webhook-Signature", schemes["zendeskWebhookSignature"]!["name"]!.GetValue<string>());
        var paths = document["paths"]!;
        Assert.Empty(paths["/health/live"]!["get"]!["security"]!.AsArray());
        Assert.Empty(paths["/api/incidents"]!["post"]!["security"]![0]!.AsObject());
        Assert.NotNull(paths["/api/admin/overview"]!["get"]!["security"]![0]!["staffSession"]);
        Assert.DoesNotContain(paths["/api/admin/overview"]!["get"]!["security"]!.AsArray(), security => security!["serviceAccountBearer"] is not null);
        Assert.NotNull(paths["/api/v1/training/completions"]!["post"]!["security"]![0]!["trainingBearer"]);
        Assert.NotNull(paths["/api/channels/elevenlabs/post-call"]!["post"]!["security"]![0]!["elevenLabsWebhookSignature"]);
    }

    [Fact]
    public async Task EveryTypedOrManualJsonBodyHasSchemaAndSyntheticExample()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var document = JsonNode.Parse(await client.GetStringAsync("/openapi.json"))!;
        foreach (var endpoint in app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
            if (endpoint.Metadata.GetMetadata<IAcceptsMetadata>() is not null)
                foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Where(method => method is "POST" or "PUT" or "PATCH"))
                {
                    var content = document["paths"]![endpoint.RoutePattern.RawText!.TrimEnd('/')]![method.ToLowerInvariant()]!["requestBody"]!["content"]!["application/json"]!;
                    Assert.NotNull(content["schema"]); Assert.NotNull(content["example"]);
                }
        foreach (var path in new[] { "/api/channels/elevenlabs/tools/{tool}", "/api/channels/elevenlabs/post-call", "/api/channels/elevenlabs/simulated-chat", "/api/channels/zendesk/webhook", "/open311/v2/requests" })
            Assert.NotNull(document["paths"]![path]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]);
        var intake = document["components"]!["schemas"]!["IncidentRequest"]!;
        Assert.Equal(10, intake["properties"]!["description"]!["minLength"]!.GetValue<int>());
        Assert.Equal(4000, intake["properties"]!["description"]!["maxLength"]!.GetValue<int>());
        Assert.Contains(intake["required"]!.AsArray(), field => field!.GetValue<string>() == "description");
    }

    [Fact]
    public async Task BinaryStreamsCachingAndResidentProjectionHaveAccurateContracts()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var document = JsonNode.Parse(await client.GetStringAsync("/openapi.json"))!; var paths = document["paths"]!;
        Assert.NotNull(paths["/api/audit/export"]!["get"]!["responses"]!["200"]!["content"]!["application/zip"]);
        Assert.NotNull(paths["/api/stream"]!["get"]!["responses"]!["200"]!["content"]!["text/event-stream"]);
        Assert.NotNull(paths["/api/v1/training/skills"]!["get"]!["responses"]!["304"]);
        Assert.NotNull(paths["/auth/login"]!["get"]!["responses"]!["302"]!["headers"]!["Location"]);
        var projection = document["components"]!["schemas"]!["PublicIncident"]!["properties"]!;
        foreach (var field in new[] { "description", "contactEmail", "contactPhone", "transcriptSummary", "rawTranscript", "audit" }) Assert.Null(projection[field]);
        Assert.NotNull(projection["publicUpdate"]);
    }

    [Fact]
    public async Task PublishedResidentAndOpen311ExamplesCanCreateSyntheticReports()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var document = JsonNode.Parse(await client.GetStringAsync("/openapi.json"))!;
        foreach (var route in new[] { "/api/incidents", "/open311/v2/requests" })
        {
            var example = document["paths"]![route]!["post"]!["requestBody"]!["content"]!["application/json"]!["example"]!.DeepClone();
            var response = await client.PostAsJsonAsync(route, example);
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task PublishedNoticeAndPublicLookupSchemasCoverActualLanguageFields()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var document = JsonNode.Parse(await client.GetStringAsync("/openapi.json"))!;
        var schemas = document["components"]!["schemas"]!;
        Assert.NotNull(schemas["PrivacyNotice"]!["properties"]!["language"]);
        Assert.NotNull(schemas["PublicIncident"]!["properties"]!["residentLanguage"]);
        foreach (var language in new[] { "en", "af", "zu" })
        {
            var notice = JsonNode.Parse(await client.GetStringAsync("/api/privacy/notice?language=" + language))!;
            Assert.Equal(language, notice["language"]!.GetValue<string>());
            foreach (var field in notice.AsObject()) Assert.NotNull(schemas["PrivacyNotice"]!["properties"]![field.Key]);
            var created = JsonNode.Parse(await (await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic municipal water leak for language contract testing.", residentLanguage = language })).Content.ReadAsStringAsync())!;
            var projection = created["incident"]!;
            var lookedUp = JsonNode.Parse(await client.GetStringAsync("/api/incidents/" + projection["id"]!.GetValue<string>()))!;
            Assert.Equal(language, lookedUp["residentLanguage"]!.GetValue<string>());
            foreach (var field in lookedUp.AsObject()) Assert.NotNull(schemas["PublicIncident"]!["properties"]![field.Key]);
        }
    }

    [Fact]
    public async Task DocumentContainsNoRuntimeSecretsAndIsIndependentOfDemoConfiguration()
    {
        using var demo = new TestApp(); using var demoClient = demo.CreateClient(); var first = JsonNode.Parse(await demoClient.GetStringAsync("/openapi.json"));
        using var production = new TestApp(); production.Settings["CIVICOPS_DEMO_MODE"] = "false";
        production.Settings["CIVICOPS_DISPATCH_TOKEN"] = "synthetic-openapi-secret-sentinel";
        using var productionClient = production.CreateClient(); var response = await productionClient.GetStringAsync("/openapi.json");
        Assert.DoesNotContain("synthetic-openapi-secret-sentinel", response); Assert.True(JsonNode.DeepEquals(first, JsonNode.Parse(response)));
    }

    private static void CheckReferences(JsonNode root, JsonNode node)
    {
        if (node is JsonObject value)
        {
            if (value["$ref"] is JsonValue reference)
            {
                var path = reference.GetValue<string>(); Assert.StartsWith("#/", path); JsonNode? target = root;
                foreach (var part in path[2..].Split('/')) target = target?[part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
                Assert.NotNull(target);
            }
            foreach (var child in value.Select(property => property.Value).OfType<JsonNode>()) CheckReferences(root, child);
        }
        else if (node is JsonArray array) foreach (var child in array.OfType<JsonNode>()) CheckReferences(root, child);
    }
}
