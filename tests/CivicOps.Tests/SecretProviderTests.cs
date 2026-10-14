using System.Net.Http.Headers;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CivicOps.Tests;

public sealed class SecretProviderTests
{
    private const string Sentinel = "synthetic-sensitive-sentinel-for-round-five";
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void DefaultSourceReadsConfiguredEnvironmentValue()
    {
        var provider = SecretValues.Create(Config(new() { ["CIVICOPS_LLM_API_KEY"] = Sentinel }));
        Assert.Equal(Sentinel, provider.GetSecret("CIVICOPS_LLM_API_KEY"));
        Assert.Null(provider.GetSecret("UNCONFIGURED_SYNTHETIC_SECRET"));
    }

    [Theory]
    [InlineData("line\nbreak")] [InlineData("oversize")]
    public void EnvironmentSecretsAreBoundedAndFailuresNeverEchoValues(string value)
    {
        value = value == "oversize" ? new string('s', 16385) : value;
        var error = Assert.Throws<SecretConfigurationException>(() => SecretValues.Read(Config(new() { ["CIVICOPS_LLM_API_KEY"] = value }), "CIVICOPS_LLM_API_KEY"));
        Assert.DoesNotContain(value, error.ToString());
    }

    [Fact]
    public void MountedSourceWinsAndRotationIsReadWithoutRestart()
    {
        var filename = Path.Combine(Path.GetTempPath(), "civicops-secret-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(filename, Sentinel + "\n");
            var config = Config(new() { ["ELEVENLABS_API_KEY"] = "synthetic-env-fallback", ["ELEVENLABS_API_KEY_FILE"] = filename });
            Assert.Equal(Sentinel, ElevenLabsOptions.From(config).ApiKey);
            File.WriteAllText(filename, Sentinel + "-rotated\r\n");
            Assert.Equal(Sentinel + "-rotated", ElevenLabsOptions.From(config).ApiKey);
        }
        finally { File.Delete(filename); }
    }

    [Theory]
    [InlineData("")] [InlineData("\0synthetic")] [InlineData("a\nb")] [InlineData("oversize")]
    public void InvalidMountedSecretsFailClosedWithoutValuesOrPaths(string value)
    {
        var filename = Path.Combine(Path.GetTempPath(), "civicops-secret-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(filename, value == "oversize" ? new string('s', 16385) : value);
            var config = Config(new() { ["ELEVENLABS_API_KEY"] = Sentinel, ["ELEVENLABS_API_KEY_FILE"] = filename });
            var exception = Assert.Throws<SecretConfigurationException>(() => ElevenLabsOptions.From(config));
            Assert.DoesNotContain(filename, exception.ToString()); Assert.DoesNotContain(Sentinel, exception.ToString());
            Assert.Null(exception.InnerException);
        }
        finally { File.Delete(filename); }
    }

    [Theory]
    [InlineData("/nonexistent/synthetic-private-file")] [InlineData("relative-file")]
    public void UnreadableFilesNeverFallBackToEnvironment(string filename)
    {
        var config = Config(new() { ["CIVICOPS_LLM_API_KEY"] = Sentinel, ["CIVICOPS_LLM_API_KEY_FILE"] = filename });
        Assert.Throws<SecretConfigurationException>(() => AiOptions.FromConfiguration(config));
    }

    [Theory]
    [InlineData("azure-key-vault")] [InlineData("hashicorp-vault")] [InlineData("unrecognised")]
    public void UnimplementedRemoteAdaptersFailClosed(string source) => Assert.Throws<SecretConfigurationException>(() =>
        SecretValues.Create(Config(new() { ["CIVICOPS_SECRET_PROVIDER"] = source })));

    [Fact]
    public void LogsRedactSecretStructuredStateScopesAndExceptionMessages()
    {
        var captured = new CaptureProvider();
        using var provider = new RedactingLoggerProvider(captured, new SensitiveValueRedactor(Config(new() { ["CIVICOPS_LLM_API_KEY"] = Sentinel })));
        var logger = provider.CreateLogger("Synthetic.Test");
        using var scope = logger.BeginScope("Scope contains " + Sentinel + " resident@example.invalid");
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(1), "Structured value {Value} phone {Phone}")(logger, Sentinel, "082 555 0100", new InvalidOperationException(Sentinel));
        var output = string.Join('\n', captured.Lines);
        Assert.DoesNotContain(Sentinel, output); Assert.DoesNotContain("resident@example.invalid", output); Assert.DoesNotContain("082 555 0100", output);
        Assert.Contains("REDACTED-SECRET", output); Assert.Contains("InvalidOperationException", output);
    }

    [Fact]
    public async Task ReadinessHealthAndProviderStatusOmitSentinelSecrets()
    {
        using var app = new TestApp();
        app.Settings["ELEVENLABS_API_KEY"] = Sentinel; app.Settings["CIVICOPS_LLM_API_KEY"] = Sentinel;
        app.Settings["CIVICOPS_EMAIL_SMTP_PASSWORD"] = Sentinel; app.Settings["ZENDESK_API_TOKEN"] = Sentinel;
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-dispatch-token");
        foreach (var path in new[] { "/healthz", "/api/integrations/readiness", "/api/channels/status", "/api/email/status", "/api/ai/status" })
        {
            var response = await client.GetAsync(path); response.EnsureSuccessStatusCode();
            Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync());
        }
    }

    private sealed class CaptureProvider : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull { Lines.Add(state.ToString()!); return null; }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Lines.Add(formatter(state, exception)); if (exception is not null) Lines.Add(exception.ToString()); }
    }
}
