namespace CivicOps.Services;

public sealed record ReadinessItem(string Name, string State, string[] EnvironmentVariables, string[] Missing,
    string Note);

public static class IntegrationReadiness
{
    private static readonly string[] InboundRequired = ["CIVICOPS_ELEVENLABS_TOOL_SECRET", "ELEVENLABS_WEBHOOK_SECRET", "ELEVENLABS_POST_CALL_WEBHOOK_ID", "PUBLIC_BASE_URL"];
    public static object Describe(IConfiguration c, bool demo)
    {
        bool Set(string name) => !string.IsNullOrWhiteSpace(SecretValues.Names.Contains(name, StringComparer.Ordinal)
            ? SecretValues.Read(c, name) : c[name]);
        ReadinessItem Item(string name, string[] names, bool ready, string note, bool simulated = false) =>
            new(name, simulated ? "simulated" : ready ? "configured" : "missing", names,
                names.Where(x => !Set(x)).ToArray(), note);
        return new
        {
            demoMode = demo, providerChecksPerformed = false,
            checklist = new[]
            {
                Item("dispatcher identity", ["CIVICOPS_DISPATCHERS", "CIVICOPS_DISPATCH_TOKEN"],
                    Set("CIVICOPS_DISPATCHERS") || Set("CIVICOPS_DISPATCH_TOKEN"), "Named hashed tokens preferred; legacy token is an alternative. Enterprise identity remains owner work.", demo),
                Item("AMD reasoning", ["CIVICOPS_LLM_BASE_URL", "CIVICOPS_LLM_MODEL", "CIVICOPS_LLM_PROVIDER_LABEL", "CIVICOPS_LLM_API_KEY"],
                    Set("CIVICOPS_LLM_BASE_URL"), "Model/label have defaults; key depends on your endpoint. Hardware/performance require owner measurement.", !Set("CIVICOPS_LLM_BASE_URL")),
                Item("ElevenLabs sessions", ["ELEVENLABS_API_KEY", "ELEVENLABS_AGENT_ID", "ELEVENLABS_SESSION_MODE"],
                    Set("ELEVENLABS_API_KEY") && Set("ELEVENLABS_AGENT_ID"), "Session mode defaults to public; private modes fetch ephemeral credentials from the backend.", demo || !Set("ELEVENLABS_API_KEY")),
                Item("ElevenLabs inbound authentication", ["CIVICOPS_ELEVENLABS_TOOL_SECRET", "ELEVENLABS_WORKSPACE_TOOL_SECRET_ID", "ELEVENLABS_WEBHOOK_SECRET", "ELEVENLABS_POST_CALL_WEBHOOK_ID", "PUBLIC_BASE_URL", "ELEVENLABS_WIDGET_ALLOWED_HOSTS"],
                    InboundRequired.All(Set), "Workspace secret id is used during provisioning; widget hosts must be reviewed by the owner.", demo),
                Item("ElevenLabs callbacks", ["ELEVENLABS_CALLBACK_AGENT_ID", "ELEVENLABS_AGENT_PHONE_NUMBER_ID", "ELEVENLABS_PHONE_PROVIDER", "ELEVENLABS_TRANSFER_PHONE"],
                    Set("ELEVENLABS_CALLBACK_AGENT_ID") && Set("ELEVENLABS_AGENT_PHONE_NUMBER_ID"), "Optional; phone provider defaults to twilio; transfer phone is optional; case consent is required.", demo),
                Item("Zendesk", ["ZENDESK_SUBDOMAIN", "ZENDESK_EMAIL", "ZENDESK_API_TOKEN", "ZENDESK_OAUTH_TOKEN", "ZENDESK_WEBHOOK_SECRET"],
                    Set("ZENDESK_SUBDOMAIN") && (Set("ZENDESK_OAUTH_TOKEN") || Set("ZENDESK_EMAIL") && Set("ZENDESK_API_TOKEN")), "OAuth token OR email + API token; webhook secret required only for inbound events. Connectivity check is explicit CLI only.", demo || !Set("ZENDESK_SUBDOMAIN") || !(Set("ZENDESK_OAUTH_TOKEN") || Set("ZENDESK_EMAIL") && Set("ZENDESK_API_TOKEN"))),
                Item("training", ["CIVICOPS_TRAINING_CLIENTS", "CIVICOPS_CERT_POLICY", "CIVICOPS_TRAINING_LIMIT", "CIVICOPS_TRAINING_PASS_SCORE", "CIVICOPS_TRAINING_CERTIFICATIONS_ENABLED"],
                    Set("CIVICOPS_TRAINING_CLIENTS"), "Separate scoped hashed clients; certificate policy defaults to warn in demo. Rules and limits have safe defaults.", demo),
                Item("retention", ["CIVICOPS_RETENTION_DAYS"], Set("CIVICOPS_RETENTION_DAYS"), "Disabled by default; configured purge removes closed case snapshots after their closing milestone. Audit and integration artifacts are retained; this is not complete PII erasure."),
                Item("email", ["CIVICOPS_EMAIL_MODE", "CIVICOPS_EMAIL_FROM", "CIVICOPS_EMAIL_NOTIFY_TO", "CIVICOPS_EMAIL_SMTP_HOST", "CIVICOPS_EMAIL_SMTP_PORT", "CIVICOPS_EMAIL_SMTP_USER", "CIVICOPS_EMAIL_SMTP_PASSWORD", "CIVICOPS_EMAIL_USE_SSL"],
                    Set("CIVICOPS_EMAIL_SMTP_HOST"), "Optional SMTP; demo always forces the local outbox.", demo || c["CIVICOPS_EMAIL_MODE"] != "smtp")
            }
        };
    }
}
