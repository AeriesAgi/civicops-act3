namespace CivicOps.Services;

public sealed class AiOptions
{
    public string? BaseUrl { get; set; }
    public string Model { get; set; } = "Qwen/Qwen3-8B";
    public string? ApiKey { get; set; }
    public string ProviderLabel { get; set; } = "OpenAI-compatible model endpoint (hardware unverified)";
    public int TimeoutMs { get; set; } = 10_000;
    public int MaxAttempts { get; set; } = 2;
    public int MaxResponseBytes { get; set; } = 65_536;
    /// <summary>openai-compatible (default) or azure-openai.</summary>
    public string Provider { get; set; } = "openai-compatible";
    public string? AzureEndpoint { get; set; }
    public string? AzureDeployment { get; set; }
    public string AzureApiVersion { get; set; } = "2025-04-01-preview";
    /// <summary>key when an API key is configured, otherwise managed-identity.</summary>
    public string AzureAuth { get; set; } = "managed-identity";
    public string? AzureClientId { get; set; }
    /// <summary>Reasoning models (gpt-5-mini) spend hidden reasoning tokens from this budget.</summary>
    public int MaxCompletionTokens { get; set; } = 4_000;
    public string ReasoningEffort { get; set; } = "low";
    public bool IsAzure => Provider == "azure-openai";
    public bool IsGemini => Provider == "gemini";
    /// <summary>Optional second provider tried before the deterministic fallback.</summary>
    public AiOptions? Secondary { get; set; }
    public const string GeminiCompletionUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    /// <summary>Azure Container Apps/App Service managed identity endpoint (platform-provided IDENTITY_ENDPOINT/IDENTITY_HEADER).</summary>
    public string? IdentityEndpoint { get; set; }
    public string? IdentityHeader { get; set; }

    /// <summary>CIVICOPS_LLM_PROVIDER may name one provider or an ordered pair, e.g. gemini,azure-openai.</summary>
    public static AiOptions FromConfiguration(IConfiguration configuration)
    {
        var chain = (string.IsNullOrWhiteSpace(configuration["CIVICOPS_LLM_PROVIDER"]) ? "openai-compatible" : configuration["CIVICOPS_LLM_PROVIDER"]!)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToArray();
        if (chain.Length is 0 or > 2 || chain.Distinct().Count() != chain.Length)
            throw new InvalidOperationException("CIVICOPS_LLM_PROVIDER must name one or two distinct providers.");
        var primary = ForProvider(configuration, chain[0]);
        if (chain.Length == 2)
        {
            var secondary = ForProvider(configuration, chain[1]);
            if (secondary.CompletionUri is not null) primary.Secondary = secondary;
        }
        return primary;
    }

