using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CivicOps.Services;

namespace CivicOps.Tests;

public sealed class MultilingualSafetyTests
{
    [Theory]
    [InlineData("Daar is brand in die huis.", "Urgent")]
    [InlineData("BRAND! Help asseblief.", "Urgent")]
    [InlineData("Mense is vasgekeer langs die pad.", "Urgent")]
    [InlineData("Daar is 'n lewendige draad op die pad.", "Urgent")]
    [InlineData("My huis oorstroom en ons benodig hulp.", "Urgent")]
    [InlineData("Die brandweer het 'n padteken omgestamp.", "Medium")]
    [InlineData("The brand new street sign is crooked.", "Medium")]
    [InlineData("A synthetic unbranded service marker is missing.", "Medium")]
    [InlineData("Synthetic pothole in the road.", "Medium")]
    public void ClassifierAndPipelineShareConservativeAfrikaansHazardFloor(string description, string expected)
    {
        Assert.Equal(expected, new DeterministicClassifier().Classify(description).Priority);
        Assert.Equal(expected == "Urgent", AgentPipeline.IsDanger(description));
    }
    [Fact]
    public async Task AfrikaansResidentToolAppliesUrgencyWithoutAutomaticDispatch()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/channels/elevenlabs/tools/report_incident", new
        {
            conversation_id = "synthetic-af-safety", turn_id = "af-turn", description = "Daar is 'n lewendige draad op die pad.", area = "Synthetic ward", language = "af"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var reference = body.GetProperty("referenceNumber").GetString();
        var incident = await client.GetFromJsonAsync<JsonElement>("/api/incidents/" + reference);
        Assert.Equal("Urgent", incident.GetProperty("priority").GetString());
        Assert.Equal("PendingApproval", incident.GetProperty("status").GetString());
    }
}
