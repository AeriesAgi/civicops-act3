using CivicOps.Models;

namespace CivicOps.Services;

public static class IncidentWorkflow
{
    public static readonly Crew[] Crews =
    [
        new("water-01", "Water crew 01", "Water and Sanitation", "Pipe leaks and water service repairs"),
        new("electricity-01", "Electricity crew 01", "Electricity", "Power outages and street lighting"),
        new("roads-01", "Roads crew 01", "Roads and Stormwater", "Potholes, blocked drains and stormwater"),
        new("waste-01", "Waste crew 01", "Waste Management", "Waste collection and dumping"),
        new("general-01", "Public works crew 01", "Public Works", "General service inspection"),
        new("emergency-01", "Emergency response crew 01", "Emergency Services", "Coordinated municipal flood support")
    ];

    private static readonly Dictionary<IncidentStatus, IncidentStatus[]> Allowed = new()
    {
        [IncidentStatus.New] = [IncidentStatus.Triaged, IncidentStatus.Closed],
        [IncidentStatus.Triaged] = [IncidentStatus.PendingApproval, IncidentStatus.Approved, IncidentStatus.Closed],
        [IncidentStatus.PendingApproval] = [IncidentStatus.Approved, IncidentStatus.Triaged, IncidentStatus.Closed],
        [IncidentStatus.Approved] = [IncidentStatus.Assigned, IncidentStatus.Closed],
        [IncidentStatus.Assigned] = [IncidentStatus.InProgress, IncidentStatus.Closed],
        [IncidentStatus.InProgress] = [IncidentStatus.Resolved, IncidentStatus.WaitingForCitizen, IncidentStatus.Closed],
        [IncidentStatus.WaitingForCitizen] = [IncidentStatus.InProgress, IncidentStatus.Resolved, IncidentStatus.Closed],
        [IncidentStatus.Resolved] = [IncidentStatus.Closed, IncidentStatus.InProgress],
        [IncidentStatus.Closed] = []
    };

    public static string? Validate(Incident incident, IncidentStatus next, string? actor, bool assignment = false)
    {
        if (incident.Status == next) return "This case is already in that status.";
        if (!Allowed.TryGetValue(incident.Status, out var allowed) || !allowed.Contains(next))
            return $"Cannot move an incident from {incident.Status} to {next}.";
        if (string.IsNullOrWhiteSpace(actor)) return "A named dispatcher is required for status changes.";
        if (next == IncidentStatus.Assigned && !assignment)
            return "Choose an eligible crew through the assignment endpoint.";
        if (next is IncidentStatus.Assigned or IncidentStatus.InProgress
            && (incident.Approval is null || incident.NeedsHumanReview))
            return "Human approval must be recorded before dispatch or response.";
        if (next == IncidentStatus.InProgress && incident.AssignedCrewId is null)
            return "Assign an eligible crew before beginning a response.";
        return null;
    }
}
