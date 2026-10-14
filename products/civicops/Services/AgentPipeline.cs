using CivicOps.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CivicOps.Services;

/// <summary>Agents may draft and recommend. Approval, assignment and sending are dispatcher operations.</summary>
public sealed class AgentPipeline(AiReasoningClient client, DeterministicClassifier classifier, TimeProvider time, CrewEligibilityService? eligibility = null, IntakeRoom? room = null)
{
    public const double HumanReviewConfidenceThreshold = .75;
    private static readonly string[] DangerTerms =
    ["fire", "trapped", "live wire", "exposed wire", "life threatening", "electrocution", "flooding home", "flooding inside", "flooding my house", "flooding my home"];

    public Task EnrichAsync(Incident incident, CancellationToken cancellationToken = default) =>
        EnrichCoreAsync(incident, null, cancellationToken);

    public Task EnrichAsync(Incident incident, IReadOnlyList<Incident> existing, CancellationToken cancellationToken = default) =>
        EnrichCoreAsync(incident, existing, cancellationToken);

    private async Task EnrichCoreAsync(Incident incident, IReadOnlyList<Incident>? existing, CancellationToken cancellationToken)
    {
        using var activity = CivicOpsTelemetry.Activities.StartActivity("intake.agent_pipeline");
        var started = time.GetTimestamp();
        var text = Bound(incident.Description, 4000);
        var input = new
        {
            description = text, area = Bound(incident.Area, 160), language = NormalizeLanguage(incident.ResidentLanguage),
            contactPreference = NormalizeContact(incident.ContactPreference)
        };
        var hazards = FindHazards(text);
        var intake = await client.DecideAsync(AgentSchemas.Intake, input, () => AgentSchemas.Element(new
        {
            what = Bound(text, 600), where = input.area, hazards, language = input.language,
            contactPreference = input.contactPreference, confidence = .9, rationale = "Resident-supplied text and location; unverified."
        }), cancellationToken);
        incident.AgentRuns.Add(intake.Run);
        incident.IntakeSummary = intake.Output.GetProperty("what").GetString()!;
        // A model may suggest a location, but cannot replace the resident's submitted area.
        incident.Hazards = intake.Output.GetProperty("hazards").EnumerateArray().Select(item => item.GetString()!).Union(hazards).Take(8).ToList();
        incident.ResidentLanguage = intake.Output.GetProperty("language").GetString()!;
        incident.ContactPreference = intake.Output.GetProperty("contactPreference").GetString()!;

        var deterministic = classifier.Classify(text);
        // Respect a service-code classification already applied by the adapter; it is an additional signal.
        var service = ServiceCatalog.Find(incident.ServiceCode);
        var serviceSignal = service is null ? null : new
        {
            serviceCode = service.ServiceCode, category = service.ServiceName, department = service.Department,
            workOrderTypeCode = incident.WorkOrderTypeCode ?? (service.ServiceCode.Contains('.') || service.Department != deterministic.Department
                ? service.WorkOrderTypeCode : deterministic.WorkOrderTypeCode)
        };
        var triage = await client.DecideAsync(AgentSchemas.Triage,
            new { description = text, area = input.area, hazards = incident.Hazards, serviceSignal },
            () => AgentSchemas.Element(new
            {
                workOrderTypeCode = serviceSignal?.workOrderTypeCode ?? deterministic.WorkOrderTypeCode,
                category = serviceSignal?.category ?? deterministic.Category,
                department = serviceSignal?.department ?? deterministic.Department,
                priority = IsDanger(text) ? "Urgent" : deterministic.Priority,
                confidence = deterministic.Category == "General service" ? .45 : .9,
                rationale = serviceSignal is null ? "Deterministic service and hazard rules; a dispatcher reviews every response."
                    : "Open311 service-code signal combined with deterministic hazard rules."
            }), cancellationToken);
        incident.AgentRuns.Add(triage.Run);
        var category = triage.Output.GetProperty("category").GetString()!;
        var department = triage.Output.GetProperty("department").GetString()!;
        var priority = triage.Output.GetProperty("priority").GetString()!;
        var workOrderTypeCode = triage.Output.GetProperty("workOrderTypeCode").GetString()!;
        var disagreement = category != deterministic.Category || department != deterministic.Department || priority != deterministic.Priority
            || workOrderTypeCode != deterministic.WorkOrderTypeCode;
        var criticalType = WorkOrderCatalog.Default.Find(deterministic.WorkOrderTypeCode)!;
        var danger = IsDanger(text) || criticalType.HazardClass == "critical";
        // Critical electrical/flood hazards retain their qualification requirements even if a model suggests another service.
        if (criticalType.HazardClass == "critical")
        {
            workOrderTypeCode = criticalType.Code; category = criticalType.Category; department = criticalType.Department;
        }
        incident.WorkOrderTypeCode = workOrderTypeCode;
        incident.Category = category;
        incident.Department = department;
        incident.Priority = danger ? "Urgent" : priority;
        incident.NeedsHumanReview |= triage.Run.Confidence < HumanReviewConfidenceThreshold || disagreement
            || danger || intake.Run.Confidence < HumanReviewConfidenceThreshold;
        if (danger && (priority != "Urgent" || triage.Output.GetProperty("workOrderTypeCode").GetString() != workOrderTypeCode))
            incident.AgentRuns.Add(triage.Run with
            {
                Id = Guid.NewGuid().ToString("N"), AgentName = "TriageSafetyFloor", Provider = "deterministic-fallback", Model = "hazard-rules-v1",
                EndpointLabel = "offline", LatencyMs = 0, Output = System.Text.Json.JsonSerializer.Serialize(new { priority = "Urgent", workOrderTypeCode }), Confidence = 1,
                Rationale = "Critical catalogue hazards retain their work-order qualification requirements and floor priority at Urgent.", FallbackUsed = true, Error = null
            });
        incident.Audit.Add(new("triaged", "CivicOps triage", $"{workOrderTypeCode}: {incident.Priority}; dispatcher review required.", time.GetUtcNow()));
        CivicOpsTelemetry.TriageLatency.Record(time.GetElapsedTime(started).TotalSeconds);

        if (existing is not null)
        {
            var match = classifier.FindDuplicate(existing, incident);
            incident.DuplicateOf = match?.ReferenceNumber;
            incident.DuplicateConfidence = match?.Confidence;
        }
        var candidate = incident.DuplicateOf;
        var candidateDescription = existing?.FirstOrDefault(item => item.ReferenceNumber == candidate)?.Description;
        var duplicateDefinition = AgentSchemas.Duplicate with
        {
            Validate = value => AgentSchemas.Duplicate.Validate(value)
                && (candidate is not null || !value.GetProperty("isDuplicate").GetBoolean())
        };
        var duplicate = await client.DecideAsync(duplicateDefinition,
            new
            {
                description = text, candidateReference = candidate,
                candidateDescription = candidateDescription is null ? null : Bound(candidateDescription, 4000),
                heuristicConfidence = incident.DuplicateConfidence
            },
            () => AgentSchemas.Element(new
            {
                isDuplicate = candidate is not null, confidence = incident.DuplicateConfidence ?? 1,
                rationale = candidate is null ? "No heuristic duplicate candidate. No reports are merged automatically."
                    : "Heuristic candidate requires dispatcher review; reports remain separate."
            }), cancellationToken);
        incident.AgentRuns.Add(duplicate.Run);
        if (candidate is not null) incident.NeedsHumanReview = true;
        if (room is { Enabled: true }) await room.ConveneAsync(incident, cancellationToken);

        var qualificationResults = eligibility is null ? null : await eligibility.ListAsync(incident, cancellationToken);
        var crews = qualificationResults is null
            ? IncidentWorkflow.Crews.Where(crew => crew.Department == incident.Department).ToArray()
            : qualificationResults.Where(result => result.CanAssign).Select(result => result.Crew).ToArray();
        var dispatch = await client.DecideAsync(AgentSchemas.Dispatch(crews.Select(crew => crew.Id)),
            new { category = incident.Category, department = incident.Department, workOrderTypeCode = incident.WorkOrderTypeCode, priority = incident.Priority, eligibleCrews = crews, qualifications = qualificationResults },
            () => AgentSchemas.Element(new
            {
                crewId = crews.FirstOrDefault()?.Id ?? "", confidence = crews.Length == 0 ? 0 : .9,
                rationale = crews.Length == 0 ? "No eligible crew; dispatcher escalation required."
                    : "Work-order skills and certification policy checked. Recommendation only; human approval and assignment required."
            }), cancellationToken);
        incident.AgentRuns.Add(dispatch.Run);
        incident.RecommendedCrewId = NullIfEmpty(dispatch.Output.GetProperty("crewId").GetString());
        incident.RecommendedCrewReason = dispatch.Run.Rationale;
        await DraftCommsAsync(incident, cancellationToken);
    }

