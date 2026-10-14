using System.Net;
using System.Text;
using System.Text.Json;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace CivicOps.Tests;

public sealed class ZendeskClientTests
{
    [Theory]
    [InlineData("Urgent", "urgent")]
    [InlineData("High", "high")]
    [InlineData("Medium", "normal")]
    [InlineData("Low", "low")]
    [InlineData("unknown", "normal")]
    [InlineData("URGENT", "urgent")]
    public void MapsPriority(string input, string expected) => Assert.Equal(expected, ZendeskClient.MapPriority(input));

    [Theory]
    [InlineData(IncidentStatus.New, "new")]
    [InlineData(IncidentStatus.Triaged, "open")]
    [InlineData(IncidentStatus.PendingApproval, "open")]
    [InlineData(IncidentStatus.Approved, "open")]
    [InlineData(IncidentStatus.Assigned, "open")]
    [InlineData(IncidentStatus.InProgress, "open")]
    [InlineData(IncidentStatus.WaitingForCitizen, "pending")]
    [InlineData(IncidentStatus.Resolved, "solved")]
    [InlineData(IncidentStatus.Closed, "solved")]
    public void MapsLifecycleWithoutManuallyClosingZendesk(IncidentStatus input, string expected) =>
        Assert.Equal(expected, ZendeskClient.MapStatus(input));

    [Theory]
    [InlineData(false, "resident@example.invalid", false)]
    [InlineData(true, "resident@example.invalid", true)]
    [InlineData(true, null, false)]
    [InlineData(true, "invalid", false)]
    public void RequesterRequiresContactAndConsent(bool consent, string? email, bool expected)
    {
        var incident = ZendeskFixture.Case(); incident.ConsentToContact = consent; incident.ContactEmail = email;
        using var body = JsonDocument.Parse(ZendeskClient.BuildTicketRequest(incident, "create", "fake-event"));
        var ticket = body.RootElement.GetProperty("ticket");
        Assert.Equal(expected, ticket.TryGetProperty("requester", out _));
        Assert.False(ticket.GetProperty("comment").GetProperty("public").GetBoolean());
        Assert.Equal(incident.ReferenceNumber, ticket.GetProperty("external_id").GetString());
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("status", false)]
    [InlineData("approved-comment", true)]
    public void OnlyExplicitlyApprovedCommsArePublic(string operation, bool expected)
    {
        var incident = ZendeskFixture.Case(); incident.PublicUpdate = "Dispatcher reviewed update.";
        using var body = JsonDocument.Parse(ZendeskClient.BuildTicketRequest(incident, operation, "fake-event"));
        var comment = body.RootElement.GetProperty("ticket").GetProperty("comment");
        Assert.Equal(expected, comment.GetProperty("public").GetBoolean());
        Assert.Contains("[CivicOps event fake-event]", comment.GetProperty("body").GetString());
        if (expected) Assert.DoesNotContain("Agent provenance", comment.GetProperty("body").GetString());
    }

