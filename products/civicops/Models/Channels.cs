using System.Text.Json.Serialization;

namespace CivicOps.Models;

// Transient normalized intake. Contacts are hashed/encrypted by ChannelStore before persistence.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InboundMessage(string Channel, string Provider, string From, string To, string Text,
    string LanguageGuess, DateTimeOffset ReceivedAt, string ProviderMessageId,
    IReadOnlyList<string>? MediaReferences = null, string? ProviderConversationId = null,
    GeoPoint? Location = null, bool LocationShared = false, string? Area = null,
    string? TranscriptSummary = null, bool HumanHandoffRequested = false, bool CallbackRequested = false,
    bool? PrivacyConsentAccepted = null, string? PrivacyConsentVersion = null);
public sealed record ChannelWebhookRequest(string Provider, string Body, string ContentType, string PublicUrl,
    IReadOnlyDictionary<string, string> Headers);
public sealed record OutboundMessage(string Channel, string Provider, string To, string From, string Text,
    string Language, int TemplateVersion = 1, string? TemplateKey = null,
    IReadOnlyDictionary<string, string>? Variables = null, string? ProviderTemplateName = null,
    DateTimeOffset? LastResidentMessageAt = null, bool Interactive = false);
public interface IInboundChannel
{
    string Provider { get; }
    IReadOnlyList<InboundMessage> VerifyAndParse(ChannelWebhookRequest request, DateTimeOffset now);
}
public interface IOutboundChannel
{
    string Provider { get; }
    HttpRequestMessage BuildRequest(OutboundMessage message, DateTimeOffset now);
}
public sealed class ChannelRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
public sealed record ChannelMessageView(string Id, string Direction, string Text, DateTimeOffset At,
    string? CaseReference, bool Simulated, string? TemplateKey = null, int? TemplateVersion = null,
    string? Actor = null, string? DeliveryState = null, IReadOnlyList<string>? MediaReferences = null);
public sealed record ChannelConversationView(string Id, string Channel, string Provider, string ResidentLabel,
    string Language, DateTimeOffset UpdatedAt, DateTimeOffset LastResidentMessageAt, bool Suppressed,
    bool ConsentAccepted, IReadOnlyList<string> CaseReferences, bool HumanQueue, bool CallbackRequested,
    IReadOnlyList<ChannelMessageView> Messages, int Version);
public sealed record ChannelTemplate(string Key, string Language, int Version, string Text,
    string? ProviderTemplateName, string ReviewState);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChannelTemplateEdit(string Text, int ExpectedVersion, string Reason, string? ProviderTemplateName = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChannelTemplatePreview(string Key, string Language, Dictionary<string, string> Variables);
public sealed record ChannelTemplatePreviewResult(string Text, int Version, string ReviewState);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChannelReplyRequest(string TemplateKey, string Language, Dictionary<string, string> Variables,
    int ExpectedTemplateVersion, int ExpectedConversationVersion, string Reason, string OperationId);
public sealed record ChannelReplyResult(string MessageId, string Mode, bool Replayed, string Text);
public sealed record ChannelIntakeResult(bool Received, bool Replayed, string ConversationId,
    string? ReferenceNumber, string? Command, string? Reply, bool Simulated);
public sealed record ChannelReadinessItem(string Channel, string Provider, string Mode, string Detail);
public sealed record ChannelReadiness(IReadOnlyList<ChannelReadinessItem> Channels, bool DemoMode, bool ExternalSendsEnabled);
public sealed record ChannelMetrics(string Channel, long Inbound, long CasesCreated, long Outbound, int Suppressed, int HumanQueue);
public sealed record ChannelPurgeResult(int Conversations, int Receipts, int Messages, string Retained);
public sealed record ChannelWebhookResult(bool Received, IReadOnlyList<ChannelIntakeResult> Results);
public sealed record ChannelSeedResult(int Count, bool Simulated, bool Synthetic, IReadOnlyList<ChannelIntakeResult> Results);
