using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

/// <summary>All normalized channels use the existing case coordinator; no agent has send authority.</summary>
public sealed class ChannelService(ChannelStore journal, IncidentCoordinator coordinator, IncidentStore incidents,
    ChannelOptions options, IConfiguration configuration, IHttpClientFactory http, TimeProvider time, GeoStore geo, SosStore sos) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const string LocationReviewRequired = "location-review-required";
    public async Task<IReadOnlyList<ChannelIntakeResult>> ReceiveAsync(IReadOnlyList<InboundMessage> messages,
        string bodyHash, string provider, string nonce, bool simulated, CancellationToken ct)
    {
        if (messages is null) throw new ChannelRequestException(400, "Normalized channel messages are required.");
        foreach (var message in messages) ChannelStore.Validate(message);
        await _gate.WaitAsync(ct);
        try
        {
            await journal.ClaimEnvelopeAsync(provider, nonce, ct);
            var results = new List<ChannelIntakeResult>();
            foreach (var message in messages)
            {
                var stableMessage = message with { ReceivedAt = default };
                var fingerprint = IncidentStore.Hash(JsonSerializer.Serialize(stableMessage));
                var prepared = await journal.PrepareAsync(message, fingerprint, nonce, simulated, ct, nonceAlreadyClaimed: true);
                if (prepared.Result is not null)
                {
                    // The channel journal lock is already released. SMS/WhatsApp panic phrases record one idempotent control-room alert; voice stays information-only.
                    if (prepared.Result.Command == "SOS" && message.Channel is "sms" or "whatsapp")
                        await sos.CreateAsync(new SosCreateRequest("channel-" + prepared.MessageKey, message.Channel, message.Text, message.From, null, false, null, null, null), null, null, ct);
                    results.Add(prepared.Result); continue;
                }
                // Persisted preparation survives a crash. Agent reasoning runs outside journal/SQL locks.
                var existing = prepared.ExistingCaseReference is null ? null : await incidents.GetAsync(prepared.ExistingCaseReference, ct);
                if (prepared.ExistingCaseReference is not null && existing is null)
                    throw new ChannelRequestException(409, "The linked case is no longer available.");
                var intakeKey = "channel:" + prepared.MessageKey;
                var recovered = prepared.ExistingCaseReference is null ? null : (await incidents.ListAsync(ct))
                    .SingleOrDefault(incident => incident.IntakeKeyHash == IncidentStore.Hash(intakeKey));
                // A resident follow-up may enrich a pending draft. Reviewed response destinations
                // stay fixed; the new point instead becomes a separate case for human review.
                var alreadyLinked = existing?.Audit.Any(entry => entry.Action == "channels.intake." + prepared.MessageKey) is true;
                var attachLocation = recovered is null && existing is not null && (CanAttachLocation(existing) || alreadyLinked);
                var created = recovered is not null ? new CreateResult(recovered, false)
                    : attachLocation ? new CreateResult(existing!, false)
                    : await coordinator.CreateAsync(prepared.Request!, intakeKey, ct);
                if (created.Error is not null) throw new ChannelRequestException(409, created.Error);
                var changed = await RecordIntakeAsync(created.Incident, prepared, message, attachLocation, ct);
                if (changed.Error == LocationReviewRequired)
                {
                    // Approval/assignment can race the earlier read. Recheck inside the incident
                    // lock and use the same durable intake key when a separate review is required.
                    created = await coordinator.CreateAsync(prepared.Request!, intakeKey, ct);
                    if (created.Error is not null) throw new ChannelRequestException(409, created.Error);
                    changed = await RecordIntakeAsync(created.Incident, prepared, message, false, ct);
                }
                if (changed.Error is not null && changed.Error != "already-linked") throw new ChannelRequestException(409, changed.Error);
                results.Add(await journal.CompleteAsync(prepared, changed.Incident ?? created.Incident, simulated, ct));
            }
            return results;
        }
        finally { _gate.Release(); }
    }
    private static bool CanAttachLocation(Incident incident) => incident.Status == IncidentStatus.PendingApproval
        && incident.Approval is null && incident.AssignedCrewId is null && incident.AssignedCrewName is null;
    private async Task<ChangeResult> RecordIntakeAsync(Incident current, PreparedChannelIntake prepared,
        InboundMessage message, bool attachLocation, CancellationToken ct)
    {
        var assigned = attachLocation ? await geo.AssignAsync(prepared.Request!.Location, ct)
            : (MunicipalityId: (string?)null, WardId: (string?)null);
        return await incidents.ChangeAsync(current.Id, null, incident =>
        {
            if (incident.Audit.Any(entry => entry.Action == "channels.intake." + prepared.MessageKey)) return "already-linked";
            if (attachLocation)
            {
                if (!CanAttachLocation(incident)) return LocationReviewRequired;
                incident.Location = prepared.Request!.Location; incident.LocationShared = true;
                incident.MunicipalityId = assigned.MunicipalityId; incident.WardId = assigned.WardId;
                incident.LocationProvenance = "Resident voluntarily shared WhatsApp report location; unverified";
                incident.PrivacyConsent = PrivacyConsent.Capture(prepared.Request, time.GetUtcNow()); incident.NeedsHumanReview = true;
                incident.Audit.Add(new("channels.location.shared", "Verified " + message.Provider,
                    "Volunteered report point geofenced on an unapproved, unassigned draft; human approval required; no location in audit.", time.GetUtcNow()));
            }
            incident.Audit.Add(new("channels.intake." + prepared.MessageKey, "Verified " + message.Provider,
                "Normalized " + message.Channel + " intake linked to conversation; caller identity unverified; human approval required; no recording retained.", time.GetUtcNow()));
            if (message.Channel == "voice") incident.TranscriptSummary = PiiRedactor.Redact(prepared.Request!.Description);
            return null;
        }, ct);
    }
    public async Task<IReadOnlyList<ChannelConversationView>> ListAsync(string actor, CancellationToken ct)
    {
        foreach (var incident in (await incidents.ListAsync(ct)).Where(incident => incident.CreatedAt >= time.GetUtcNow().AddDays(-options.RetentionDays)))
            await journal.ImportLegacyAsync(incident, ct);
        return await journal.ListAsync(actor, ct);
    }
    public async Task<ChannelReplyResult> ReplyAsync(string id, ChannelReplyRequest reply, string actor, CancellationToken ct)
    {
        if (options.Mode == "disabled" || options.Mode == "configured" && !options.SendsEnabled)
            throw new ChannelRequestException(503, "External sends are disabled. Owner must explicitly enable configured transport.");
        await _gate.WaitAsync(ct);
        try
        {
            var prepared = await journal.PrepareReplyAsync(id, reply, actor, ct);
            if (prepared.Message is null) return prepared.Result;
            if (options.Mode == "simulated") return await journal.CompleteReplyAsync(prepared, "simulated", actor, ct);
            string outcome;
            try
            {
                using var request = ChannelAdapters.CreateOutbound(prepared.Message.Provider, configuration, options.DemoMode)
                    .BuildRequest(prepared.Message, time.GetUtcNow());
                // One explicit human-approved attempt. Ambiguous outcomes are never automatically retried.
                using var client = http.CreateClient("civicops-channels");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                outcome = response.IsSuccessStatusCode ? "accepted-unverified" : "provider-rejected";
            }
            catch (ChannelRequestException)
            { await journal.CompleteReplyAsync(prepared, "configuration-blocked", actor, CancellationToken.None); throw; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
            { outcome = "reconciliation-required"; }
            return await journal.CompleteReplyAsync(prepared, outcome, actor, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }
    public ChannelReadiness Readiness()
    {
        var items = new List<ChannelReadinessItem>();
        foreach (var pair in ChannelOptions.Providers)
        {
            var mode = options.Mode == "simulated" && options.DemoMode ? "simulated" : options.CanReceiveNative ? "configured" : "disabled";
            if (mode == "configured" && !NativeCredentials(pair.Key)) mode = "disabled";
            items.Add(new(pair.Value, pair.Key, mode, mode switch
            {
                "simulated" => "EXPLICIT DEMO / SYNTHETIC: signed fixtures and human-approved simulated outbox; no provider traffic.",
                "configured" => "Native verification plus authenticated signed relay configured; unverified live. " + OutboundReadiness(pair.Key),
                _ => "Disabled: requires owner-managed encryption, provider authentication and a tenant-bound signed relay."
            }));
        }
        return new(items, options.DemoMode, options.SendsEnabled
            && items.Any(item => item.Mode == "configured" && OutboundCredentials(item.Provider)));
    }
    private string OutboundReadiness(string provider)
    {
        if (!options.SendsEnabled) return "Outbound disabled by municipal policy.";
        if (provider is "twilio-voice" or "sip") return "Outbound disabled: this adapter has no outbound transport; callback/PBX acceptance is owner-pending.";
        if (!OutboundCredentials(provider)) return "Outbound disabled: required transport credentials are incomplete.";
        return "Outbound explicitly enabled for named human actions; provider acceptance/delivery remain unverified.";
    }
    private bool OutboundCredentials(string provider) => provider switch
    {
        "twilio-sms" => Has("CIVICOPS_TWILIO_AUTH_TOKEN") && Present("CIVICOPS_TWILIO_ACCOUNT_SID") && Present("CIVICOPS_TWILIO_FROM_NUMBER"),
        "clickatell" => Has("CIVICOPS_CLICKATELL_API_KEY") && Present("CIVICOPS_CLICKATELL_FROM_NUMBER"),
        "whatsapp" => Has("CIVICOPS_WHATSAPP_ACCESS_TOKEN") && Present("CIVICOPS_WHATSAPP_PHONE_NUMBER_ID"),
        "elevenlabs" => Has("ELEVENLABS_API_KEY") && Present("ELEVENLABS_AGENT_PHONE_NUMBER_ID")
            && (Present("ELEVENLABS_CALLBACK_AGENT_ID") || Present("ELEVENLABS_AGENT_ID")),
        _ => false
    };
    private bool NativeCredentials(string provider) => provider switch
    {
        "twilio-sms" or "twilio-voice" => Has("CIVICOPS_TWILIO_AUTH_TOKEN") && !string.IsNullOrWhiteSpace(configuration["CIVICOPS_TWILIO_ACCOUNT_SID"]) && !string.IsNullOrWhiteSpace(configuration["CIVICOPS_TWILIO_FROM_NUMBER"]),
        "clickatell" => Has("CIVICOPS_CLICKATELL_CALLBACK_SECRET") && !string.IsNullOrWhiteSpace(configuration["CIVICOPS_CLICKATELL_FROM_NUMBER"]),
        "whatsapp" => Has("CIVICOPS_WHATSAPP_APP_SECRET") && !string.IsNullOrWhiteSpace(configuration["CIVICOPS_WHATSAPP_PHONE_NUMBER_ID"]),
        "elevenlabs" => Has("ELEVENLABS_WEBHOOK_SECRET"),
        "sip" => Has("CIVICOPS_SIP_CALLBACK_SECRET"),
        _ => false
    };
    private bool Has(string name) => !string.IsNullOrWhiteSpace(SecretValues.Read(configuration, name));
    private bool Present(string name) => !string.IsNullOrWhiteSpace(configuration[name]);
    public void Dispose() => _gate.Dispose();
}

public sealed class ChannelRetentionWorker(TenantStore tenants, ChannelStore journal, TimeProvider time,
    ILogger<ChannelRetentionWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> PurgeUnavailable = LoggerMessage.Define(LogLevel.Warning,
        new EventId(4701, "ChannelRetentionUnavailable"), "Channel retention is unavailable for a municipality; check storage.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            IReadOnlyList<Tenant> municipalities;
            try { municipalities = await tenants.ListAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or Npgsql.NpgsqlException or TimeoutException or OperationCanceledException)
            { PurgeUnavailable(logger, null); continue; }
            foreach (var tenant in municipalities)
            {
                try { using var lease = TenantContext.Use(tenant.Id); await journal.PurgeAsync("CivicOps automatic retention", stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is IOException or InvalidOperationException or JsonException or Npgsql.NpgsqlException or TimeoutException or OperationCanceledException)
                { PurgeUnavailable(logger, null); }
            }
        }
    }
}
