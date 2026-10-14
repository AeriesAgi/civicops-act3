using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CivicOps.Services;

public static class CivicOpsTelemetry
{
    public const string Name = "CivicOps";
    public static readonly ActivitySource Activities = new(Name, "1.0.0");
    public static readonly Meter Meter = new(Name, "1.0.0");
    public static readonly Counter<long> Intake = Meter.CreateCounter<long>("civicops.intake.total", "{report}", "Successful new resident intake requests.");
    public static readonly Histogram<double> TriageLatency = Meter.CreateHistogram<double>("civicops.triage.duration", "s", "Backend extraction and triage time.");
    public static readonly Counter<long> AgentDecisions = Meter.CreateCounter<long>("civicops.agent.decisions", "{decision}");
    public static readonly Counter<long> AiFallbacks = Meter.CreateCounter<long>("civicops.ai.fallbacks", "{decision}");
    public static readonly Counter<long> OutboxAttempts = Meter.CreateCounter<long>("civicops.outbox.attempts", "{attempt}");
    public static readonly Histogram<double> OutboxDeliveryLatency = Meter.CreateHistogram<double>("civicops.outbox.delivery.duration", "s", "Elapsed creation-to-delivery including retries; simulated deliveries are labelled.");

    public static void AddCivicOpsObservability(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options => { options.IncludeScopes = true; options.UseUtcTimestamp = true; options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
        builder.Services.Configure<LoggerFactoryOptions>(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
        builder.Services.Configure<Microsoft.AspNetCore.Http.ProblemDetailsOptions>(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToHexString() ?? context.HttpContext.TraceIdentifier;
            context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.Items[CorrelationMiddleware.ItemKey] ?? context.HttpContext.TraceIdentifier;
        });
        builder.Services.AddSingleton<OperationalSignalsWorker>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<OperationalSignalsWorker>());

        var demo = bool.TryParse(builder.Configuration["CIVICOPS_DEMO_MODE"], out var isDemo) && isDemo;
        var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var mode = builder.Configuration["CIVICOPS_TELEMETRY"]?.Trim().ToLowerInvariant();
        mode = string.IsNullOrEmpty(mode) ? !demo && !string.IsNullOrWhiteSpace(endpoint) ? "otlp" : "off" : mode;
        if (mode is not ("off" or "console" or "otlp")) throw new InvalidOperationException("CIVICOPS_TELEMETRY must be off, console, or otlp.");
        if (demo && mode == "otlp" || bool.TryParse(builder.Configuration["OTEL_SDK_DISABLED"], out var disabled) && disabled) mode = "off";
        if (mode == "off") return;
        var telemetry = builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "civicops",
            serviceVersion: builder.Configuration["OTEL_SERVICE_VERSION"]));
        telemetry.WithTracing(tracing =>
        {
            tracing.AddSource(Name, "Npgsql").AddAspNetCoreInstrumentation(options => options.RecordException = false)
                .AddHttpClientInstrumentation(options => options.RecordException = false)
                .AddProcessor(new SafeSpanProcessor(new SensitiveValueRedactor(builder.Configuration)));
            if (mode == "console") tracing.AddConsoleExporter();
        }).WithMetrics(metrics =>
        {
            metrics.AddMeter(Name, "Npgsql").AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
                .AddView("civicops.triage.duration", new ExplicitBucketHistogramConfiguration { Boundaries = [0.1, 0.25, 0.5, 1, 2, 5, 10, 20, 30, 60] })
                .AddView("civicops.outbox.delivery.duration", new ExplicitBucketHistogramConfiguration { Boundaries = [1, 5, 10, 30, 60, 120, 300, 900, 3600] });
            if (mode == "console") metrics.AddConsoleExporter();
        });
        if (mode == "otlp") telemetry.UseOtlpExporter(); // Standard OTEL_* variables, including per-signal overrides.
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeScopes = true; options.IncludeFormattedMessage = true;
            if (mode == "console") options.AddConsoleExporter();
        });
    }

    public static void Propagate(HttpRequestMessage request)
    {
        if (Activity.Current is not { } activity) return;
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{activity.TraceId}-{activity.SpanId}-{(activity.Recorded ? "01" : "00")}");
        if (activity.GetBaggageItem("civicops.correlation_id") is { } correlation) request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlation);
    }

    public sealed class SafeSpanProcessor(SensitiveValueRedactor? redactor = null) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            // Do not export URL/query strings or SQL text. Provider request/response content is never a span attribute.
            foreach (var tag in activity.TagObjects.ToArray())
                if (tag.Key is "url.full" or "url.path" or "url.query" or "http.url" or "http.target" or "db.statement" or "db.query.text" or "exception.message" or "exception.stacktrace") activity.SetTag(tag.Key, null);
                else if (tag.Value is string text) activity.SetTag(tag.Key, redactor?.Redact(text) ?? PiiRedactor.Redact(text));
        }
    }
}

public sealed class CorrelationMiddleware(RequestDelegate next, ILogger<CorrelationMiddleware> logger)
{
    private static readonly Action<ILogger, string, string, int, double, Exception?> RequestCompleted = LoggerMessage.Define<string, string, int, double>(
        LogLevel.Information, new EventId(4601, "RequestCompleted"), "HTTP {Method} {Route} completed with {StatusCode} in {DurationMs} ms");
    public const string ItemKey = "civicops.correlation";
    public async Task InvokeAsync(HttpContext context)
    {
        using var ownedActivity = Activity.Current is null ? CreateRequestActivity(context) : null;
        var requestActivity = Activity.Current;
        var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
        var correlation = IsValid(supplied) ? supplied : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = correlation;
        Activity.Current?.SetTag("civicops.correlation_id", correlation);
        Activity.Current?.AddBaggage("civicops.correlation_id", correlation);
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlation,
            ["TraceId"] = Activity.Current?.TraceId.ToHexString(), ["SpanId"] = Activity.Current?.SpanId.ToHexString() });
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Correlation-ID"] = correlation;
            if (requestActivity is { } activity) context.Response.Headers["traceparent"] = $"00-{activity.TraceId}-{activity.SpanId}-{(activity.Recorded ? "01" : "00")}";
            return Task.CompletedTask;
        });
        var started = Stopwatch.GetTimestamp();
        await next(context);
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        if (logger.IsEnabled(LogLevel.Information)) RequestCompleted(logger, context.Request.Method, route, context.Response.StatusCode, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2), null);
    }
    public static bool IsValid(string value) => value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
        && PiiRedactor.Redact(value) == value;
    private static Activity CreateRequestActivity(HttpContext context)
    {
        var activity = new Activity("CivicOps.request").SetIdFormat(ActivityIdFormat.W3C);
        if (ActivityContext.TryParse(context.Request.Headers["traceparent"].ToString(), context.Request.Headers["tracestate"].ToString(), out var parent))
            activity.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
        return activity.Start();
    }
}
