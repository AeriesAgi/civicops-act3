using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

/// <summary>CIVICOPS_INTAKE_ROOM=true enables the advisory three-seat intake room after triage.</summary>
public sealed class IntakeRoomOptions
{
    public bool Enabled { get; init; }
    public int TimeoutMs { get; init; } = 25_000;
    public static IntakeRoomOptions From(IConfiguration configuration) => new()
    {
        Enabled = bool.TryParse(configuration["CIVICOPS_INTAKE_ROOM"], out var enabled) && enabled,
        TimeoutMs = int.TryParse(configuration["CIVICOPS_INTAKE_ROOM_TIMEOUT_MS"], out var timeout) ? Math.Clamp(timeout, 1_000, 60_000) : 25_000
    };
}

/// <summary>
/// Port of the Tablekeeper Band classroom: fixed coordinator, implementer and reviewer seats run in parallel over
/// the configured provider chain with one room timeout. Each seat validates against its own schema and falls back
/// to deterministic rules on any failure. Output is advisory; the recommendation never assigns, dispatches or contacts.
/// </summary>
public sealed class IntakeRoom(AiReasoningClient client, IntakeRoomOptions options, TimeProvider time)
{
    public bool Enabled => options.Enabled;

    public async Task ConveneAsync(Incident incident, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        var text = incident.Description.Length <= 4000 ? incident.Description : incident.Description[..4000];
        var area = incident.Area.Length <= 160 ? incident.Area : incident.Area[..160];
        var danger = AgentPipeline.IsDanger(text);
        var hazards = AgentPipeline.FindHazards(text);
        // Only the bounded report and triage outcome reach the seats; the serializer redacts contact details.
        var input = new { description = text, area, category = incident.Category, department = incident.Department, priority = incident.Priority, hazards = incident.Hazards };
        using var room = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        room.CancelAfter(TimeSpan.FromMilliseconds(options.TimeoutMs));
        var coordinatorFallback = () => AgentSchemas.Element(new
        {
            queue = danger ? "emergency-escalation" : incident.Priority is "Urgent" or "High" ? "urgent-field"
                : incident.Category == "General service" ? "needs-clarification" : "standard-field",
            confidence = .8, rationale = "Deterministic queue rules from triage priority and hazard terms."
        });
        var extractorFallback = () => AgentSchemas.Element(new
        {
            locationHint = area, hazards, confidence = .8, rationale = "Resident-supplied area restated; hazard terms from fixed rules. Location unverified."
        });
        var reviewerFallback = () => AgentSchemas.Element(new
        {
            requiresHumanReview = danger || incident.NeedsHumanReview, safetyConcern = danger ? "life-safety" : "none",
            confidence = .8, rationale = "Deterministic hazard rules; a dispatcher reviews every report."
        });
        AgentDecision[] seats;
        try
        {
            // Independent seats run in parallel; trace order stays fixed (coordinator, implementer, reviewer).
            seats = await Task.WhenAll(
                client.DecideAsync(AgentSchemas.RoomCoordinator, input, coordinatorFallback, room.Token),
                client.DecideAsync(AgentSchemas.RoomExtractor, input, extractorFallback, room.Token),
                client.DecideAsync(AgentSchemas.RoomReviewer, input, reviewerFallback, room.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Room timeout: every seat is simulated locally, labelled as such.
            seats = await Task.WhenAll(
                client.DecideOfflineAsync(AgentSchemas.RoomCoordinator, input, coordinatorFallback, "room_timeout"),
                client.DecideOfflineAsync(AgentSchemas.RoomExtractor, input, extractorFallback, "room_timeout"),
                client.DecideOfflineAsync(AgentSchemas.RoomReviewer, input, reviewerFallback, "room_timeout"));
        }
        foreach (var seat in seats) incident.AgentRuns.Add(seat.Run);
        var queue = seats[0].Output.GetProperty("queue").GetString()!;
        // A model cannot downgrade a deterministic life-safety signal.
        if (danger) queue = "emergency-escalation";
        var review = seats[2].Output.GetProperty("requiresHumanReview").GetBoolean() || danger;
        var concern = danger ? "life-safety" : seats[2].Output.GetProperty("safetyConcern").GetString()!;
        incident.NeedsHumanReview |= review || queue is "emergency-escalation" or "needs-clarification";
        var live = seats.Count(seat => !seat.Run.FallbackUsed);
        var mode = live == 3 ? "live" : live == 0 ? "simulated" : "mixed";
        var summary = $"Intake room ({mode}: {live}/3 seats live): queue {queue}; safety {concern}; "
            + (review ? "priority human review." : "standard human review.") + " Recommendation only; a dispatcher approves any dispatch.";
        incident.AgentRuns.Add(seats[0].Run with
        {
            Id = Guid.NewGuid().ToString("N"), AgentName = "IntakeRoom.Recommendation", Provider = "intake-room-rules", Model = "room-chair-v1",
            EndpointLabel = "offline", LatencyMs = 0, Confidence = seats.Min(seat => seat.Run.Confidence), FallbackUsed = false, Error = null,
            Output = JsonSerializer.Serialize(new { mode, liveSeats = live, queue, safetyConcern = concern, requiresHumanReview = review }),
            Rationale = summary, At = time.GetUtcNow(), ToolPermissions = ["read:intake-room", "recommend:intake-queue"]
        });
        incident.Audit.Add(new("intake-room", "CivicOps intake room", summary, time.GetUtcNow()));
    }
}
