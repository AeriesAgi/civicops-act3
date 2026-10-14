using System.Text;
using System.Collections.Concurrent;

namespace CivicOps.Services;

/// <summary>Values are never returned from readiness endpoints. Remote adapters must implement this contract.</summary>
public interface ISecretProvider
{
    string? GetSecret(string name);
}

public sealed class EnvironmentSecretProvider(IConfiguration configuration) : ISecretProvider
{
    public string? GetSecret(string name)
    {
        // Portal/CLI secrets often carry a trailing newline; trim line endings and surrounding whitespace like mounted files.
        var value = configuration[name]?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Any(char.IsControl) || Encoding.UTF8.GetByteCount(value) > 16384) throw new SecretConfigurationException(name);
        return value;
    }
}

/// <summary>Explicit mounted files take precedence, including when an environment value also exists.</summary>
public sealed class MountedFileSecretProvider(IConfiguration configuration, ISecretProvider fallback) : ISecretProvider
{
    private const int MaximumBytes = 16384;
    public string? GetSecret(string name)
    {
        var filename = configuration[name + "_FILE"];
        if (string.IsNullOrWhiteSpace(filename)) return fallback.GetSecret(name);
        try
        {
            if (!Path.IsPathFullyQualified(filename)) throw new IOException();
            using var stream = File.OpenRead(filename);
            if (stream.Length is <= 0 or > MaximumBytes) throw new IOException();
            var bytes = new byte[MaximumBytes + 1];
            var length = stream.ReadAtLeast(bytes, 1, throwOnEndOfStream: false);
            while (length <= MaximumBytes)
            {
                var count = stream.Read(bytes, length, bytes.Length - length);
                if (count == 0) break;
                length += count;
            }
            if (length > MaximumBytes) throw new IOException();
            var secret = new UTF8Encoding(false, true).GetString(bytes, 0, length).TrimEnd('\r', '\n');
            if (secret.Length == 0 || secret.Any(char.IsControl)) throw new IOException();
            return secret;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Deliberately omit filename, source bytes, and inner exception.
            throw new SecretConfigurationException(name);
        }
    }
}

public sealed class SecretConfigurationException(string? name = null) : Exception(name is null
    ? "Configured secret source is unavailable or invalid."
    : $"Configured secret {name} is unavailable or invalid (embedded control characters, empty/unreadable file, or over 16 KB). Re-enter the value without line breaks.");

public static class SecretValues
{
    internal static readonly ConcurrentDictionary<string, byte> Observed = new(StringComparer.Ordinal);
    public static readonly string[] Names =
    [
        "CIVICOPS_OIDC_CLIENT_SECRET", "CIVICOPS_LLM_API_KEY", "GEMINI_API_KEY", "ELEVENLABS_API_KEY",
        "CIVICOPS_ELEVENLABS_TOOL_SECRET", "ELEVENLABS_WEBHOOK_SECRET", "ZENDESK_API_TOKEN",
        "ZENDESK_OAUTH_TOKEN", "ZENDESK_WEBHOOK_SECRET", "CIVICOPS_EMAIL_SMTP_PASSWORD",
        "CIVICOPS_DISPATCH_TOKEN", "CIVICOPS_DISPATCHERS", "CIVICOPS_TRAINING_CLIENTS",
        "CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH", "CIVICOPS_DATABASE_URL", "CIVICOPS_AUDIT_SIGNING_KEY",
        "CIVICOPS_CHANNEL_INGRESS_SECRET", "CIVICOPS_CHANNEL_ENCRYPTION_KEY", "CIVICOPS_TWILIO_AUTH_TOKEN",
        "CIVICOPS_CLICKATELL_API_KEY", "CIVICOPS_CLICKATELL_CALLBACK_SECRET", "CIVICOPS_WHATSAPP_APP_SECRET",
        "CIVICOPS_WHATSAPP_VERIFY_TOKEN", "CIVICOPS_WHATSAPP_ACCESS_TOKEN", "CIVICOPS_SIP_CALLBACK_SECRET"
    ];

