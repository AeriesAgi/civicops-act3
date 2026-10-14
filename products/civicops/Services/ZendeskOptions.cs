namespace CivicOps.Services;

public sealed class ZendeskOptions
{
    public string? Subdomain { get; init; }
    public string? Email { get; init; }
    public string? ApiToken { get; init; }
    public string? OAuthToken { get; init; }
    public string? WebhookSecret { get; init; }
    public string DataDirectory { get; init; } = string.Empty;
    public bool DemoMode { get; init; }
    public int MaximumAttempts { get; init; } = 5;
    public TimeSpan RetryBase { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public bool Live => !DemoMode && !string.IsNullOrWhiteSpace(Subdomain)
        && (!string.IsNullOrWhiteSpace(OAuthToken) || (!string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(ApiToken)));

    public static ZendeskOptions FromConfiguration(IConfiguration configuration, IWebHostEnvironment environment)
        => FromConfiguration(configuration, DataPaths.Resolve(configuration, environment));

    public static ZendeskOptions FromConfiguration(IConfiguration configuration, string dataDirectory)
    {
        var subdomain = configuration["ZENDESK_SUBDOMAIN"]?.Trim();
        if (!string.IsNullOrEmpty(subdomain) && (subdomain.Length > 63 || subdomain.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new InvalidOperationException("ZENDESK_SUBDOMAIN must be a subdomain label, without a URL or slash.");
        return new()
        {
            Subdomain = subdomain, Email = configuration["ZENDESK_EMAIL"],
            ApiToken = SecretValues.Read(configuration, "ZENDESK_API_TOKEN"), OAuthToken = SecretValues.Read(configuration, "ZENDESK_OAUTH_TOKEN"),
            WebhookSecret = SecretValues.Read(configuration, "ZENDESK_WEBHOOK_SECRET"),
            DemoMode = bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demo) && demo,
            DataDirectory = dataDirectory, RequestTimeout = ProviderCircuit.TimeoutFrom(configuration)
        };
    }
}