    private static AiOptions ForProvider(IConfiguration configuration, string provider)
    {
        var demo = bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demoMode) && demoMode;
        var liveInDemo = DemoLiveProviders(configuration).Contains("llm");
        if (provider is not ("openai-compatible" or "azure-openai" or "gemini"))
            throw new InvalidOperationException("CIVICOPS_LLM_PROVIDER entries must be gemini, azure-openai or openai-compatible.");
        var gemini = provider == "gemini";
        var azure = provider == "azure-openai";
        var apiKey = gemini ? SecretValues.Read(configuration, "GEMINI_API_KEY") : SecretValues.Read(configuration, "CIVICOPS_LLM_API_KEY");
        var deployment = configuration["CIVICOPS_AZURE_OPENAI_DEPLOYMENT"]?.Trim();
        var modelSetting = gemini ? configuration["GEMINI_MODEL"]?.Trim() : configuration["CIVICOPS_LLM_MODEL"]?.Trim();
        var model = !string.IsNullOrWhiteSpace(modelSetting) ? modelSetting!
            : gemini ? "gemini-2.5-flash" : azure && !string.IsNullOrWhiteSpace(deployment) ? deployment! : "Qwen/Qwen3-8B";
        var label = gemini ? configuration["GEMINI_PROVIDER_LABEL"]?.Trim() : configuration["CIVICOPS_LLM_PROVIDER_LABEL"]?.Trim();
        var auth = configuration["CIVICOPS_AZURE_OPENAI_AUTH"]?.Trim().ToLowerInvariant();
        auth = string.IsNullOrWhiteSpace(auth) ? (string.IsNullOrWhiteSpace(apiKey) ? "managed-identity" : "key") : auth;
        if (auth is not ("key" or "managed-identity"))
            throw new InvalidOperationException("CIVICOPS_AZURE_OPENAI_AUTH must be key or managed-identity.");
        var effort = configuration["CIVICOPS_LLM_REASONING_EFFORT"]?.Trim().ToLowerInvariant();
        effort = string.IsNullOrWhiteSpace(effort) ? "low" : effort;
        if (effort is not ("minimal" or "low" or "medium" or "high" or "none"))
            throw new InvalidOperationException("CIVICOPS_LLM_REASONING_EFFORT must be none, minimal, low, medium or high.");
        var enabled = !demo || liveInDemo;
        return new()
        {
            Provider = provider,
            BaseUrl = enabled && !azure ? (gemini ? (string.IsNullOrWhiteSpace(apiKey) ? null : GeminiCompletionUrl) : configuration["CIVICOPS_LLM_BASE_URL"]) : null,
            AzureEndpoint = enabled && azure ? configuration["CIVICOPS_AZURE_OPENAI_ENDPOINT"]?.Trim() : null,
            AzureDeployment = deployment,
            AzureApiVersion = string.IsNullOrWhiteSpace(configuration["CIVICOPS_AZURE_OPENAI_API_VERSION"]) ? "2025-04-01-preview" : configuration["CIVICOPS_AZURE_OPENAI_API_VERSION"]!.Trim(),
            AzureAuth = auth,
            AzureClientId = configuration["CIVICOPS_AZURE_OPENAI_CLIENT_ID"]?.Trim(),
            Model = model,
            ApiKey = apiKey,
            ProviderLabel = !string.IsNullOrWhiteSpace(label) ? label!
                : azure ? $"Azure OpenAI ({model})" : gemini ? $"Google Gemini ({model})" : "OpenAI-compatible model endpoint (hardware unverified)",
            TimeoutMs = int.TryParse(configuration["CIVICOPS_LLM_TIMEOUT_MS"], out var timeout)
                ? Math.Clamp(timeout, 250, 60_000) : azure || gemini ? 20_000 : 10_000,
            MaxCompletionTokens = int.TryParse(configuration["CIVICOPS_LLM_MAX_COMPLETION_TOKENS"], out var tokens)
                ? Math.Clamp(tokens, 256, 16_000) : 4_000,
            ReasoningEffort = effort,
            IdentityEndpoint = configuration["IDENTITY_ENDPOINT"],
            IdentityHeader = configuration["IDENTITY_HEADER"]
        };
    }

    /// <summary>Comma list of providers (llm, elevenlabs) allowed to call real services while CIVICOPS_DEMO_MODE=true.</summary>
    public static HashSet<string> DemoLiveProviders(IConfiguration configuration) =>
        (configuration["CIVICOPS_DEMO_LIVE_PROVIDERS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    public Uri? CompletionUri
    {
        get
        {
            if (IsAzure)
            {
                if (!Uri.TryCreate(AzureEndpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https"
                    || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
                    || !(endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                        || endpoint.Host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase))
                    || string.IsNullOrWhiteSpace(AzureDeployment) || !System.Text.RegularExpressions.Regex.IsMatch(AzureDeployment, "^[A-Za-z0-9._-]{1,64}$")
                    || !System.Text.RegularExpressions.Regex.IsMatch(AzureApiVersion, "^[0-9]{4}-[0-9]{2}-[0-9]{2}(-preview)?$")) return null;
                return new Uri($"https://{endpoint.Host}/openai/deployments/{AzureDeployment}/chat/completions?api-version={AzureApiVersion}");
            }
            if (IsGemini) return BaseUrl == GeminiCompletionUrl ? new Uri(GeminiCompletionUrl) : null;
            if (!Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;
            var path = uri.AbsolutePath.TrimEnd('/');
            return new Uri(uri.GetLeftPart(UriPartial.Authority)
                + (path.EndsWith("/v1", StringComparison.Ordinal) ? path : path + "/v1") + "/chat/completions");
        }
    }

    public bool Configured => IsAzure ? !string.IsNullOrWhiteSpace(AzureEndpoint) : !string.IsNullOrWhiteSpace(BaseUrl);
}

public sealed record AiStatus(
    string Mode,
    string Model,
    string ProviderLabel,
    string EndpointLabel,
    long CallCount,
    long HttpRequestCount,
    long FallbackCount,
    double P50LatencyMs,
    double P95LatencyMs,
    double FallbackRate,
    int LatencyWindowSize,
    int TimeoutMs,
    int MaxAttempts);
