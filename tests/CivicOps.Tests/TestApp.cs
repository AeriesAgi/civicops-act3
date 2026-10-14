using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CivicOps.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
namespace CivicOps.Tests;

public sealed class TestApp : WebApplicationFactory<Program>
{
    internal static readonly ConcurrentDictionary<string, string> HandledErrors = new(StringComparer.Ordinal);
    private static readonly IDisposable Diagnostics = DiagnosticListener.AllListeners.Subscribe(new SyntheticExceptionObserver());
    private string? _testDatabase;
    public TestApp() { _ = Diagnostics; Settings["CIVICOPS_STORAGE"] = StoreBackends.Current; }
    public bool KeepData { get; set; }
    public bool EnableBackgroundDelivery { get; set; } = true;
    public string DataDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "civicops-tests-" + Guid.NewGuid().ToString("N"));
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["CIVICOPS_DEMO_MODE"] = "true", ["CIVICOPS_EMAIL_MODE"] = "outbox",
        ["CIVICOPS_MAP_TILE_URL"] = "none", ["CIVICOPS_MAP_ATTRIBUTION"] = "", ["CIVICOPS_MAP_CREW_STALE_MINUTES"] = "",
        ["CIVICOPS_LLM_BASE_URL"] = "", ["CIVICOPS_LLM_API_KEY"] = "",
        ["ELEVENLABS_API_KEY"] = "", ["ELEVENLABS_AGENT_ID"] = "",
        ["CIVICOPS_ELEVENLABS_TOOL_SECRET"] = "", ["ELEVENLABS_WEBHOOK_SECRET"] = "test-webhook-secret",
        ["ELEVENLABS_AGENT_PHONE_NUMBER_ID"] = "", ["ZENDESK_API_TOKEN"] = "",
        ["ZENDESK_OAUTH_TOKEN"] = "", ["ZENDESK_EMAIL"] = "", ["ZENDESK_SUBDOMAIN"] = "", ["ZENDESK_WEBHOOK_SECRET"] = "test-webhook-secret",
        ["CIVICOPS_EMAIL_SMTP_HOST"] = "", ["CIVICOPS_EMAIL_NOTIFY_TO"] = "", ["CIVICOPS_DISPATCH_TOKEN"] = "test-dispatch-token",
        ["CIVICOPS_DISPATCHERS"] = "", ["CIVICOPS_INTAKE_LIMIT"] = "1000", ["CIVICOPS_DISPATCH_LIMIT"] = "1000"
    };
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Settings.TryAdd("CIVICOPS_SECRET_PROVIDER", "env");
        foreach (var name in SecretValues.Names) Settings.TryAdd(name + "_FILE", "");
        Settings.TryAdd("CIVICOPS_INFORMATION_OFFICER_CONTACT", "");
        Settings.TryAdd("CIVICOPS_PII_RETENTION_DAYS", "");
        Settings.TryAdd("CIVICOPS_AUTH_LIMIT", "1000"); Settings.TryAdd("CIVICOPS_TOOL_LIMIT", "1000");
        Settings.TryAdd("CIVICOPS_CREW_OIDC_CLIENT_ID", "");
        Settings.TryAdd("CIVICOPS_SIP_CALLBACK_SECRET", "");
        Settings.TryAdd("CIVICOPS_CHANNEL_MODE", "");
        Settings.TryAdd("CIVICOPS_CHANNEL_ENCRYPTION_KEY", "");
        Settings.TryAdd("CIVICOPS_CHANNEL_INGRESS_SECRET", "");
        Settings.TryAdd("CIVICOPS_CHANNEL_WEBHOOK_BASE_URL", "");
        Settings.TryAdd("CIVICOPS_CHANNEL_SEND_ENABLED", "");
        Settings.TryAdd("CIVICOPS_CHANNEL_RETENTION_DAYS", "");
        Settings.TryAdd("CIVICOPS_TWILIO_AUTH_TOKEN", "");
        Settings.TryAdd("CIVICOPS_TWILIO_ACCOUNT_SID", "");
        Settings.TryAdd("CIVICOPS_TWILIO_FROM_NUMBER", "");
        Settings.TryAdd("CIVICOPS_CLICKATELL_API_KEY", "");
        Settings.TryAdd("CIVICOPS_CLICKATELL_CALLBACK_SECRET", "");
        Settings.TryAdd("CIVICOPS_CLICKATELL_FROM_NUMBER", "");
        Settings.TryAdd("CIVICOPS_WHATSAPP_APP_SECRET", "");
        Settings.TryAdd("CIVICOPS_WHATSAPP_VERIFY_TOKEN", "");
        Settings.TryAdd("CIVICOPS_WHATSAPP_ACCESS_TOKEN", "");
        Settings.TryAdd("CIVICOPS_WHATSAPP_PHONE_NUMBER_ID", "");
        Settings.TryAdd("CIVICOPS_ELEVENLABS_INBOUND_SIP_URI", "");

        // No owner-configured issuer or bootstrap credential may enter an offline test implicitly.
        foreach (var key in new[] { "PROVIDER", "AUTHORITY", "CLIENT_ID", "CLIENT_SECRET", "DIRECTORY_ID", "ROLE_CLAIM", "GROUP_CLAIM", "TENANT_CLAIM", "ROLE_MAP", "GROUP_MAP", "TENANT_ID", "HOSTED_DOMAIN", "SESSION_MINUTES" })
            Settings.TryAdd("CIVICOPS_OIDC_" + key, "");
        Settings.TryAdd("CIVICOPS_BOOTSTRAP_ADMIN_TOKEN_HASH", ""); Settings.TryAdd("CIVICOPS_BOOTSTRAP_ADMIN_TENANT", "");
        Settings["CIVICOPS_DATA_DIR"] = DataDirectory;
        if (Settings.GetValueOrDefault("CIVICOPS_STORAGE") == "postgres")
            Settings["CIVICOPS_DATABASE_URL"] = _testDatabase = StoreBackends.TestConnection(DataDirectory);
        else Settings.TryAdd("CIVICOPS_DATABASE_URL", "");
        builder.UseEnvironment("Testing");
        foreach (var entry in Settings) builder.UseSetting(entry.Key, entry.Value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
        if (!EnableBackgroundDelivery)
            builder.ConfigureServices(services =>
            {
                // Tests asserting one request's unchanged snapshot deliberately own delivery scheduling.
                foreach (var descriptor in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(IntegrationOutboxWorker)).ToArray()) services.Remove(descriptor);
            });
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }
    // The full suite runs many in-memory hosts in parallel; on a loaded box the default 100 s client
    // timeout aborted otherwise-passing requests. Allow a generous ceiling so only real hangs fail.
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.Timeout = TimeSpan.FromMinutes(5);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !KeepData && _testDatabase is not null) StoreBackends.DeletePartition(_testDatabase);
        if (disposing && !KeepData && Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true);
    }
    // Synthetic test diagnostics contain only exception type and code stack; messages/payloads are omitted.
    private sealed class SyntheticExceptionObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(DiagnosticListener listener) => listener.Subscribe(this, name => name == "Microsoft.AspNetCore.Diagnostics.HandledException");
        public void OnNext(KeyValuePair<string, object?> diagnostic)
        {
            if (diagnostic.Value is null) return;
            var type = diagnostic.Value.GetType();
            if (type.GetProperty("exception")?.GetValue(diagnostic.Value) is Exception exception
                && type.GetProperty("httpContext")?.GetValue(diagnostic.Value) is Microsoft.AspNetCore.Http.HttpContext context)
            {
                var correlation = context.Items[CorrelationMiddleware.ItemKey]?.ToString() ?? context.TraceIdentifier;
                HandledErrors[correlation] = exception.GetType().FullName + "\n" + exception.StackTrace;
            }
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
