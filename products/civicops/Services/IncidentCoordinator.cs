using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed class IncidentCoordinator(IncidentStore store, DeterministicClassifier classifier,
    AgentPipeline pipeline, IntegrationOutbox outbox, EmailNotificationService email, TimeProvider time, CrewEligibilityService eligibility, TenantStore? tenants = null) : IDisposable
{
    private readonly SemaphoreSlim _intakeGate = new(1, 1);
    public async Task<CreateResult> CreateAsync(IncidentRequest request, string? idempotencyKey, CancellationToken ct)
    {
        var validation = RequestValidation.Intake(request, idempotencyKey) ?? PrivacyConsent.Validate(request);
        if (validation is not null) return new(new Incident(), false, validation);
        await _intakeGate.WaitAsync(ct);
        try
        {
            var normalized = request with { Description = request.Description.Trim().Normalize(System.Text.NormalizationForm.FormC), Area = RequestValidation.NormalizeArea(request.Area ?? "Needs area confirmation") };
            var fingerprint = IncidentStore.Hash(JsonSerializer.Serialize(normalized));
            var existing = await store.ListAsync(ct);
            var keyHash = string.IsNullOrEmpty(idempotencyKey) ? null : IncidentStore.Hash(idempotencyKey);
            var replay = keyHash is null ? null : existing.FirstOrDefault(x => x.IntakeKeyHash == keyHash);
            if (replay is not null)
            {
                if (replay.IntakeFingerprint != fingerprint) return new(replay, false, "This submission key already belongs to a different report.");
                await outbox.EnqueueAsync(replay, "create", ct);
                return new(replay, false);
            }
            var classified = classifier.Classify(normalized.Description);
            var residentLanguage = string.IsNullOrWhiteSpace(request.ResidentLanguage) && tenants is not null
                ? (await tenants.GetAsync(TenantContext.CurrentId, ct))?.LocaleDefaults.Language ?? "en" : request.ResidentLanguage ?? "en";
            var service = ServiceCatalog.Find(request.ServiceCode);
            var now = time.GetUtcNow();
            var incident = new Incident
            {
                Description = PiiRedactor.Redact(normalized.Description), RawDescription = PiiRedactor.Redact(normalized.Description),
                Area = normalized.Area!, NormalizedArea = normalized.Area!,
                Source = string.IsNullOrWhiteSpace(request.Source) ? "web" : request.Source.Trim(),
                SourceLabel = string.IsNullOrWhiteSpace(request.SourceLabel) ? "Resident form" : request.SourceLabel.Trim(),
                LocationProvenance = string.IsNullOrWhiteSpace(request.LocationProvenance) ? "Resident supplied; unverified" : request.LocationProvenance.Trim(),
                Location = request.ShareLocation ? request.Location : null, LocationShared = request.ShareLocation,
                ContactEmail = request.ConsentToContact ? request.ContactEmail?.Trim() : null, ContactPhone = request.ConsentToContact ? request.ContactPhone?.Trim() : null,
                ConsentToContact = request.ConsentToContact, CallbackConsent = request.CallbackConsent,
                PrivacyConsent = Services.PrivacyConsent.Capture(request, now),
                ResidentLanguage = residentLanguage, ContactPreference = request.ContactPreference ?? "none",
                ConversationId = request.ConversationId, ServiceCode = service?.ServiceCode,
                WorkOrderTypeCode = service is null ? classified.WorkOrderTypeCode
                    : service.ServiceCode.Contains('.') || service.Department != classified.Department ? service.WorkOrderTypeCode : classified.WorkOrderTypeCode,
                Category = service?.ServiceName ?? classified.Category, Department = service?.Department ?? classified.Department,
                Priority = classified.Priority, Status = IncidentStatus.PendingApproval, NeedsHumanReview = true,
                CreatedAt = now, UpdatedAt = now, ReferenceNumber = Incident.NewReference(now), IntakeFingerprint = fingerprint
            };
            var duplicate = classifier.FindDuplicate(existing, incident);
            incident.DuplicateOf = duplicate?.ReferenceNumber; incident.DuplicateConfidence = duplicate?.Confidence;
            await pipeline.EnrichAsync(incident, existing, ct);
            incident.NeedsHumanReview = true; // All new cases wait for verified human approval.
            incident.Audit.Add(new("agent-pipeline", "CivicOps backend agents", "Bounded intake, triage, duplicate check, dispatch recommendation and comms draft recorded.", time.GetUtcNow()));
            incident.Audit.Add(new("metrics-intake", "CivicOps backend agents", JsonSerializer.Serialize(new
            {
                source = incident.Source, duplicate = incident.DuplicateOf is not null,
                aiRuns = incident.AgentRuns.Count(run => !run.FallbackUsed), fallbackRuns = incident.AgentRuns.Count(run => run.FallbackUsed)
            }), time.GetUtcNow()));
            var created = await store.CreateAsync(incident, idempotencyKey, classifier, ct);
            if (created.Created)
            {
                await outbox.EnqueueAsync(created.Incident, "create", ct);
                CivicOpsTelemetry.Intake.Add(1);
                if (created.Incident.ConsentToContact) await email.NotifyAsync(created.Incident, IncidentView.SafeUpdate(created.Incident.Status, created.Incident.ResidentLanguage), ct);
            }
            return created;
        }
        finally { _intakeGate.Release(); }
    }

    public Task<ChangeResult> ApproveAsync(string id, ApprovalRequest request, string actor, CancellationToken ct) =>
        ChangeAsync(id, request.ExpectedVersion, incident =>
        {
            var reapproval = incident.Approval is not null && incident.NeedsHumanReview
                && incident.Status is IncidentStatus.Approved or IncidentStatus.Assigned or IncidentStatus.InProgress or IncidentStatus.WaitingForCitizen or IncidentStatus.Resolved;
            if (!reapproval)
            {
                var error = IncidentWorkflow.Validate(incident, IncidentStatus.Approved, actor);
                if (error is not null) return error;
                incident.Status = IncidentStatus.Approved;
            }
            incident.Approval = new(actor, request.Note?.Trim(), time.GetUtcNow());
            incident.NeedsHumanReview = false;
            incident.PublicUpdate = IncidentView.SafeUpdate(incident.Status, incident.ResidentLanguage);
            incident.Audit.Add(new(reapproval ? "reapproved" : "approved", actor, request.Note?.Trim() ?? (reapproval ? "Additional information reviewed; existing response status preserved." : "Human approval recorded."), time.GetUtcNow()));
            return null;
        }, ct);

    public async Task<ChangeResult> AssignAsync(string id, AssignmentRequest request, string actor, CancellationToken ct)
    {
        var current = await store.GetAsync(id, ct);
        if (current is null) return new(null);
        var assessed = await eligibility.EvaluateAsync(current, request.CrewId, ct);
        if (assessed is null) return new(current, "Unknown crew.");
        if (!assessed.CanAssign) return new(current, "This crew is not eligible: " + string.Join(" ", assessed.Reasons));
        return await ChangeAsync(id, request.ExpectedVersion, incident =>
        {
            var error = IncidentWorkflow.Validate(incident, IncidentStatus.Assigned, actor, assignment: true);
            if (error is not null) return error;
            incident.AssignedCrewId = assessed.Crew.Id; incident.AssignedCrewName = assessed.Crew.Name; incident.Status = IncidentStatus.Assigned;
            incident.PublicUpdate = IncidentView.SafeUpdate(incident.Status, incident.ResidentLanguage);
            incident.Audit.Add(new("assigned", actor, (request.Note?.Trim() ?? $"{assessed.Crew.Name} assigned.")
                + " Eligibility: " + assessed.Status + ". " + string.Join(" ", assessed.Reasons), time.GetUtcNow()));
            return null;
        }, ct, current);
    }

    public Task<ChangeResult> OverrideWorkOrderTypeAsync(string id, WorkOrderOverrideRequest request, string actor, CancellationToken ct) =>
        ChangeAsync(id, request.ExpectedVersion, incident =>
        {
            var type = WorkOrderCatalog.Default.Find(request.WorkOrderTypeCode);
            if (type is null) return "Unknown work-order type.";
            if (incident.Status == IncidentStatus.Closed) return "A closed case cannot change work-order type.";
            if (string.IsNullOrWhiteSpace(actor)) return "A named dispatcher is required.";
            var previous = incident.WorkOrderTypeCode ?? "legacy inferred";
            incident.WorkOrderTypeCode = type.Code; incident.Category = type.Category; incident.Department = type.Department;
            incident.ServiceCode = type.Code; incident.NeedsHumanReview = true;
            if (type.HazardClass == "critical") incident.Priority = "Urgent";
            incident.RecommendedCrewId = null;
            incident.RecommendedCrewReason = "Work-order override requires current crew qualification review.";
            incident.Approval = null; incident.AssignedCrewId = null; incident.AssignedCrewName = null;
            incident.Status = IncidentStatus.PendingApproval; incident.PublicUpdate = IncidentView.SafeUpdate(incident.Status, incident.ResidentLanguage);
            incident.Audit.Add(new("work-order-type-overridden", actor, $"{previous} -> {type.Code}; approval, assignment and crew recommendation require new review. "
                + request.Note?.Trim(), time.GetUtcNow()));
            return null;
        }, ct);

    public Task<ChangeResult> StatusAsync(string id, StatusRequest request, string actor, CancellationToken ct) =>
        ChangeAsync(id, request.ExpectedVersion, incident =>
        {
            if (!Enum.TryParse<IncidentStatus>(request.Status, true, out var next) || !Enum.IsDefined(next)) return "Unknown status.";
            if (next == IncidentStatus.Approved) return "Use the approval endpoint.";
            var error = IncidentWorkflow.Validate(incident, next, actor);
            if (error is not null) return error;
            incident.Status = next; incident.PublicUpdate = IncidentView.SafeUpdate(next, incident.ResidentLanguage);
            incident.Audit.Add(new("status", actor, request.PublicUpdate?.Trim() ?? incident.PublicUpdate, time.GetUtcNow()));
            if (next is IncidentStatus.Resolved or IncidentStatus.Closed)
                incident.Audit.Add(new(next == IncidentStatus.Resolved ? "resolved" : "closed", actor, incident.PublicUpdate, time.GetUtcNow()));
            return null;
        }, ct);

    private async Task<ChangeResult> ChangeAsync(string id, int? version, Func<Incident, string?> apply, CancellationToken ct,
        Incident? baseline = null)
    {
        // Preserve the pre-eligibility/draft snapshot. Reasoning remains outside the
        // store lock; explicit versions and all concurrent domain changes stay strict.
        var current = await store.GetAsync(id, ct);
        if (current is null) return new(null);
        if (version is not null && current.Version != version
            || baseline is not null && current.Version != baseline.Version && !store.OnlyDeliveryReceiptsChanged(baseline, current))
            return new(current, "This case changed. Refresh before trying again.", true);
        baseline ??= current;
        var previous = JsonSerializer.Deserialize<Incident>(JsonSerializer.Serialize(baseline))!;
        var error = apply(previous);
        if (error is not null) return new(previous, error);
        await pipeline.DraftCommsAsync(previous, ct);
        if (previous.Audit.Count < baseline.Audit.Count || !previous.Audit.Take(baseline.Audit.Count).SequenceEqual(baseline.Audit))
            return new(current, "Existing audit events cannot be modified or removed.");
        var changed = await store.ChangeWorkflowAsync(id, baseline, allowDeliveryRebase: version is null, incident =>
        {
            incident.WorkOrderTypeCode = previous.WorkOrderTypeCode; incident.ServiceCode = previous.ServiceCode;
            incident.Category = previous.Category; incident.Department = previous.Department; incident.Priority = previous.Priority;
            incident.RecommendedCrewId = previous.RecommendedCrewId; incident.RecommendedCrewReason = previous.RecommendedCrewReason;
            incident.Status = previous.Status; incident.Approval = previous.Approval; incident.NeedsHumanReview = previous.NeedsHumanReview;
            incident.AssignedCrewId = previous.AssignedCrewId; incident.AssignedCrewName = previous.AssignedCrewName;
            incident.PublicUpdate = previous.PublicUpdate;
            // Keep delivery receipts and provider metadata from the locked current
            // snapshot, then append this named human's prepared decision once.
            incident.Audit.AddRange(previous.Audit.Skip(baseline.Audit.Count));
            incident.AgentRuns = previous.AgentRuns; incident.CommsDrafts = previous.CommsDrafts;
            return null;
        }, ct);
        if (changed.Error is null && changed.Incident is not null) await outbox.EnqueueAsync(changed.Incident, "status", ct);
        return changed;
    }
    public void Dispose() => _intakeGate.Dispose();
}
