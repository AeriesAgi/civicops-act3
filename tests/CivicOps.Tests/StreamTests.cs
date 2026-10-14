using System.Net.Http.Json;
namespace CivicOps.Tests;
public sealed class StreamTests
{
    [Fact]
    public async Task AuthenticatedStreamPublishesRefreshWithoutOperationalPayload()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        // Bound HTTP startup/intake separately from each active-stream read: the
        // cold authenticated fixture must not consume the event assertion budget.
        client.Timeout = TimeSpan.FromSeconds(30);
        using var lifetime = new CancellationTokenSource();
        try
        {
            using var response = await client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            response.EnsureSuccessStatusCode(); Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(lifetime.Token));
            async Task<string?> ReadLineAsync() => await reader.ReadLineAsync(lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("event: ready", await ReadLineAsync()); Assert.Equal("data: {}", await ReadLineAsync()); Assert.Equal("", await ReadLineAsync());
            (await client.PostAsJsonAsync("/api/incidents", new { description = "Synthetic private SSE sentinel pipe leak", contactEmail = "resident@example.invalid", consentToContact = true }, lifetime.Token)).EnsureSuccessStatusCode();
            Assert.Equal("event: refresh", await ReadLineAsync()); Assert.Equal("data: {}", await ReadLineAsync());
        }
        finally { lifetime.Cancel(); }
    }
}
