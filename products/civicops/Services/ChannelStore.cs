using System.Text.Json;
using System.Security.Cryptography;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed record PreparedChannelIntake(string MessageKey, string ConversationId, IncidentRequest? Request,
    ChannelIntakeResult? Result, bool Replayed, string? ExistingCaseReference = null);
public sealed record PreparedChannelReply(string ConversationId, string OperationKey, OutboundMessage? Message,
    ChannelReplyResult Result);

/// <summary>Tenant-bound, integrity-checked conversation, consent and delivery journal.</summary>
public sealed class ChannelStore(IConfiguration configuration, IWebHostEnvironment environment, DurableStorage storage,
    IncidentStore incidents, TimeProvider time, CaseEventStream events, ChannelOptions options) : IDisposable
{
    private static readonly string[] MetricChannels = ["sms", "whatsapp", "voice", "web", "open311"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string StatePath => Path.Combine(TenantContext.DirectoryFor(DataPaths.Resolve(configuration, environment)), "channels.json");
    private string KeyPath => Path.Combine(DataPaths.Resolve(configuration, environment), "synthetic-channel-key.json");
    private sealed class Conversation
    {
        public string Id { get; set; } = "";
        public string Channel { get; set; } = "";
        public string Provider { get; set; } = "";
        public string NumberHash { get; set; } = "";
        public string EncryptedFrom { get; set; } = "";
        public string EncryptedTo { get; set; } = "";
        public string ResidentLabel { get; set; } = "";
        public string Language { get; set; } = "en";
        public int Version { get; set; } = 1;
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset LastResidentMessageAt { get; set; }
        public bool Suppressed { get; set; }
        public bool ConsentAccepted { get; set; }
        public bool NoticeDelivered { get; set; }
        public bool HumanQueue { get; set; }
        public bool CallbackRequested { get; set; }
        public IncidentRequest? PendingReport { get; set; }
        public DateTimeOffset? PendingReportAt { get; set; }
        public List<string> CaseReferences { get; set; } = [];
        public List<ChannelMessageView> Messages { get; set; } = [];
    }
    private sealed record Receipt(string ConversationId, string BodyHash, DateTimeOffset At,
        IncidentRequest? Request, ChannelIntakeResult? Result, string? ExistingCaseReference = null);
    private sealed record SendReceipt(string ConversationId, string BodyHash, string MessageId,
        string Mode, string Text, DateTimeOffset At, string TemplateKey);
    private sealed class Totals
    {
        public long Inbound { get; set; }
        public long CasesCreated { get; set; }
        public long Outbound { get; set; }
    }
    private sealed record Snapshot(string TenantId, List<Conversation> Conversations, List<ChannelTemplate> Templates,
        Dictionary<string, Receipt> Receipts, Dictionary<string, DateTimeOffset> Nonces, HashSet<string> GlobalSuppression,
        Dictionary<string, SendReceipt> Sends, Dictionary<string, Totals> Totals, List<EnterpriseAuditRecord> Audit,
        HashSet<string> ChannelSuppression, string? ContactKeyId = null);
    private static Snapshot Empty() => new(TenantContext.CurrentId, [], ChannelTemplates.Defaults(), new(StringComparer.Ordinal),
        new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), [], new(StringComparer.Ordinal));
    private async Task<byte[]> KeyAsync(CancellationToken ct)
    {
        if (!options.DemoMode && options.EncryptionKey is not null) return Convert.FromBase64String(options.EncryptionKey);
        if (!options.DemoMode) throw new ChannelRequestException(503, "Channel contact encryption requires an owner-managed key.");
        var persisted = await storage.ReadAsync(KeyPath, ct);
        if (persisted is not null) return Convert.FromBase64String(JsonSerializer.Deserialize<string>(persisted, Json)!);
        var key = RandomNumberGenerator.GetBytes(32);
        await storage.WriteAsync(KeyPath, JsonSerializer.Serialize(Convert.ToBase64String(key), Json), ct);
        return key;
    }
    private async Task<T> WithAsync<T>(Func<Snapshot, Task<T>> action, CancellationToken ct)
    {
        await using var transaction = storage.Begin(); await transaction.InitializeAsync(ct); await _gate.WaitAsync(ct);
        try { return await action(await LoadAsync(ct)); }
        catch { transaction.Abort(); throw; }
        finally { _gate.Release(); }
    }
    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        var persisted = await storage.ReadAsync(StatePath, ct);
        if (persisted is null) return Empty();
        var snapshot = JsonSerializer.Deserialize<Snapshot>(persisted, Json) ?? throw new InvalidDataException("Invalid channel state.");
        if (snapshot.TenantId != TenantContext.CurrentId) throw new InvalidDataException("Channel municipality mismatch.");
        var previous = AuditLog.Genesis;
        for (var index = 0; index < snapshot.Audit.Count; index++)
        {
            var record = snapshot.Audit[index];
            if (record.TenantId != snapshot.TenantId || record.Sequence != index + 1L || record.PreviousHash != previous
                || record.Hash != Hash(record with { Hash = "" })) throw new InvalidDataException("Channel audit integrity failed.");
            previous = record.Hash;
        }
        if (snapshot.Audit.Count == 0 || snapshot.Audit[^1].StateHash != StateHash(snapshot))
            throw new InvalidDataException("Channel state differs from its audit.");
        if (snapshot.ContactKeyId is not null)
        {
            byte[] key;
            if (options.DemoMode)
            {
                var keyDocument = await storage.ReadAsync(KeyPath, ct) ?? throw new InvalidDataException("Synthetic channel key is missing.");
                try
                {
                    var encoded = JsonSerializer.Deserialize<string>(keyDocument, Json);
                    if (encoded is null) throw new FormatException();
                    key = Convert.FromBase64String(encoded);
                    if (key.Length != 32) throw new FormatException();
                }
                catch (Exception error) when (error is JsonException or FormatException)
                { throw new InvalidDataException("Synthetic channel key is invalid; restore the original key before processing.", error); }
            }
            else key = options.EncryptionKey is not null ? Convert.FromBase64String(options.EncryptionKey)
                : throw new InvalidDataException("The owner channel key is unavailable.");
            if (snapshot.ContactKeyId != ContactKeyId(key, snapshot.TenantId))
                throw new InvalidDataException("Channel contact key changed; deliberate migration is required before intake or sends.");
        }
        // Recover a file-mode interruption between journal and main audit writes.
        var mirrored = (await incidents.AuditAsync(ct)).Where(entry => entry.Detail.Contains("identity-audit:", StringComparison.Ordinal))
            .Select(entry => entry.Detail[^64..]).ToHashSet(StringComparer.Ordinal);
        foreach (var record in snapshot.Audit.Where(record => !mirrored.Contains(record.Hash))) await EnterpriseAudit.MirrorAsync(incidents, record, ct);
        return snapshot;
    }
    private async Task SaveAsync(Snapshot snapshot, string action, string actor, string detail, CancellationToken ct)
    {
        var record = new EnterpriseAuditRecord(snapshot.TenantId, snapshot.Audit.Count + 1L, action,
            PiiRedactor.Redact(actor), PiiRedactor.Redact(detail), time.GetUtcNow(),
            snapshot.Audit.LastOrDefault()?.Hash ?? AuditLog.Genesis, StateHash(snapshot), "");
        record = record with { Hash = Hash(record) }; snapshot.Audit.Add(record);
        await storage.WriteAsync(StatePath, JsonSerializer.Serialize(snapshot, Json), ct);
        await EnterpriseAudit.MirrorAsync(incidents, record, ct); events.Publish();
    }
    private static string Hash<T>(T value) => IncidentStore.Hash(JsonSerializer.Serialize(value, Json));
    private static string StateHash(Snapshot snapshot) => Hash(snapshot with { Audit = [] });
    private static Totals Count(Snapshot snapshot, string channel)
    {
        if (!snapshot.Totals.TryGetValue(channel, out var totals)) snapshot.Totals[channel] = totals = new();
        return totals;
    }
    public static void Validate(InboundMessage message)
    {
        if (message is null || message.Provider is null || message.Channel is null || message.From is null
            || message.To is null || message.Text is null || message.LanguageGuess is null || message.ProviderMessageId is null)
            throw new ChannelRequestException(400, "Required normalized channel fields must be present and non-null.");
        if (!ChannelOptions.Providers.TryGetValue(message.Provider, out var channel) || channel != message.Channel)
            throw new ChannelRequestException(400, "Provider and channel must match the reviewed adapter list.");
        _ = ChannelContactCrypto.Normalize(message.From);
        foreach (var (value, name, maximum, minimum) in new[] {
            (message.To, "to", 200, 1), (message.Text, "text", 4000, 0),
            (message.LanguageGuess, "languageGuess", 12, 0),
            (message.ProviderMessageId, "providerMessageId", 200, 1), (message.Area, "area", 200, 0),
            (message.TranscriptSummary, "transcriptSummary", 4000, 0), (message.ProviderConversationId, "providerConversationId", 200, 0) })
            if (RequestValidation.Text(value, name, maximum, minimum) is { } error) throw new ChannelRequestException(400, error);
        if (message.Location is not null && (!message.LocationShared || !GeoGeofencing.IsValidPoint(message.Location)))
            throw new ChannelRequestException(400, "Location must be explicitly volunteered for this report and within the supported envelope.");
        if (message.LocationShared && message.Location is null) throw new ChannelRequestException(400, "Shared location is missing coordinates.");
        if (message.MediaReferences is { Count: > 10 } || message.MediaReferences?.Any(reference =>
            reference is null || reference.Length is < 1 or > 200 || !reference.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')) is true)
            throw new ChannelRequestException(400, "Only bounded provider media IDs are accepted; URLs and media bytes are excluded.");
        if (message.PrivacyConsentAccepted is true && message.PrivacyConsentVersion != PrivacyConsent.Version)
            throw new ChannelRequestException(400, "The current processing notice version is required.");
    }
    private static void ClaimNonce(Snapshot snapshot, string provider, string nonce, DateTimeOffset now)
    {
        Text(nonce, "nonce", 128, 16);
        var key = IncidentStore.Hash(provider + ":" + nonce);
        if (snapshot.Nonces.ContainsKey(key)) throw new ChannelRequestException(401, "Webhook nonce has already been used.");
        foreach (var prior in snapshot.Nonces.Where(pair => pair.Value < now.AddMinutes(-6)).Select(pair => pair.Key).ToArray()) snapshot.Nonces.Remove(prior);
        if (snapshot.Nonces.Count >= 10000) throw new ChannelRequestException(429, "Channel replay budget exhausted.");
        snapshot.Nonces[key] = now;
    }
    public Task ClaimEnvelopeAsync(string provider, string nonce, CancellationToken ct) => WithAsync(async snapshot =>
    {
        ClaimNonce(snapshot, provider, nonce, time.GetUtcNow());
        await SaveAsync(snapshot, "channels.webhook.verified", "Verified " + provider,
            "Signature and bounded fresh ingress nonce accepted; no body or address retained.", ct);
        return true;
    }, ct);
    public Task<bool> IsSuppressedAsync(string contact, string channel, CancellationToken ct) => WithAsync(async snapshot =>
    {
        if (snapshot.GlobalSuppression.Count == 0 && snapshot.ChannelSuppression.Count == 0) return false;
        var key = await KeyAsync(ct);
        var numberHash = ChannelContactCrypto.Hash(key, ChannelContactCrypto.Normalize(contact), snapshot.TenantId);
        return snapshot.GlobalSuppression.Contains(numberHash) || snapshot.ChannelSuppression.Contains(channel + ":" + numberHash);
    }, ct);
    public Task<PreparedChannelIntake> PrepareAsync(InboundMessage message, string bodyHash, string nonce,
        bool simulated, CancellationToken ct, bool nonceAlreadyClaimed = false) => WithAsync<PreparedChannelIntake>(async snapshot =>
    {
        Validate(message); var now = time.GetUtcNow();
        if (message.ReceivedAt > now.AddMinutes(1) || message.ReceivedAt < now.AddDays(-30))
            throw new ChannelRequestException(400, "Provider received-at is outside the bounded retention window.");
        if (!nonceAlreadyClaimed) ClaimNonce(snapshot, message.Provider, nonce, now);
        var encryptionKey = await KeyAsync(ct);
        snapshot = snapshot with { ContactKeyId = ContactKeyId(encryptionKey, snapshot.TenantId) };
        bodyHash = Convert.ToHexString(HMACSHA256.HashData(encryptionKey, System.Text.Encoding.UTF8.GetBytes(snapshot.TenantId + ":" + bodyHash)));
        var messageKey = IncidentStore.Hash(message.Provider + ":" + message.ProviderMessageId);
        if (snapshot.Receipts.TryGetValue(messageKey, out var prior))
        {
            if (prior.BodyHash != bodyHash) throw new ChannelRequestException(409, "Provider message ID belongs to different content.");
            if (prior.Request is not null && prior.At < now.AddDays(-options.RetentionDays))
            {
                prior = prior with { Request = null, Result = new(true, true, prior.ConversationId, null, "expired",
                    "Expired processing draft purged; please submit a new report.", simulated) };
                snapshot.Receipts[messageKey] = prior;
            }
            await SaveAsync(snapshot, "channels.webhook.retry", "Verified " + message.Provider,
                "Identical provider message retried with a fresh signed nonce; no new case or message.", ct);
            return new(messageKey, prior.ConversationId, prior.Request, prior.Result is null ? null : prior.Result with { Replayed = true }, true, prior.ExistingCaseReference);
        }
        var number = ChannelContactCrypto.Normalize(message.From);
        var numberHash = ChannelContactCrypto.Hash(encryptionKey, number, snapshot.TenantId);
        var id = "conv-" + IncidentStore.Hash(message.Channel + ":" + numberHash)[..32].ToLowerInvariant();
        var conversation = snapshot.Conversations.SingleOrDefault(item => item.Id == id);
        if (conversation is null)
        {
            if (snapshot.Conversations.Count >= 5000) throw new ChannelRequestException(429, "Conversation capacity requires municipal retention review.");
            conversation = new Conversation { Id = id, Channel = message.Channel, Provider = message.Provider, NumberHash = numberHash,
                EncryptedFrom = ChannelContactCrypto.Protect(encryptionKey, number, snapshot.TenantId),
                EncryptedTo = ChannelContactCrypto.Protect(encryptionKey, message.To, snapshot.TenantId),
                ResidentLabel = ChannelContactCrypto.Label(number), UpdatedAt = now, LastResidentMessageAt = message.ReceivedAt };
            snapshot.Conversations.Add(conversation);
        }
        if (conversation.Messages.Count >= 1000) throw new ChannelRequestException(429, "Conversation timeline is full; apply retention before accepting more.");
        conversation.Provider = message.Provider; conversation.Language = ChannelCommands.Language(message.LanguageGuess, message.Text);
        conversation.UpdatedAt = now; conversation.LastResidentMessageAt = message.ReceivedAt > conversation.LastResidentMessageAt ? message.ReceivedAt : conversation.LastResidentMessageAt;
        conversation.Version++;
        var text = PiiRedactor.Redact(message.TranscriptSummary ?? message.Text);
        var firstContact = conversation.Messages.Count == 0;
        conversation.Messages.Add(new(messageKey, "inbound", text, message.ReceivedAt, null, simulated, MediaReferences: message.MediaReferences?.ToArray()));
        Count(snapshot, message.Channel).Inbound++;
        conversation.HumanQueue |= message.HumanHandoffRequested || message.CallbackRequested;
        conversation.CallbackRequested |= message.CallbackRequested;
        if (message.PrivacyConsentAccepted is true) conversation.ConsentAccepted = true;
        var command = ChannelCommands.Canonical(message.Text);
        string? reply = null; IncidentRequest? request = null; string? reference = null; string? existingCaseReference = null;
        if (command is "STOP" or "STOPALL")
        {
            conversation.Suppressed = true; snapshot.ChannelSuppression.Add(message.Channel + ":" + numberHash);
            if (command == "STOPALL")
            {
                snapshot.GlobalSuppression.Add(numberHash);
                // Keep unopened channels suppressed when START removes the number-wide barrier.
                foreach (var otherChannel in MetricChannels) snapshot.ChannelSuppression.Add(otherChannel + ":" + numberHash);
                foreach (var other in snapshot.Conversations.Where(item => item.NumberHash == numberHash)) { other.Suppressed = true; snapshot.ChannelSuppression.Add(other.Channel + ":" + numberHash); other.Version++; }
            }
            reply = "Messaging stopped. START re-subscribes this channel. No external message was sent.";
        }
        else if (command == "START")
        {
            snapshot.GlobalSuppression.Remove(numberHash); snapshot.ChannelSuppression.Remove(message.Channel + ":" + numberHash); conversation.Suppressed = false;
            reply = "This channel is re-subscribed. " + ChannelCommands.Help(conversation.Language);
        }
        else if (command == "HELP") reply = ChannelCommands.Help(conversation.Language);
        else if (command == "SOS")
        {
            conversation.HumanQueue = true;
            reply = message.Channel is "sms" or "whatsapp"
                ? "SOS recorded for the municipal control room. A named human must assess it. CivicOps does not alert responders and does not call 10111, 10177 or 112."
                : "SOS INFORMATION — CivicOps does not alert responders. Emergency numbers: SAPS 10111; ambulance/fire 10177; mobile emergency 112. Contact emergency services directly. No control-room SOS was created from this voice menu.";
        }
        else if (command is "HUMAN" or "CALLBACK" || message.HumanHandoffRequested && command == "REPORT")
        {
            conversation.HumanQueue = true; conversation.CallbackRequested |= command == "CALLBACK";
            reply = conversation.CallbackRequested ? "Callback request queued for a named human. No call placed; no arrival time promised." : "A human call-taker review is queued; no dispatch action taken.";
        }
        else if (command == "STATUS") reply = "Reply STATUS <reference> for a case linked to this conversation.";
        else if (command.StartsWith("STATUS ", StringComparison.Ordinal))
        {
            var requested = message.Text.Trim()[7..].Trim();
            var found = conversation.CaseReferences.Contains(requested, StringComparer.OrdinalIgnoreCase) ? await incidents.GetAsync(requested, ct) : null;
            reference = found?.ReferenceNumber;
            reply = found is null ? "No linked case with that reference. Ask the municipality for assistance." : found.ReferenceNumber + ": " + IncidentView.SafeUpdate(found.Status, conversation.Language);
        }
        else if (command == "REPORT") reply = "Describe the municipal problem and area. Location is optional. " + NoticeDraft(snapshot, conversation.Language);
        else if (command == "YES")
        {
            if (!conversation.NoticeDelivered && !conversation.ConsentAccepted)
            {
                conversation.HumanQueue = true; reply = "A human must provide the current notice before processing consent can be recorded. " + NoticeDraft(snapshot, conversation.Language);
            }
            else
            {
                conversation.ConsentAccepted = true;
                request = conversation.PendingReportAt >= now.AddDays(-options.RetentionDays) ? conversation.PendingReport : null;
                if (request is null) { conversation.PendingReport = null; conversation.PendingReportAt = null; }
                reply = request is null ? "Processing consent recorded. Describe the municipal problem and area; location is optional." : null;
            }
        }
        else
        {
            var description = text;
            if (message.Text.Trim().StartsWith("REPORT ", StringComparison.OrdinalIgnoreCase)) description = PiiRedactor.Redact(message.Text.Trim()[7..]);
            if (description.Length < 3 && message.MediaReferences is { Count: > 0 }) { conversation.HumanQueue = true; reply = "Media is not fetched or stored. Please describe the problem for a human reviewer."; }
            else
            {
                var candidate = new IncidentRequest(description, message.Area ?? "Needs area confirmation", Source: message.Channel,
                    SourceLabel: (simulated ? "SIMULATED " : "Verified inbound ") + message.Provider,
                    ResidentLanguage: conversation.Language, ConversationId: id, PrivacyConsentAccepted: true,
                    PrivacyConsentVersion: PrivacyConsent.Version,
                    Location: conversation.ConsentAccepted ? message.Location : null,
                    ShareLocation: conversation.ConsentAccepted && message.LocationShared);
                if (conversation.ConsentAccepted)
                {
                    request = candidate;
                    if (message.Location is not null && message.Text == "Resident shared a report location. Human review requires the report description."
                        && conversation.CaseReferences.Count > 0) existingCaseReference = conversation.CaseReferences[^1];
                }
                else { conversation.PendingReport = candidate; conversation.PendingReportAt = now; conversation.HumanQueue = true; reply = NoticeDraft(snapshot, conversation.Language); }
            }
        }
        if (firstContact)
        {
            var notice = Latest(snapshot, "channel.notice", conversation.Language);
            conversation.Messages.Add(new("notice-" + messageKey, "draft", NoticeDraft(snapshot, conversation.Language),
                now, null, simulated, notice.Key, notice.Version, "CivicOps notice draft", "awaiting human send"));
        }
        var knownCommand = command is "STOP" or "STOPALL" or "START" or "HELP" or "SOS" or "HUMAN" or "CALLBACK" or "YES" or "REPORT" or "STATUS"
            || command.StartsWith("STATUS ", StringComparison.Ordinal) ? command.Split(' ')[0] : null;
        var result = request is null ? new ChannelIntakeResult(true, false, id, reference, knownCommand, reply, simulated) : null;
        if (reply is not null) conversation.Messages.Add(new("draft-" + messageKey, "draft", reply, now, reference, simulated,
            Actor: "CivicOps command draft", DeliveryState: "awaiting human review"));
        snapshot.Receipts[messageKey] = new(id, bodyHash, now, request, result, existingCaseReference);
        await SaveAsync(snapshot, command is "STOP" or "STOPALL" or "START" ? "channels.consent.changed" : "channels.inbound",
            "Verified " + message.Provider, "Normalized channel intake and command state recorded; contact encrypted; no dispatch or external send.", ct);
        return new(messageKey, id, request, result, false, existingCaseReference);
    }, ct);
    public Task<ChannelIntakeResult> CompleteAsync(PreparedChannelIntake prepared, Incident incident, bool simulated,
        CancellationToken ct) => WithAsync(async snapshot =>
    {
        var receipt = snapshot.Receipts[prepared.MessageKey];
        if (receipt.Result is not null) return receipt.Result with { Replayed = true };
        var conversation = snapshot.Conversations.Single(item => item.Id == prepared.ConversationId);
        if (!conversation.CaseReferences.Contains(incident.ReferenceNumber, StringComparer.Ordinal))
        { conversation.CaseReferences.Add(incident.ReferenceNumber); Count(snapshot, conversation.Channel).CasesCreated++; }
        conversation.PendingReport = null; conversation.PendingReportAt = null;
        var index = conversation.Messages.FindIndex(item => item.Id == prepared.MessageKey);
        if (index >= 0) conversation.Messages[index] = conversation.Messages[index] with { CaseReference = incident.ReferenceNumber };
        var template = Latest(snapshot, "case.received", conversation.Language);
        var reply = ChannelTemplates.Render(template, DraftVariables(conversation.Language, incident.ReferenceNumber));
        conversation.Messages.Add(new("received-" + prepared.MessageKey, "draft", reply, time.GetUtcNow(), incident.ReferenceNumber,
            simulated, template.Key, template.Version, "CivicOps receipt draft", "awaiting human send"));
        conversation.Version++; conversation.UpdatedAt = time.GetUtcNow();
        var result = new ChannelIntakeResult(true, prepared.Replayed, conversation.Id, incident.ReferenceNumber, null, reply, simulated);
        snapshot.Receipts[prepared.MessageKey] = receipt with { Request = null, Result = result };
        await SaveAsync(snapshot, "channels.case.linked", "CivicOps channel pipeline",
            incident.ReferenceNumber + " linked to normalized intake; case remains subject to human approval.", ct);
        return result;
    }, ct);
    private ChannelConversationView View(Snapshot snapshot, Conversation conversation, bool messages)
    {
        var cutoff = time.GetUtcNow().AddDays(-options.RetentionDays);
        return new(conversation.Id, conversation.Channel, conversation.Provider, conversation.ResidentLabel, conversation.Language,
            conversation.UpdatedAt, conversation.LastResidentMessageAt, conversation.Suppressed || snapshot.ChannelSuppression.Contains(conversation.Channel + ":" + conversation.NumberHash) || snapshot.GlobalSuppression.Contains(conversation.NumberHash),
            conversation.ConsentAccepted, conversation.CaseReferences.ToArray(), conversation.HumanQueue, conversation.CallbackRequested,
            messages ? conversation.Messages.Where(item => item.At >= cutoff).ToArray() : [], conversation.Version);
    }
    public Task<IReadOnlyList<ChannelConversationView>> ListAsync(string actor, CancellationToken ct) => WithAsync(async snapshot =>
    {
        var result = snapshot.Conversations.Where(item => item.UpdatedAt >= time.GetUtcNow().AddDays(-options.RetentionDays))
            .OrderByDescending(item => item.UpdatedAt).Take(500).Select(item => View(snapshot, item, false)).ToArray();
        await SaveAsync(snapshot, "channels.access", actor, "Inbox list accessed; count=" + result.Length + "; no contact or message content in audit.", ct);
        return (IReadOnlyList<ChannelConversationView>)result;
    }, ct);
    public Task<ChannelConversationView?> GetAsync(string id, string actor, CancellationToken ct) => WithAsync(async snapshot =>
    {
        var conversation = snapshot.Conversations.SingleOrDefault(item => item.Id == id && item.UpdatedAt >= time.GetUtcNow().AddDays(-options.RetentionDays));
        await SaveAsync(snapshot, "channels.access", actor, "Conversation timeline accessed; found=" + (conversation is not null) + "; no resident content in audit.", ct);
        return conversation is null ? null : View(snapshot, conversation, true);
    }, ct);
    private static ChannelTemplate Latest(Snapshot snapshot, string key, string language) =>
        snapshot.Templates.Where(item => item.Key == key && item.Language == language).OrderByDescending(item => item.Version).FirstOrDefault()
            ?? throw new ChannelRequestException(400, "Unknown template key or language.");
    private static string NoticeDraft(Snapshot snapshot, string language) =>
        ChannelTemplates.Render(Latest(snapshot, "channel.notice", language), DraftVariables(language));
    private static Dictionary<string, string> DraftVariables(string language, string? reference = null) => new()
    {
        ["reference"] = reference ?? (language switch { "af" => "Verwysing nog nie toegeken nie", "zu" => "Ireferensi ayikanikezwa", _ => "Reference not yet assigned" }),
        ["update"] = language switch { "af" => "Wag op menslike hersiening; geen aankomstyd word belowe nie.", "zu" => "Ilinde ukubuyekezwa umuntu; asithembisi isikhathi sokufika.", _ => "Awaiting human review; no arrival time is promised." },
        ["request"] = language switch { "af" => "'n beskrywing van die diensprobleem vanaf 'n veilige afstand", "zu" => "incazelo yenkinga yesevisi usendaweni ephephile", _ => "a description of the service issue from a safe distance" }
    };
    public Task<IReadOnlyList<ChannelTemplate>> TemplatesAsync(CancellationToken ct) => WithAsync(snapshot => Task.FromResult<IReadOnlyList<ChannelTemplate>>(
        snapshot.Templates.GroupBy(item => (item.Key, item.Language)).Select(group => group.MaxBy(item => item.Version)!).OrderBy(item => item.Key).ThenBy(item => item.Language).ToArray()), ct);
    public Task<ChannelTemplate> EditTemplateAsync(string key, string language, ChannelTemplateEdit edit, string actor, CancellationToken ct) => WithAsync(async snapshot =>
    {
        if (edit is null || edit.Text is null) throw new ChannelRequestException(400, "A non-null template edit is required.");
        ChannelTemplates.Validate(edit.Text); Text(edit.Reason, "reason", 500, 5);
        if (edit.ProviderTemplateName is not null && (edit.ProviderTemplateName.Length is < 1 or > 128
            || !edit.ProviderTemplateName.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')))
            throw new ChannelRequestException(400, "Provider template names use lowercase letters, digits and underscores.");
        var prior = Latest(snapshot, key, language);
        if (prior.Version != edit.ExpectedVersion) throw new ChannelRequestException(409, "Template changed; refresh the current version.");
        if (key == "channel.notice" && !edit.Text.Contains(PrivacyConsent.Version, StringComparison.Ordinal))
            throw new ChannelRequestException(400, "The first-contact notice must retain the current processing version.");
        var next = prior with { Version = prior.Version + 1, Text = PiiRedactor.Redact(edit.Text), ProviderTemplateName = edit.ProviderTemplateName };
        if (key is "channel.notice" or "case.received")
            _ = ChannelTemplates.Render(next, DraftVariables(language, key == "case.received" ? "CIV-20261009-00000011" : null));
        snapshot.Templates.Add(next);
        await SaveAsync(snapshot, "channels.template.edited", actor, key + "/" + language + " v" + next.Version + "; " + PiiRedactor.Redact(edit.Reason), ct);
        return next;
    }, ct);
    public Task<ChannelTemplatePreviewResult> PreviewAsync(ChannelTemplatePreview preview, CancellationToken ct) => WithAsync(snapshot =>
    {
        if (preview is null || preview.Variables is null) throw new ChannelRequestException(400, "A template preview with non-null variables is required.");
        var template = Latest(snapshot, preview.Key, preview.Language);
        return Task.FromResult(new ChannelTemplatePreviewResult(ChannelTemplates.Render(template, preview.Variables), template.Version, template.ReviewState));
    }, ct);
    public Task<PreparedChannelReply> PrepareReplyAsync(string id, ChannelReplyRequest reply, string actor, CancellationToken ct) => WithAsync<PreparedChannelReply>(async snapshot =>
    {
        if (reply is null || reply.Variables is null) throw new ChannelRequestException(400, "A reviewed reply with non-null variables is required.");
        Text(actor, "actor", 200, 1); Text(reply.Reason, "reason", 500, 5); Text(reply.OperationId, "operationId", 128, 8);
        var key = IncidentStore.Hash(id + ":" + reply.OperationId); var bodyHash = Hash(reply);
        if (snapshot.Sends.TryGetValue(key, out var prior))
        {
            if (prior.BodyHash != bodyHash) throw new ChannelRequestException(409, "Send operation ID belongs to different content.");
            return new(id, key, null, new(prior.MessageId, prior.Mode, true, prior.Text));
        }
        var conversation = snapshot.Conversations.SingleOrDefault(item => item.Id == id && item.UpdatedAt >= time.GetUtcNow().AddDays(-options.RetentionDays))
            ?? throw new ChannelRequestException(404, "Conversation not found.");
        if (conversation.Suppressed || snapshot.ChannelSuppression.Contains(conversation.Channel + ":" + conversation.NumberHash) || snapshot.GlobalSuppression.Contains(conversation.NumberHash))
            throw new ChannelRequestException(409, "Outbound blocked: this resident is suppressed.");
        if (!conversation.ConsentAccepted && reply.TemplateKey != "channel.notice")
            throw new ChannelRequestException(409, "Processing consent is required before sending case messages.");
        if (conversation.Channel == "voice" && !conversation.CallbackRequested)
            throw new ChannelRequestException(409, "An explicit resident callback request is required before a voice send.");
        if (conversation.Channel == "voice" && options.Mode == "configured" && reply.Language == "zu")
            throw new ChannelRequestException(409, "Configured isiZulu callback voice requires owner/provider acceptance and is disabled.");
        if (conversation.Version != reply.ExpectedConversationVersion) throw new ChannelRequestException(409, "Conversation changed; refresh before sending.");
        var template = Latest(snapshot, reply.TemplateKey, reply.Language);
        if (template.Version != reply.ExpectedTemplateVersion) throw new ChannelRequestException(409, "Template changed; preview the current version before sending.");
        var text = ChannelTemplates.Render(template, reply.Variables);
        if (reply.TemplateKey != "channel.notice")
        {
            if (!reply.Variables.TryGetValue("reference", out var reference) || !conversation.CaseReferences.Contains(reference, StringComparer.Ordinal))
                throw new ChannelRequestException(409, "A linked case reference is required.");
            var incident = await incidents.GetAsync(reference, ct);
            if (incident is null) throw new ChannelRequestException(409, "The linked case is no longer available.");
            if (reply.TemplateKey == "case.resolved" && incident.Status is not (IncidentStatus.Resolved or IncidentStatus.Closed))
                throw new ChannelRequestException(409, "The case is not recorded as resolved.");
        }
        var encryptionKey = await KeyAsync(ct);
        var number = ChannelContactCrypto.Unprotect(encryptionKey, conversation.EncryptedFrom, snapshot.TenantId);
        if (!number.StartsWith('+')) throw new ChannelRequestException(409, "No addressable resident contact is available.");
        var sender = ChannelContactCrypto.Unprotect(encryptionKey, conversation.EncryptedTo, snapshot.TenantId);
        var messageId = "out-" + Guid.NewGuid().ToString("N"); var now = time.GetUtcNow();
        snapshot.Sends[key] = new(id, bodyHash, messageId, "pending", text, now, template.Key);
        conversation.Messages.Add(new(messageId, "outbound", text, now, reply.Variables.GetValueOrDefault("reference"),
            options.Mode == "simulated", template.Key, template.Version, actor, "pending"));
        conversation.Version++; conversation.UpdatedAt = now;
        await SaveAsync(snapshot, "channels.send.approved", actor, "Explicit human send intent recorded; template=" + template.Key
            + "; version=" + template.Version + "; " + PiiRedactor.Redact(reply.Reason), ct);
        return new(id, key, new(conversation.Channel, conversation.Provider, number, sender, text, reply.Language, template.Version,
            template.Key, ChannelTemplates.UsedVariables(template, reply.Variables), template.ProviderTemplateName, conversation.LastResidentMessageAt,
            Interactive: conversation.Channel == "whatsapp" && template.Key == "channel.notice" && template.ProviderTemplateName is null
                && conversation.LastResidentMessageAt > now.AddHours(-24) && conversation.LastResidentMessageAt <= now),
            new(messageId, "pending", false, text));
    }, ct);
    public Task<ChannelReplyResult> CompleteReplyAsync(PreparedChannelReply prepared, string mode, string actor, CancellationToken ct) => WithAsync<ChannelReplyResult>(async snapshot =>
    {
        var prior = snapshot.Sends[prepared.OperationKey];
        if (prior.Mode != "pending") return new(prior.MessageId, prior.Mode, true, prior.Text);
        snapshot.Sends[prepared.OperationKey] = prior with { Mode = mode };
        var conversation = snapshot.Conversations.Single(item => item.Id == prepared.ConversationId);
        var index = conversation.Messages.FindIndex(item => item.Id == prior.MessageId);
        conversation.Messages[index] = conversation.Messages[index] with { DeliveryState = mode, Simulated = mode == "simulated" };
        if (mode is "simulated" or "accepted-unverified")
        { Count(snapshot, conversation.Channel).Outbound++; if (prior.TemplateKey == "channel.notice") conversation.NoticeDelivered = true; }
        conversation.Version++; conversation.UpdatedAt = time.GetUtcNow();
        await SaveAsync(snapshot, "channels.send.receipt", actor, "Human approved delivery outcome=" + mode + "; no provider response body or contact retained.", ct);
        return new(prior.MessageId, mode, false, prior.Text);
    }, ct);
    public Task<IReadOnlyList<ChannelMetrics>> MetricsAsync(CancellationToken ct) => WithAsync(snapshot => Task.FromResult<IReadOnlyList<ChannelMetrics>>(
        MetricChannels.Select(channel =>
        {
            var totals = snapshot.Totals.GetValueOrDefault(channel) ?? new Totals();
            return new ChannelMetrics(channel, totals.Inbound, totals.CasesCreated, totals.Outbound,
                snapshot.Conversations.Count(item => item.Channel == channel && (item.Suppressed || snapshot.ChannelSuppression.Contains(item.Channel + ":" + item.NumberHash) || snapshot.GlobalSuppression.Contains(item.NumberHash))),
                snapshot.Conversations.Count(item => item.Channel == channel && item.HumanQueue));
        }).ToArray()), ct);
    public Task<ChannelPurgeResult> PurgeAsync(string actor, CancellationToken ct) => WithAsync<ChannelPurgeResult>(async snapshot =>
    {
        var cutoff = time.GetUtcNow().AddDays(-options.RetentionDays); var count = snapshot.Conversations.RemoveAll(item => item.UpdatedAt < cutoff);
        var messages = 0;
        foreach (var conversation in snapshot.Conversations)
        {
            messages += conversation.Messages.RemoveAll(item => item.At < cutoff);
            if (conversation.PendingReportAt < cutoff) { conversation.PendingReport = null; conversation.PendingReportAt = null; }
        }
        foreach (var key in snapshot.Receipts.Where(pair => pair.Value.At < cutoff && pair.Value.Request is not null).Select(pair => pair.Key).ToArray())
            snapshot.Receipts[key] = snapshot.Receipts[key] with { Request = null, Result = new(true, true, snapshot.Receipts[key].ConversationId, null, "expired", "Expired processing draft purged; ask a human before creating a new report.", options.DemoMode) };
        var receiptKeys = snapshot.Receipts.Where(pair => pair.Value.At < time.GetUtcNow().AddDays(-90)).Select(pair => pair.Key).ToArray();
        foreach (var key in receiptKeys) snapshot.Receipts.Remove(key);
        foreach (var key in snapshot.Sends.Where(pair => pair.Value.At < cutoff).Select(pair => pair.Key).ToArray())
        {
            var prior = snapshot.Sends[key];
            snapshot.Sends[key] = prior with { Text = "", Mode = prior.Mode == "pending" ? "expired-reconciliation-required" : "expired" };
        }
        foreach (var key in snapshot.Nonces.Where(pair => pair.Value < time.GetUtcNow().AddMinutes(-6)).Select(pair => pair.Key).ToArray()) snapshot.Nonces.Remove(key);
        await SaveAsync(snapshot, "channels.retention.purged", actor, "Expired conversations=" + count + "; messages=" + messages
            + "; provider receipts=" + receiptKeys.Length + "; value-free suppression hashes and audit retained.", ct);
        return new(count, receiptKeys.Length, messages, "Suppression hashes, template history, aggregate counters and value-free audit remain; backups/provider records require owner retention.");
    }, ct);
    public Task ImportLegacyAsync(Incident incident, CancellationToken ct) => WithAsync(async snapshot =>
    {
        if (incident.ConversationId?.StartsWith("conv-", StringComparison.Ordinal) is true) return false;
        if (snapshot.Receipts.ContainsKey("legacy-" + incident.Id)) return false;
        if (incident.Source is not ("web" or "chat" or "voice" or "open311")) return false;
        var channel = incident.Source is "web" or "chat" ? "web" : incident.Source;
        var contact = !string.IsNullOrWhiteSpace(incident.ContactPhone) ? incident.ContactPhone
            : incident.Source == "voice" && incident.ConversationId is not null ? "call:" + IncidentStore.Hash(incident.ConversationId) : "web:" + incident.Id;
        try { contact = ChannelContactCrypto.Normalize(contact); }
        catch (ChannelRequestException) { contact = "web:" + incident.Id; }
        var encryptionKey = await KeyAsync(ct); var number = ChannelContactCrypto.Normalize(contact);
        snapshot = snapshot with { ContactKeyId = ContactKeyId(encryptionKey, snapshot.TenantId) };
        var numberHash = ChannelContactCrypto.Hash(encryptionKey, number, snapshot.TenantId);
        var id = "conv-" + IncidentStore.Hash(channel + ":" + numberHash)[..32].ToLowerInvariant();
        var conversation = snapshot.Conversations.SingleOrDefault(item => item.Id == id);
        if (conversation is null)
        {
            conversation = new() { Id = id, Channel = channel, Provider = incident.Source is "chat" or "voice" ? "elevenlabs" : "civicops-web",
                NumberHash = numberHash, EncryptedFrom = ChannelContactCrypto.Protect(encryptionKey, number, snapshot.TenantId),
                EncryptedTo = ChannelContactCrypto.Protect(encryptionKey, "municipal-intake", snapshot.TenantId),
                ResidentLabel = ChannelContactCrypto.Label(number), Language = incident.ResidentLanguage,
                ConsentAccepted = incident.PrivacyConsent?.Accepted is true, CallbackRequested = incident.CallbackConsent && incident.ConsentToContact,
                UpdatedAt = incident.CreatedAt, LastResidentMessageAt = incident.CreatedAt };
            snapshot.Conversations.Add(conversation);
        }
        conversation.Messages.Add(new("legacy-" + incident.Id, "inbound", PiiRedactor.Redact(incident.TranscriptSummary ?? incident.Description),
            incident.CreatedAt, incident.ReferenceNumber, options.DemoMode));
        conversation.UpdatedAt = incident.CreatedAt > conversation.UpdatedAt ? incident.CreatedAt : conversation.UpdatedAt;
        conversation.LastResidentMessageAt = incident.CreatedAt > conversation.LastResidentMessageAt ? incident.CreatedAt : conversation.LastResidentMessageAt;
        conversation.CaseReferences.Add(incident.ReferenceNumber); conversation.Version++;
        snapshot.Receipts["legacy-" + incident.Id] = new(id, IncidentStore.Hash(incident.Id), time.GetUtcNow(), null,
            new(true, false, id, incident.ReferenceNumber, null, null, options.DemoMode));
        Count(snapshot, channel).Inbound++; Count(snapshot, channel).CasesCreated++;
        await SaveAsync(snapshot, "channels.legacy.linked", "CivicOps existing intake", incident.ReferenceNumber
            + " projected to inbox; unaddressable reports are not asserted to be a verified resident identity.", ct);
        return true;
    }, ct);
    public Task RecordIvrNoticeAsync(string id, CancellationToken ct) => WithAsync(async snapshot =>
    {
        var conversation = snapshot.Conversations.Single(item => item.Id == id);
        if (conversation.NoticeDelivered) return false;
        conversation.NoticeDelivered = true; conversation.Version++;
        await SaveAsync(snapshot, "channels.notice.provided", "Configured municipal IVR",
            "Current purpose notice provided in IVR response; resident must explicitly say YES; provider playback unverified.", ct);
        return true;
    }, ct);
    public static void Text(string? value, string name, int maximum, int minimum)
    { if (RequestValidation.Text(value, name, maximum, minimum) is { } error) throw new ChannelRequestException(400, error); }
    public void Dispose() => _gate.Dispose();
    private static string ContactKeyId(byte[] key, string tenant) => Convert.ToHexString(HMACSHA256.HashData(key,
        System.Text.Encoding.UTF8.GetBytes("civicops-channel-key:" + tenant)));
    public Task VerifyIntegrityAsync(CancellationToken ct) => WithAsync(snapshot => Task.FromResult(snapshot.Audit.Count), ct);
}