    public static ISecretProvider Create(IConfiguration configuration)
    {
        var source = configuration["CIVICOPS_SECRET_PROVIDER"]?.Trim().ToLowerInvariant();
        if (source is not (null or "" or "env" or "files"))
            throw new SecretConfigurationException(); // Vault adapters are an explicit documented extension, never a silent fallback.
        return new MountedFileSecretProvider(configuration, new EnvironmentSecretProvider(configuration));
    }

    public static string? Read(IConfiguration configuration, string name)
    {
        var value = Create(configuration).GetSecret(name);
        if (!string.IsNullOrEmpty(value)) Observed.TryAdd(value, 0);
        return value;
    }
}

public sealed class SensitiveValueRedactor(IConfiguration configuration)
{
    private readonly string[] _values = SecretValues.Names.Select(name => SecretValues.Read(configuration, name))
        .Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).Distinct(StringComparer.Ordinal)
        .OrderByDescending(value => value.Length).ToArray();

    public string Redact(string? text)
    {
        var safe = text ?? "";
        foreach (var value in _values.Concat(SecretValues.Observed.Keys).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
            safe = safe.Replace(value, "[REDACTED-SECRET]", StringComparison.Ordinal);
        return PiiRedactor.Redact(safe);
    }
}

public sealed class RedactingLoggerProvider(ILoggerProvider inner, SensitiveValueRedactor redactor) : ILoggerProvider, ISupportExternalScope
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName), redactor);
    public void Dispose() => inner.Dispose();
    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        if (inner is ISupportExternalScope supported) supported.SetScopeProvider(new RedactingScopes(scopeProvider, redactor));
    }

    private sealed class RedactingScopes(IExternalScopeProvider scopes, SensitiveValueRedactor redactor) : IExternalScopeProvider
    {
        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) => scopes.ForEachScope((scope, innerState) =>
            callback(scope is IEnumerable<KeyValuePair<string, object?>> fields ? fields.Select(field => new KeyValuePair<string, object?>(field.Key,
                field.Value is null ? null : redactor.Redact(field.Value.ToString()))).ToList() : redactor.Redact(scope?.ToString()), innerState), state);
        public IDisposable Push(object? state) => scopes.Push(state);
    }

    private sealed class RedactingLogger(ILogger logger, SensitiveValueRedactor redactor) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => state is IEnumerable<KeyValuePair<string, object?>> fields
            ? logger.BeginScope(Sanitize(fields)) : logger.BeginScope(redactor.Redact(state.ToString()));
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = redactor.Redact(formatter(state, exception));
            if (exception is not null) message += " (" + exception.GetType().Name + ")";
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                var safeFields = Sanitize(fields);
                logger.Log(logLevel, eventId, safeFields, null, (_, _) => message);
            }
            else logger.Log(logLevel, eventId, message, null, static (value, _) => value);
        }
        private List<KeyValuePair<string, object?>> Sanitize(IEnumerable<KeyValuePair<string, object?>> fields) => fields
            .Select(field => new KeyValuePair<string, object?>(field.Key, field.Value is null || field.Value is bool
                || field.Value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                ? field.Value : redactor.Redact(field.Value.ToString()))).ToList();
    }
}

public static class SecretRegistration
{
    public static void AddCivicOpsSecrets(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ISecretProvider>(provider => SecretValues.Create(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton<SensitiveValueRedactor>();
        foreach (var descriptor in builder.Services.Where(item => item.ServiceType == typeof(ILoggerProvider)).ToArray())
        {
            builder.Services.Remove(descriptor);
            builder.Services.AddSingleton<ILoggerProvider>(provider =>
            {
                var inner = descriptor.ImplementationInstance as ILoggerProvider
                    ?? descriptor.ImplementationFactory?.Invoke(provider) as ILoggerProvider
                    ?? (ILoggerProvider)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
                return new RedactingLoggerProvider(inner, provider.GetRequiredService<SensitiveValueRedactor>());
            });
        }
    }
}
