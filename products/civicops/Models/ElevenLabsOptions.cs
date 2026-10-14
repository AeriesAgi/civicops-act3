using CivicOps.Services;

namespace CivicOps.Models;

public sealed class ElevenLabsOptions
{
    public string? ApiKey { get; init; }
    public string ApiBase { get; init; } = "https://api.elevenlabs.io";
    public string? AgentId { get; init; }
    public string? CallbackAgentId { get; init; }
    public string? PhoneNumberId { get; init; }
    public string? ToolSecret { get; init; }
    public string? WebhookSecret { get; init; }
    public string SessionMode { get; init; } = "public";
    public string PhoneProvider { get; init; } = "twilio";
    public bool DemoMode { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>CIVICOPS_DEMO_LIVE_PROVIDERS contains elevenlabs: the explicit demo may use the real resident agent.</summary>
    public bool LiveInDemo { get; init; }
    public bool Simulated => (DemoMode && (!LiveInDemo || string.IsNullOrWhiteSpace(AgentId))) || string.IsNullOrWhiteSpace(ApiKey);
    public bool CallbacksEnabled => !string.IsNullOrWhiteSpace(PhoneNumberId);

    public static ElevenLabsOptions From(IConfiguration c) => new()
    {
        ApiKey = SecretValues.Read(c, "ELEVENLABS_API_KEY"),
        ApiBase = string.IsNullOrWhiteSpace(c["ELEVENLABS_API_BASE"]) ? "https://api.elevenlabs.io" : c["ELEVENLABS_API_BASE"]!,
        AgentId = c["ELEVENLABS_AGENT_ID"], CallbackAgentId = c["ELEVENLABS_CALLBACK_AGENT_ID"],
        PhoneNumberId = c["ELEVENLABS_AGENT_PHONE_NUMBER_ID"],
        ToolSecret = SecretValues.Read(c, "CIVICOPS_ELEVENLABS_TOOL_SECRET"), WebhookSecret = SecretValues.Read(c, "ELEVENLABS_WEBHOOK_SECRET"),
        SessionMode = Option(c["ELEVENLABS_SESSION_MODE"], "public", ["public", "signed-url", "webrtc"], "ELEVENLABS_SESSION_MODE"),
        PhoneProvider = Option(c["ELEVENLABS_PHONE_PROVIDER"], "twilio", ["twilio", "sip-trunk"], "ELEVENLABS_PHONE_PROVIDER"),
        DemoMode = bool.TryParse(c["CIVICOPS_DEMO_MODE"], out var demo) && demo,
        LiveInDemo = CivicOps.Services.AiOptions.DemoLiveProviders(c).Contains("elevenlabs"),
        RequestTimeout = ProviderCircuit.TimeoutFrom(c)
    };

    private static string Option(string? value, string fallback, string[] permitted, string name)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        return permitted.Contains(normalized, StringComparer.Ordinal) ? normalized
            : throw new InvalidOperationException(name + " must be one of " + string.Join(", ", permitted) + ".");
    }
}
