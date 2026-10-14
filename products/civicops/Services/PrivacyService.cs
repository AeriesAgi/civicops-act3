using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed class PrivacyService(IConfiguration configuration, IWebHostEnvironment environment,
    IncidentStore incidents, TimeProvider clock, IntegrationOutbox? outbox = null, EmailNotificationService? email = null, DurableStorage? persistence = null) : IDisposable
{
    private readonly DurableStorage _storage = persistence ?? new DurableStorage(configuration, environment);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string PathName => Path.Combine(TenantContext.DirectoryFor(DataPaths.Resolve(configuration, environment)), "privacy-requests.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string? InformationOfficerContact => RequestValidation.Text(configuration["CIVICOPS_INFORMATION_OFFICER_CONTACT"], "informationOfficerContact", 254) is null
        && configuration["CIVICOPS_INFORMATION_OFFICER_CONTACT"]?.Any(char.IsControl) is not true
        ? configuration["CIVICOPS_INFORMATION_OFFICER_CONTACT"] : null;
    public async Task<IReadOnlyList<DataSubjectRequest>> ListAsync(CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try { return await ReadAsync(ct); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<DataSubjectRequest> CreateAsync(DataSubjectRequestCreate request, string actor, CancellationToken ct)
    {
        if (request.Action is not ("access" or "correction" or "anonymisation")) throw new InvalidOperationException("Choose access, correction or anonymisation.");
        var error = RequestValidation.Text(request.ReferenceNumber, "referenceNumber", 64, 1)
            ?? RequestValidation.Text(request.RequestNote, "requestNote", 1000, 10);
        if (error is not null) throw new InvalidOperationException(error);
        var incident = await incidents.GetAsync(request.ReferenceNumber, ct) ?? throw new KeyNotFoundException("Case not found.");
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var requests = await ReadAsync(ct);
            if (requests.Count(request => request.Status != "fulfilled") >= 1000) throw new InvalidOperationException("Privacy request review queue is full.");
            var record = new DataSubjectRequest(Guid.NewGuid().ToString("N"), TenantContext.CurrentId, incident.Id,
                incident.ReferenceNumber, request.Action, "pending-verification", PiiRedactor.Redact(request.RequestNote), null,
                actor, clock.GetUtcNow());
            await SaveAsync(requests.Append(record).ToList(), ct);
            await AuditAsync(record, "requested", actor, ct);
            return record;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<DataSubjectRequest> VerifyAsync(string id, DataSubjectVerification verification, string actor, CancellationToken ct)
    {
        if (RequestValidation.Text(verification.VerificationNote, "verificationNote", 1000, 10) is { } error) throw new InvalidOperationException(error);
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var requests = await ReadAsync(ct); var index = requests.FindIndex(record => record.Id == id);
            if (index < 0) throw new KeyNotFoundException("Privacy request not found.");
            if (requests[index].Status != "pending-verification") throw new InvalidOperationException("Only pending requests can be verified.");
            var record = requests[index] with { Status = "verified", VerificationNote = PiiRedactor.Redact(verification.VerificationNote), VerifiedBy = actor, VerifiedAt = clock.GetUtcNow() };
            requests[index] = record; await SaveAsync(requests, ct); await AuditAsync(record, "identity-verified-by-admin", actor, ct);
            return record;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    public async Task<object> FulfilAsync(string id, DataSubjectFulfilment fulfilment, string actor, CancellationToken ct)
    {
        await using var transaction = _storage.Begin();
        await transaction.InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var requests = await ReadAsync(ct); var index = requests.FindIndex(record => record.Id == id);
            if (index < 0) throw new KeyNotFoundException("Privacy request not found.");
            var record = requests[index];
            if (record.Status != "verified") throw new InvalidOperationException("An administrator must record identity verification before fulfilment.");
            var incident = await incidents.GetAsync(record.IncidentId, ct) ?? throw new KeyNotFoundException("Case not found.");
            object result;
            if (record.Action == "access")
                result = new { requestId = record.Id, municipality = TenantContext.CurrentId, exportedAt = clock.GetUtcNow(),
                    incident, contact = new { incident.ContactEmail, incident.ContactPhone },
                    boundaries = "Local case data only. Provider-held records, backups and legacy immutable audit require separate Information Officer review." };
            else
            {
                if (fulfilment.ExpectedVersion is null) throw new InvalidOperationException("expectedVersion is required for correction or anonymisation.");
                if (record.Action == "correction")
                {
                    var error = RequestValidation.Intake(new IncidentRequest(fulfilment.Description ?? incident.Description,
                        ContactEmail: fulfilment.ContactEmail, ContactPhone: fulfilment.ContactPhone), null);
                    if (error is not null) throw new InvalidOperationException(error);
                    if (!string.IsNullOrWhiteSpace(fulfilment.ContactEmail) && !new EmailAddressAttribute().IsValid(fulfilment.ContactEmail))
                        throw new InvalidOperationException("Invalid contactEmail.");
                }
                var changed = await incidents.ChangeAsync(incident.Id, fulfilment.ExpectedVersion, candidate =>
                {
                    if (record.Action == "correction")
                    {
                        if (fulfilment.Description is not null)
                            candidate.Description = candidate.RawDescription = PiiRedactor.Redact(fulfilment.Description.Trim().Normalize(System.Text.NormalizationForm.FormC));
                        candidate.ContactEmail = fulfilment.ConsentToContact ? fulfilment.ContactEmail?.Trim() : null;
                        candidate.ContactPhone = fulfilment.ConsentToContact ? fulfilment.ContactPhone?.Trim() : null;
                        candidate.ConsentToContact = fulfilment.ConsentToContact;
                        candidate.CallbackConsent = false; candidate.ContactPreference = fulfilment.ConsentToContact ? "email" : "none";
                        candidate.NeedsHumanReview = true;
                    }
                    else
                    {
                        candidate.PrivacyAnonymised = true;
                        candidate.Description = candidate.RawDescription = "Resident narrative anonymised following a verified data-subject request.";
                        candidate.Area = candidate.NormalizedArea = "Area anonymised";
                        candidate.Location = null; candidate.LocationShared = false; candidate.MunicipalityId = candidate.WardId = null;
                        candidate.IntakeSummary = "Anonymised"; candidate.ContactEmail = candidate.ContactPhone = null;
                        candidate.ConsentToContact = candidate.CallbackConsent = false; candidate.ContactPreference = "none";
                        candidate.RawTranscript = candidate.TranscriptSummary = candidate.CallMetadata = candidate.ConversationId = null;
                        candidate.IntakeFingerprint = null; candidate.AgentRuns.Clear(); candidate.CommsDrafts.Clear();
                        candidate.RecommendedCrewReason = "Resident data anonymised"; candidate.LocationProvenance = "Anonymised";
                        candidate.PublicUpdate = IncidentView.SafeUpdate(candidate.Status);
                    }
                    candidate.Audit.Add(new("privacy." + record.Action, actor,
                        $"Verified data-subject request {record.Id} fulfilled; prior immutable audit retained for lawful review.", clock.GetUtcNow()));
                    return null;
                }, ct);
                if (changed.Error is not null) throw new PrivacyConflictException(changed.Error);
                if (outbox is not null) await outbox.ErasePersonalDataAsync(incident.Id, record.Action == "anonymisation", ct);
                if (email is not null) await email.EraseCaseOutboxAsync(incident.ReferenceNumber, ct);
                result = new { requestId = record.Id, action = record.Action, status = "fulfilled", version = changed.Incident!.Version,
                    residual = "Legacy immutable audit, provider records and backups are retained for separate Information Officer review; this is local anonymisation, not full erasure." };
            }
            var fulfilled = record with { Status = "fulfilled", FulfilledBy = actor, FulfilledAt = clock.GetUtcNow() };
            requests[index] = fulfilled; await SaveAsync(requests, ct); await AuditAsync(fulfilled, "fulfilled", actor, ct);
            return result;
        }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private Task AuditAsync(DataSubjectRequest record, string operation, string actor, CancellationToken ct) => incidents.RecordSystemEventAsync(
        new("privacy.request-" + operation, actor, $"Request {record.Id}; action {record.Action}; case {record.ReferenceNumber}; stateHash {IncidentStore.Hash(JsonSerializer.Serialize(record))}; verification notes retained separately with PII redaction.", clock.GetUtcNow()), ct);
    private async Task<List<DataSubjectRequest>> ReadAsync(CancellationToken ct)
    {
        var persisted = await _storage.ReadAsync(PathName, ct);
        if (persisted is null) return [];
        var records = JsonSerializer.Deserialize<List<DataSubjectRequest>>(persisted, Json) ?? throw new InvalidDataException("Invalid privacy request store.");
        if (records.Any(record => record.TenantId != TenantContext.CurrentId)) throw new InvalidDataException("Privacy request partition municipality mismatch.");
        var audit = await incidents.AuditAsync(ct);
        if (!(await incidents.VerifyAuditAsync(ct)).Valid) throw new InvalidDataException("Privacy audit chain failed integrity verification.");
        foreach (var record in records)
        {
            var last = audit.LastOrDefault(entry => entry.Action.StartsWith("privacy.request-", StringComparison.Ordinal)
                && entry.Detail.StartsWith("Request " + record.Id + ";", StringComparison.Ordinal));
            if (last is null || !last.Detail.Contains("stateHash " + IncidentStore.Hash(JsonSerializer.Serialize(record)) + ";", StringComparison.Ordinal))
                throw new InvalidDataException("Privacy request differs from authenticated audit state; writes are disabled.");
        }
        return records;
    }
    private Task SaveAsync(List<DataSubjectRequest> records, CancellationToken ct) => _storage.WriteAsync(PathName, JsonSerializer.Serialize(records, Json), ct);
    public void Dispose() => _gate.Dispose();
}
public sealed class PrivacyConflictException(string message) : Exception(message);