    public async Task DraftCommsAsync(Incident incident, CancellationToken cancellationToken = default)
    {
        var language = NormalizeLanguage(incident.ResidentLanguage);
        var status = incident.Status.ToString();
        foreach (var older in incident.CommsDrafts.Where(draft => draft.State == "PendingReview" && draft.Status != status))
            older.State = "Superseded";
        var comms = await client.DecideAsync(AgentSchemas.Comms(language, incident.ReferenceNumber),
            new { reference = incident.ReferenceNumber, status, language, publicUpdate = Bound(incident.PublicUpdate, 600) },
            () => AgentSchemas.Element(new
            {
                language, text = FallbackComms(incident.ReferenceNumber, incident.Status, language), confidence = 1,
                rationale = "A bounded status template; dispatcher review required before sending."
            }), cancellationToken);
        incident.AgentRuns.Add(comms.Run);
        incident.CommsDrafts.Add(new CommsDraft(Guid.NewGuid().ToString("N"), status, language,
            comms.Output.GetProperty("text").GetString()!, time.GetUtcNow()) { AgentRunId = comms.Run.Id });
    }

    public static bool IsDanger(string text)
    {
        var normalized = text.ToLowerInvariant();
        return DangerTerms.Any(normalized.Contains) || DeterministicClassifier.Priority(normalized) == "Urgent";
    }
    public static string[] FindHazards(string text) => DangerTerms.Where(text.ToLowerInvariant().Contains).Take(8).ToArray();
    public static string NormalizeLanguage(string? language) => ResidentMessages.Normalize(language);
    private static string NormalizeContact(string? contact) => AgentSchemas.ContactPreferences.Contains(contact, StringComparer.Ordinal) ? contact! : "none";
    private static string Bound(string text, int length) => text.Length <= length ? text : text[..length];
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FallbackComms(string reference, IncidentStatus status, string language) =>
        ResidentMessages.Draft(reference, status, language);

}

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddCivicOpsAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(WorkOrderCatalog.Default);
        services.TryAddSingleton<WorkforceStore>();
        services.TryAddSingleton<CrewEligibilityService>();
        services.TryAddSingleton<DeterministicClassifier>();
        services.AddSingleton(AiOptions.FromConfiguration(configuration));
        services.AddHttpClient("civicops-ai", http => http.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton(provider => new AiReasoningClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("civicops-ai"),
            provider.GetRequiredService<AiOptions>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetService<SpendCaps>()));
        services.AddSingleton(IntakeRoomOptions.From(configuration));
        services.AddSingleton<IntakeRoom>();
        services.AddSingleton<AgentPipeline>();
        return services;
    }
}