    [Fact]
    public async Task CreatesTicketUsingDocumentedPathAndBasicTokenAuth()
    {
        var handler = new ZendeskFakeHandler((request, count) => count == 1
            ? ZendeskFakeHandler.Json("{\"results\":[]}") : ZendeskFakeHandler.Json("{\"ticket\":{\"id\":123}}"));
        var client = new ZendeskClient(new HttpClient(handler), ZendeskFixture.LiveOptions());
        var result = await client.DeliverAsync(ZendeskFixture.Item(), null, default);
        Assert.Equal(123, result.TicketId);
        Assert.False(result.Simulated);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.StartsWith("https://synthetic.zendesk.com/api/v2/search.json?query=", handler.Requests[0].Url);
        Assert.Contains("external_id:CIV-20261008-FAKE0001", Uri.UnescapeDataString(handler.Requests[0].Url));
        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("https://synthetic.zendesk.com/api/v2/tickets.json", handler.Requests[1].Url);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("staff@example.invalid/token:test-zendesk-token")), handler.Requests[1].Auth);
        using var sent = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("CIV-20261008-FAKE0001", sent.RootElement.GetProperty("ticket").GetProperty("external_id").GetString());
    }

    [Fact]
    public async Task ExistingExternalIdAvoidsDuplicateCreation()
    {
        var handler = new ZendeskFakeHandler((_, _) => ZendeskFakeHandler.Json("{\"results\":[{\"id\":321,\"external_id\":\"CIV-20261008-FAKE0001\"}]}"));
        var result = await new ZendeskClient(new(handler), ZendeskFixture.LiveOptions()).DeliverAsync(ZendeskFixture.Item(), null, default);
        Assert.True(result.Replayed); Assert.Equal(321, result.TicketId); Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SearchRejectsOtherExternalReferences()
    {
        var handler = new ZendeskFakeHandler((_, _) => ZendeskFakeHandler.Json("{\"results\":[{\"id\":321,\"external_id\":\"OTHER\"}]}"));
        Assert.Null(await new ZendeskClient(new(handler), ZendeskFixture.LiveOptions()).FindByExternalIdAsync("CIV-20261008-FAKE0001", default));
    }

    [Fact]
    public async Task UpdatesKnownTicketWithPutAndPrivateNote()
    {
        var handler = new ZendeskFakeHandler((_, _) => ZendeskFakeHandler.Json("{\"ticket\":{\"id\":123}}"));
        var item = ZendeskFixture.Item("status");
        await new ZendeskClient(new(handler), ZendeskFixture.LiveOptions()).DeliverAsync(item, 123, default);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://synthetic.zendesk.com/api/v2/tickets/123.json", request.Url);
    }

    [Fact]
    public async Task LostUpdateResponseDoesNotAppendSameCommentAgain()
    {
        var item = ZendeskFixture.Item("status"); item.Attempts = 2;
        var handler = new ZendeskFakeHandler((_, _) => ZendeskFakeHandler.Json($"{{\"comments\":[{{\"body\":\"update [CivicOps event {item.Id}]\"}}]}}"));
        var result = await new ZendeskClient(new(handler), ZendeskFixture.LiveOptions()).DeliverAsync(item, 123, default);
        Assert.True(result.Replayed);
        Assert.Single(handler.Requests); Assert.Contains("/comments.json?sort_order=desc", handler.Requests[0].Url);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ticket\":{\"id\":-1}}")]
    [InlineData("{\"ticket\":{\"id\":\"bad\"}}")]
    public async Task InvalidTicketResponseFailsForDurableRetry(string response)
    {
        var handler = new ZendeskFakeHandler((_, _) => ZendeskFakeHandler.Json(response));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ZendeskClient(new(handler), ZendeskFixture.LiveOptions())
            .DeliverAsync(ZendeskFixture.Item("status"), 123, default));
    }

    [Fact]
    public async Task SimulationSavesExactRequestAndNeverUsesNetwork()
    {
        using var fixture = new ZendeskFixture();
        var handler = new ZendeskFakeHandler((_, _) => throw new InvalidOperationException("No network in simulation"));
        var client = new ZendeskClient(new(handler), fixture.Options);
        var item = ZendeskFixture.Item();
        var result = await client.DeliverAsync(item, null, default);
        Assert.True(result.Simulated); Assert.Null(result.TicketUrl);
        Assert.Equal(ZendeskClient.SimulatedTicketId(item.CaseReference), result.TicketId);
        Assert.Equal(item.RequestJson, await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "zendesk-outbox", item.Id + ".json")));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FailureDoesNotExposeProviderResponseOrCredentials()
    {
        var handler = new ZendeskFakeHandler((_, _) => new(HttpStatusCode.Unauthorized)
        { Content = new StringContent("private response test-zendesk-token") });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new ZendeskClient(new(handler), ZendeskFixture.LiveOptions())
            .DeliverAsync(ZendeskFixture.Item(), null, default));
        Assert.Equal("Zendesk returned HTTP 401.", error.Message);
    }

    [Theory]
    [InlineData("https://tenant.zendesk.com")]
    [InlineData("tenant.zendesk.com")]
    [InlineData("tenant/path")]
    [InlineData("tenant space")]
    [InlineData("tenant@")]
    public void RejectsSubdomainInjection(string subdomain)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ZENDESK_SUBDOMAIN"] = subdomain }).Build();
        Assert.Throws<InvalidOperationException>(() => ZendeskOptions.FromConfiguration(config, new ZendeskEnvironment()));
    }

    [Fact]
    public void DemoModeOverridesSuppliedLiveCredentials() => Assert.False(new ZendeskOptions
    { DemoMode = true, Subdomain = "synthetic", Email = "staff@example.invalid", ApiToken = "test-zendesk-token" }.Live);

    [Fact]
    public void MissingCredentialsClearlySimulates() => Assert.False(new ZendeskOptions { Subdomain = "synthetic" }.Live);

    [Fact]
    public async Task OAuthSupportsAccountsWhichCannotCreateLegacyApiTokens()
    {
        var options = new ZendeskOptions { Subdomain = "synthetic", OAuthToken = "test-zendesk-oauth-token" };
        var handler = new ZendeskFakeHandler((_, _) => ZendeskFakeHandler.Json("{\"ticket\":{\"id\":123}}"));
        Assert.True(options.Live);
        await new ZendeskClient(new(handler), options).DeliverAsync(ZendeskFixture.Item("status"), 123, default);
        Assert.Equal("Bearer test-zendesk-oauth-token", Assert.Single(handler.Requests).Auth);
    }
}

