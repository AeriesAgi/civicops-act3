using CivicOps.Models;
using System.Security.Cryptography;
using System.Text;

namespace CivicOps.Services;

/// <summary>Applies to static assets, rejected requests and errors as well as successful API replies.
/// The offline interface uses external same-origin scripts/styles, so inline code is never permitted.</summary>
public static class SecurityHeaders
{
    private const string NonceKey = "civicops:csp-nonce";
    private const string NoncePlaceholder = "CIVICOPS_SCRIPT_NONCE";
    public static string Nonce(HttpContext http)
    {
        if (http.Items[NonceKey] is not string value)
            http.Items[NonceKey] = value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        return value;
    }

    public static IApplicationBuilder UseCivicOpsSecurityHeaders(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var environment = http.RequestServices.GetRequiredService<IHostEnvironment>();
        var eleven = http.RequestServices.GetRequiredService<ElevenLabsOptions>();
        var map = http.RequestServices.GetService<MapOptions>();
        _ = Nonce(http);
        http.Response.OnStarting(() =>
        {
            Apply(http, environment.IsProduction(), eleven, map);
            return Task.CompletedTask;
        });
        await next(http);
    });

    public static IApplicationBuilder UseCivicOpsHtmlShell(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if ((HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
            && http.Request.Path.Value is "/" or "/index.html" or "/crew" or "/crew/" or "/crew/index.html")
        {
            if (http.Request.Path.Value == "/crew")
            {
                http.Response.Redirect(http.Request.PathBase + "/crew/");
                return;
            }
            var shell = http.Request.Path.StartsWithSegments("/crew") ? "crew/index.html" : "index.html";
            var file = http.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider.GetFileInfo(shell);
            if (file.Exists)
            {
                using var reader = new StreamReader(file.CreateReadStream());
                var html = (await reader.ReadToEndAsync(http.RequestAborted)).Replace(NoncePlaceholder, Nonce(http), StringComparison.Ordinal);
                http.Response.ContentType = "text/html; charset=utf-8";
                http.Response.Headers.CacheControl = "no-store";
                http.Response.ContentLength = Encoding.UTF8.GetByteCount(html);
                if (!HttpMethods.IsHead(http.Request.Method)) await http.Response.WriteAsync(html, http.RequestAborted);
                return;
            }
        }
        await next(http);
    });

    public static void Apply(HttpContext http, bool production, ElevenLabsOptions eleven, MapOptions? map = null)
    {
        var voice = !eleven.Simulated && !string.IsNullOrWhiteSpace(eleven.AgentId);
        var crew = http.Request.Path.StartsWithSegments("/crew");
        http.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy(eleven, Nonce(http), map, crew);
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        // Permission availability does not collect anything. The crew page also requires a live shift,
        // explicit consent and a visible page before requesting a fix; the server repeats these guards.
        http.Response.Headers["Permissions-Policy"] = "camera=" + (crew ? "(self)" : "()") + ", geolocation=" + (crew ? "(self)" : "()") + ", microphone=" + (voice ? "(self)" : "()")
            + ", payment=(), usb=(), browsing-topics=()";
        http.Response.Headers["X-Frame-Options"] = "DENY";
        http.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        http.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        // HSTS is meaningful only over HTTPS; ingress must preserve the trusted request scheme.
        if (production && http.Request.IsHttps)
            http.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }

    public static string ContentSecurityPolicy(ElevenLabsOptions eleven, string nonce, MapOptions? map = null, bool crew = false)
    {
        var voice = !eleven.Simulated && !string.IsNullOrWhiteSpace(eleven.AgentId);
        var scripts = "'nonce-" + nonce + "'";
        var styles = "'self'";
        var fonts = "'self'";
        var images = "'self'";
        if (crew) images += " blob:"; // Local, metadata-stripped photo previews only.
        if (map?.TileOrigin is { } tileOrigin) images += " " + tileOrigin;
        var connections = "'self'";
        var media = "'self'";
        var workers = "'self'";
        if (voice)
        {
            scripts += eleven.SessionMode == "public" ? " https://unpkg.com/@elevenlabs/convai-widget-embed@0.19.0/dist/index.js"
                : " https://cdn.jsdelivr.net/npm/@elevenlabs/client@1.27.0/dist/lib.iife.js";
            connections += " https://api.elevenlabs.io wss://api.elevenlabs.io";
            // Regional API origins are accepted only from the same provider domain and never interpolate credentials.
            if (Uri.TryCreate(eleven.ApiBase, UriKind.Absolute, out var api) && api.Scheme == "https" && api.IsDefaultPort
                && string.IsNullOrEmpty(api.UserInfo) && string.IsNullOrEmpty(api.Query) && string.IsNullOrEmpty(api.Fragment)
                && api.Host.EndsWith(".elevenlabs.io", StringComparison.OrdinalIgnoreCase))
                connections += " " + api.GetLeftPart(UriPartial.Authority) + " wss://" + api.IdnHost;
            if (eleven.SessionMode is "public" or "webrtc")
                connections += " https://livekit.rtc.elevenlabs.io wss://livekit.rtc.elevenlabs.io https://*.livekit.cloud wss://*.livekit.cloud";
            // The pinned SDK uses this pinned audio-resampling worklet in a dedicated audio context.
            scripts += " blob: https://cdn.jsdelivr.net/npm/@alexanderolsen/libsamplerate-js@2.1.2/dist/libsamplerate.worklet.js";
            if (eleven.SessionMode == "public")
            {
                styles += " 'nonce-" + nonce + "' https://fonts.googleapis.com";
                fonts += " https://fonts.gstatic.com";
                images += " https://storage.googleapis.com/eleven-public-cdn/";
            }
            media += " blob:";
            workers += " blob:";
        }
        return "default-src 'none'; script-src " + scripts + "; script-src-attr 'none'; style-src " + styles + "; style-src-attr 'none';"
            + " connect-src " + connections + "; img-src " + images + "; font-src " + fonts + "; media-src " + media + "; worker-src " + workers
            + "; manifest-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; frame-src 'none'";
    }
}
