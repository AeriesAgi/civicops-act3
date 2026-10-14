using CivicOps.Models;
using CivicOps.Services;
namespace CivicOps.Tests;
public sealed class ClassifierWorkflowTests
{
    [Theory]
    [InlineData("Stormwater water leak in road", "Roads")]
    [InlineData("Blocked drain with leaking water", "Roads")]
    [InlineData("storm water rising outside", "Roads")]
    [InlineData("a pipe leak by the road", "Water")]
    [InlineData("pothole at the intersection", "Roads")]
    [InlineData("traffic light not working", "Roads")]
    [InlineData("power outage at hall", "Electricity")]
    [InlineData("streetlight flickering", "Electricity")]
    [InlineData("rubbish dumping outside", "Waste")]
    [InlineData("waste at the depot", "Waste")]
    [InlineData("a broken bench", "General service")]
    public void ClassifierServiceRegressions(string text, string expected) => Assert.Equal(expected, new DeterministicClassifier().Classify(text).Category);

    [Theory]
    [InlineData("a person is trapped", "Urgent")]
    [InlineData("fire near the depot", "Urgent")]
    [InlineData("live wire fallen", "Urgent")]
    [InlineData("water flooding my home", "Urgent")]
    [InlineData("blocked pipe outside", "High")]
    [InlineData("water flood outside", "High")]
    [InlineData("power outage outside", "High")]
    [InlineData("small leak, no rush", "Medium")]
    [InlineData("please inspect the hall", "Medium")]
    public void ClassifierPriorityRegressions(string text, string expected) => Assert.Equal(expected, new DeterministicClassifier().Classify(text).Priority);

    public static IEnumerable<object[]> Transitions()
    {
        var allowed = new Dictionary<IncidentStatus, IncidentStatus[]>
        {
            [IncidentStatus.New] = [IncidentStatus.Triaged, IncidentStatus.Closed],
            [IncidentStatus.Triaged] = [IncidentStatus.PendingApproval, IncidentStatus.Approved, IncidentStatus.Closed],
            [IncidentStatus.PendingApproval] = [IncidentStatus.Approved, IncidentStatus.Triaged, IncidentStatus.Closed],
            [IncidentStatus.Approved] = [IncidentStatus.Assigned, IncidentStatus.Closed],
            [IncidentStatus.Assigned] = [IncidentStatus.InProgress, IncidentStatus.Closed],
            [IncidentStatus.InProgress] = [IncidentStatus.Resolved, IncidentStatus.WaitingForCitizen, IncidentStatus.Closed],
            [IncidentStatus.WaitingForCitizen] = [IncidentStatus.InProgress, IncidentStatus.Resolved, IncidentStatus.Closed],
            [IncidentStatus.Resolved] = [IncidentStatus.Closed, IncidentStatus.InProgress], [IncidentStatus.Closed] = []
        };
        foreach (var from in Enum.GetValues<IncidentStatus>()) foreach (var to in Enum.GetValues<IncidentStatus>()) yield return [from, to, allowed[from].Contains(to)];
    }
    [Theory, MemberData(nameof(Transitions))]
    public void AllLifecycleTransitionsHaveEnforcedBoundaries(IncidentStatus from, IncidentStatus to, bool allowed)
    {
        var incident = new Incident { Status = from, Approval = new("Verified dispatcher", null, DateTimeOffset.UtcNow), NeedsHumanReview = false, AssignedCrewId = "water-01" };
        Assert.Equal(allowed, IncidentWorkflow.Validate(incident, to, "Verified dispatcher", assignment: true) is null);
    }
    [Theory]
    [InlineData(IncidentStatus.Assigned)] [InlineData(IncidentStatus.InProgress)]
    public void DispatchAlwaysRequiresApproval(IncidentStatus next)
    {
        var incident = new Incident { Status = next == IncidentStatus.Assigned ? IncidentStatus.Approved : IncidentStatus.Assigned };
        Assert.NotNull(IncidentWorkflow.Validate(incident, next, "Verified dispatcher", true));
    }
    [Fact]
    public void DirectAssignmentTransitionRequiresAssignmentEndpoint() => Assert.NotNull(IncidentWorkflow.Validate(new Incident { Status = IncidentStatus.Approved }, IncidentStatus.Assigned, "Verified dispatcher"));

}