internal sealed class ZendeskFakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string? Auth, string? Body)> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
        return respond(request, Requests.Count);
    }
    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

internal sealed class ZendeskClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan interval) => Now += interval;
}

internal sealed class ZendeskFixture : IDisposable
{
    private static string TemporaryRoot => Path.GetTempPath();
    public string Directory { get; } = Path.Combine(TemporaryRoot, "zendesk-tests", Guid.NewGuid().ToString("N"));
    public ZendeskClock Clock { get; } = new();
    public ZendeskOptions Options => new() { DataDirectory = Directory, DemoMode = true, RetryBase = TimeSpan.FromSeconds(5) };
    public IncidentStore Store { get; }
    public ZendeskFixture()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CIVICOPS_DATA_DIR"] = Directory }).Build();
        Store = new(config, new ZendeskEnvironment(), Clock);
    }
    public static ZendeskOptions LiveOptions(string? directory = null, int maximumAttempts = 5) => new()
    {
        DataDirectory = directory ?? Path.Combine(TemporaryRoot, "zendesk-fake"), Subdomain = "synthetic", Email = "staff@example.invalid",
        ApiToken = "test-zendesk-token", MaximumAttempts = maximumAttempts
    };
    public static Incident Case() => new()
    {
        Id = "fake-case-id", ReferenceNumber = "CIV-20261008-FAKE0001", Description = "Synthetic pothole needing review",
        Area = "Synthetic ward", NormalizedArea = "Synthetic ward", Category = "Roads", Department = "Roads and Stormwater", Priority = "High"
    };
    public static IntegrationOutboxItem Item(string operation = "create") => new()
    {
        Id = "fake-outbox-event", CaseReference = Case().ReferenceNumber, IncidentId = Case().Id, Operation = operation,
        Attempts = 1, RequestJson = ZendeskClient.BuildTicketRequest(Case(), operation, "fake-outbox-event")
    };
    public async Task<Incident> CreateAsync() => (await Store.CreateAsync(Case(), "fake-intake-key", new DeterministicClassifier(), default)).Incident;
    public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
}

internal sealed class ZendeskEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "CivicOps";
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "zendesk-environment-" + Guid.NewGuid().ToString("N"));
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
