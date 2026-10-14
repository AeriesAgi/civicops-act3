using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CivicOps.Tests;

public sealed class AdminConsoleTests
{
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task NewCatalogueServiceIsDiscoverableAndUsableOnlyInItsMunicipality(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var admin = await RbacTests.RoleClient(app, "admin");
        var catalogue = JsonNode.Parse(await admin.GetStringAsync("/api/admin/catalogue"))!; var version = catalogue["version"]!.GetValue<string>(); catalogue["version"] = "2.0.0";
        var type = catalogue["workOrderTypes"]![0]!.DeepClone(); type["code"] = "WATER.ADMIN_REVIEW"; type["serviceCode"] = "admin-review";
        catalogue["workOrderTypes"]!.AsArray().Add(type);
        (await admin.PutAsJsonAsync("/api/admin/catalogue", new { expectedVersion = version, catalogue })).EnsureSuccessStatusCode();
        var services = await admin.GetFromJsonAsync<JsonElement>("/open311/v2/services");
        Assert.Contains(services.EnumerateArray(), service => service.GetProperty("service_code").GetString() == "WATER.ADMIN_REVIEW");
        (await admin.PostAsJsonAsync("/open311/v2/requests", new { service_code = "WATER.ADMIN_REVIEW", description = "Synthetic municipal pipe service review.", address_string = "Synthetic catalogue area" })).EnsureSuccessStatusCode();
        using var coast = await RbacTests.RoleClient(app, "admin", tenant: "synthetic-coast");
        Assert.DoesNotContain((await coast.GetFromJsonAsync<JsonElement>("/open311/v2/services")).EnumerateArray(), service => service.GetProperty("service_code").GetString() == "WATER.ADMIN_REVIEW");
        Assert.Equal(HttpStatusCode.BadRequest, (await coast.PostAsJsonAsync("/open311/v2/requests", new { service_code = "WATER.ADMIN_REVIEW", description = "Synthetic municipal pipe service review." })).StatusCode);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task ResidentIntakeUsesTenantLanguageWithoutChangingTheSubmissionRetryFingerprint(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var client = app.CreateClient(); client.DefaultRequestHeaders.Add("Idempotency-Key", "tenant-locale-retry");
        var payload = new { description = "Synthetic coastal water leak with municipality locale.", area = "Synthetic coastal locale area" };
        var created = await client.PostAsJsonAsync("/m/coast/api/incidents", payload); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync()); var id = document.RootElement.GetProperty("incident").GetProperty("id").GetString()!;
        using (TenantContext.Use("synthetic-coast")) Assert.Equal("af", (await app.Services.GetRequiredService<IncidentStore>().GetAsync(id, default))!.ResidentLanguage);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/m/coast/api/incidents", payload)).StatusCode);
    }
    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task CatalogueEditsRequireNewVersionAndBecomeLiveOnlyInTheirMunicipality(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var admin = await RbacTests.RoleClient(app, "admin");
        var catalogue = JsonNode.Parse(await admin.GetStringAsync("/api/admin/catalogue"))!.AsObject();
        var originalVersion = catalogue["version"]!.GetValue<string>();
        catalogue["version"] = "2.0.0"; catalogue["workOrderTypes"]![0]!["title"] = "Synthetic municipal catalogue edit";
        var edit = await admin.PutAsJsonAsync("/api/admin/catalogue", new { expectedVersion = originalVersion, catalogue }); Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var active = await admin.GetFromJsonAsync<JsonElement>("/api/work-order-types"); Assert.Equal("2.0.0", active.GetProperty("version").GetString());
        Assert.Equal("Synthetic municipal catalogue edit", active.GetProperty("workOrderTypes")[0].GetProperty("title").GetString());
        var triage = await admin.GetFromJsonAsync<JsonElement>("/api/ai/triage-contract");
        Assert.Contains("Synthetic municipal catalogue edit", triage.GetProperty("systemPrompt").GetString());
        using var coast = await RbacTests.RoleClient(app, "admin", tenant: "synthetic-coast");
        Assert.Equal(originalVersion, (await coast.GetFromJsonAsync<JsonElement>("/api/admin/catalogue")).GetProperty("version").GetString());
        Assert.DoesNotContain("Synthetic municipal catalogue edit", (await coast.GetFromJsonAsync<JsonElement>("/api/ai/triage-contract")).GetProperty("systemPrompt").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/admin/catalogue", new { expectedVersion = originalVersion, catalogue })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/catalogue", new { expectedVersion = "2.0.0", catalogue })).StatusCode);
        Assert.Contains(await app.Services.GetRequiredService<IncidentStore>().AuditAsync(default), entry => entry.Action == "catalogue.edited");
    }

    [Theory]
    [InlineData("unknown-key")][InlineData("invalid-hazard")][InlineData("missing-skill")][InlineData("duplicate-code")][InlineData("empty-procedure")][InlineData("removed-classifier-code")]
    public async Task CatalogueEditorRejectsSchemaAndReferenceViolations(string violation)
    {
        using var app = new TestApp(); using var admin = await RbacTests.RoleClient(app, "admin");
        var catalogue = JsonNode.Parse(await admin.GetStringAsync("/api/admin/catalogue"))!;
        var version = catalogue["version"]!.GetValue<string>(); catalogue["version"] = "2.0.0";
        switch (violation)
        {
            case "unknown-key": catalogue["dangerousField"] = true; break;
            case "invalid-hazard": catalogue["workOrderTypes"]![0]!["hazardClass"] = "safe-no-review"; break;
            case "missing-skill": catalogue["workOrderTypes"]![0]!["requiredSkills"]![0]!["code"] = "MISSING.SKILL"; break;
            case "duplicate-code": catalogue["workOrderTypes"]![1]!["code"] = catalogue["workOrderTypes"]![0]!["code"]!.DeepClone(); break;
            case "empty-procedure": catalogue["workOrderTypes"]![0]!["procedureSteps"] = new JsonArray(); break;
            case "removed-classifier-code": catalogue["workOrderTypes"]!.AsArray().RemoveAt(0); break;
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/catalogue", new { expectedVersion = version, catalogue })).StatusCode);
        Assert.Equal(version, (await admin.GetFromJsonAsync<JsonElement>("/api/admin/catalogue")).GetProperty("version").GetString());
    }

    [Fact]
    public async Task CatalogueRevisionsSurviveRestartAndFailClosedOnTampering()
    {
        var app = new TestApp { KeepData = true }; var directory = app.DataDirectory;
        using (var admin = await RbacTests.RoleClient(app, "admin"))
        {
            var catalogue = JsonNode.Parse(await admin.GetStringAsync("/api/admin/catalogue"))!; var version = catalogue["version"]!.GetValue<string>(); catalogue["version"] = "2.0.0";
            (await admin.PutAsJsonAsync("/api/admin/catalogue", new { expectedVersion = version, catalogue })).EnsureSuccessStatusCode();
        }
        app.Dispose();
        using (var restarted = new TestApp { DataDirectory = directory, KeepData = true })
        using (var admin = await RbacTests.RoleClient(restarted, "admin")) Assert.Equal("2.0.0", (await admin.GetFromJsonAsync<JsonElement>("/api/admin/catalogue")).GetProperty("version").GetString());
        var path = Path.Combine(directory, "catalogue-revisions.json"); var revisions = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        revisions[0]!["actor"] = "Tampered synthetic actor"; await File.WriteAllTextAsync(path, revisions.ToJsonString());
        using var tampered = new TestApp { DataDirectory = directory }; using var client = tampered.CreateClient();
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("/api/work-order-types")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(StoreBackends.Available), MemberType = typeof(StoreBackends))]
    public async Task AdministrationCannotEditAnotherMunicipalityOrInventAnUpdateToForeignWorkforce(string backend)
    {
        using var storageBackend = StoreBackends.Use(backend);
        using var app = new TestApp(); using var coast = await RbacTests.RoleClient(app, "admin", tenant: "synthetic-coast");
        var tenants = await coast.GetFromJsonAsync<JsonElement>("/api/admin/tenants"); var inland = tenants.EnumerateArray().Single(value => value.GetProperty("id").GetString() == "default");
        Assert.Equal(HttpStatusCode.NotFound, (await coast.PutAsJsonAsync("/api/admin/tenants/default", inland)).StatusCode);
        var crew = (await coast.GetFromJsonAsync<JsonElement>("/api/admin/crews"))[0]; var foreign = JsonNode.Parse(crew.GetRawText())!; foreign["id"] = "foreign-only-crew";
        Assert.Equal(HttpStatusCode.NotFound, (await coast.PutAsJsonAsync("/api/admin/crews/foreign-only-crew", foreign)).StatusCode);
        var worker = JsonNode.Parse((await coast.GetFromJsonAsync<JsonElement>("/api/admin/workers"))[0].GetRawText())!; worker["id"] = "foreign-only-worker";
        Assert.Equal(HttpStatusCode.NotFound, (await coast.PutAsJsonAsync("/api/admin/workers/foreign-only-worker", worker)).StatusCode);
    }
}
