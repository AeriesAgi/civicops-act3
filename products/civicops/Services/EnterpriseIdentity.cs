using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CivicOps.Services;

public static class EnterpriseIdentity
{
    public const string SessionScheme = "civicops-session";
    public const string OidcScheme = "civicops-oidc";
    public static IServiceCollection AddCivicOpsIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = IdentityOptions.From(configuration);
        _ = BootstrapUserStore.ValidateConfiguredAdmin(configuration);
        // Tickets already live in ServerSessionStore. Keep cookie/CSRF keys in the same
        // process boundary so a read-only container needs no HOME key directory.
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton(settings);
        services.AddSingleton<ServerSessionStore>();
        services.AddSingleton<ServiceAccountStore>();
        services.AddSingleton<BootstrapUserStore>();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CivicOps-CSRF";
            options.Cookie.Name = settings.DemoMode ? "CivicOps-Demo-CSRF" : "__Host-CivicOps-CSRF";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = settings.DemoMode ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
        });
        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SessionScheme;
            options.DefaultSignInScheme = SessionScheme;
            options.DefaultChallengeScheme = SessionScheme;
        }).AddCookie(SessionScheme, options =>
        {
            options.Cookie.Name = "__Host-CivicOps-Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(settings.SessionMinutes);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            options.Events.OnValidatePrincipal = async context =>
            {
                if (OperationalHealth.IsGlobalProbe(context.HttpContext)) return;
                // Role changes and revocation affect existing bootstrap sessions on their next request.
                var id = context.Principal?.FindFirst("civicops:bootstrap-user")?.Value;
                if (id is null) return;
                var principal = await context.HttpContext.RequestServices.GetRequiredService<BootstrapUserStore>()
                    .PrincipalForUserAsync(id, context.HttpContext.RequestAborted);
                if (principal is null || principal.FindFirst("civicops:bootstrap-generation")?.Value != context.Principal?.FindFirst("civicops:bootstrap-generation")?.Value) context.RejectPrincipal();
                else context.ReplacePrincipal(principal);
            };
        });
        services.AddOptions<CookieAuthenticationOptions>(SessionScheme).Configure<ServerSessionStore>((options, store) => options.SessionStore = store);
        if (settings.Configured) authentication.AddOpenIdConnect(OidcScheme, options =>
        {
            options.SignInScheme = SessionScheme;
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.MapInboundClaims = false;
            options.SaveTokens = false;
            options.RequireHttpsMetadata = true;
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";
            options.Scope.Clear(); options.Scope.Add("openid"); options.Scope.Add("profile");
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Events.OnTokenValidated = async context =>
            {
                try
                {
                    var principal = settings.MapPrincipal(context.Principal!);
                    if (await context.HttpContext.RequestServices.GetRequiredService<TenantStore>()
                        .GetAsync(principal.FindFirst("civicops:tenant")!.Value, context.HttpContext.RequestAborted) is null)
                    { context.Fail("Identity municipality is not configured."); return; }
                    if (principal.FindFirst("civicops:crew") is { } crew)
                    {
                        using var tenantScope = TenantContext.Use(principal.FindFirst("civicops:tenant")!.Value);
                        var crews = await context.HttpContext.RequestServices.GetRequiredService<WorkforceStore>().ListCrewsAsync(context.HttpContext.RequestAborted);
                        if (!crews.Any(value => value.Id == crew.Value)) { context.Fail("Identity crew is not configured in this municipality."); return; }
                    }
                    context.Principal = principal;
                    // Keep only the ID token in the server ticket to support issuer sign-out hints.
                    if (context.Properties is { } properties && context.TokenEndpointResponse?.IdToken is { Length: > 0 } idToken)
                        properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = idToken }]);
                }
                catch (InvalidOperationException error) { context.Fail(error.Message); }
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse(); context.Response.StatusCode = 401;
                return context.Response.WriteAsJsonAsync(new { error = "Staff sign-in failed. Check issuer configuration and mapped municipality/roles." });
            };
            options.Events.OnRedirectToIdentityProviderForSignOut = context =>
            {
                // Google has no end-session endpoint; local sign-out still destroys the CivicOps ticket.
                if (string.IsNullOrWhiteSpace(context.ProtocolMessage.IssuerAddress))
                { context.HandleResponse(); context.Response.Redirect(context.Properties.RedirectUri ?? "/"); }
                return Task.CompletedTask;
            };
        });
        return services;
    }
}

/// <summary>The browser receives an encrypted opaque reference; claims and credentials stay server-side.
/// Sessions deliberately expire on process restart. This matches CivicOps' single-process storage boundary.</summary>
public sealed class ServerSessionStore(TimeProvider time) : ITicketStore
{
    private readonly ConcurrentDictionary<string, AuthenticationTicket> _tickets = new(StringComparer.Ordinal);
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        Prune(); var key = "session-" + Guid.NewGuid().ToString("N"); _tickets[key] = ticket; return Task.FromResult(key);
    }
    public Task RenewAsync(string key, AuthenticationTicket ticket) { _tickets[key] = ticket; return Task.CompletedTask; }
    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (_tickets.TryGetValue(key, out var ticket) && ticket.Properties.ExpiresUtc > time.GetUtcNow()) return Task.FromResult<AuthenticationTicket?>(ticket);
        _tickets.TryRemove(key, out _); return Task.FromResult<AuthenticationTicket?>(null);
    }
    public Task RemoveAsync(string key) { _tickets.TryRemove(key, out _); return Task.CompletedTask; }
    private void Prune()
    {
        foreach (var pair in _tickets) if (pair.Value.Properties.ExpiresUtc <= time.GetUtcNow()) _tickets.TryRemove(pair.Key, out _);
    }
}
