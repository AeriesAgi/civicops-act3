using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace CivicOps.Services;

public sealed record DemoLoginRequest(string Role, string TenantId = "default", string? CrewId = null);
public sealed record BootstrapLoginRequest(string Token);

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapCivicOpsIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/status", async (HttpContext http, IdentityOptions options, IAntiforgery antiforgery, TenantStore tenants, CancellationToken ct) =>
        {
            var csrf = antiforgery.GetAndStoreTokens(http);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { authenticated = http.User.Identity?.IsAuthenticated is true, options.DemoMode,
                oidcConfigured = options.Configured, oidcSession = http.User.HasClaim(claim => claim.Type == "civicops:issuer"), options.Provider, name = http.User.Identity?.Name,
                roles = http.User.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray(),
                permissions = PermissionMatrix.Permissions.Keys.Where(policy => PermissionMatrix.Allows(policy, http.User)).ToArray(),
                tenantId = http.User.FindFirst("civicops:tenant")?.Value, csrfToken = csrf.RequestToken,
                tenants = options.DemoMode ? (await tenants.ListAsync(ct)).Select(tenant => new { tenant.Id, tenant.Name, tenant.Slug }).ToArray() : null });
        }).RequireAuthorization("public").RequireRateLimiting("session");
        app.MapGet("/auth/login", (string? returnUrl, IdentityOptions options) =>
        {
            if (!options.Configured) return Results.Problem("Staff SSO is disabled. Configure a trusted OIDC issuer or use administrator-issued bootstrap access.", statusCode: 503);
            if (!LocalReturn(returnUrl)) return Results.BadRequest(new { error = "returnUrl must be a local application path." });
            return Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl ?? "/" }, [EnterpriseIdentity.OidcScheme]);
        }).RequireAuthorization("public").RequireRateLimiting("session");
        app.MapPost("/auth/demo-login", async (HttpContext http, DemoLoginRequest request, IdentityOptions options, TenantStore tenants, WorkforceStore workforce, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!options.DemoMode) return Results.NotFound();
            if (await CsrfAsync(http, antiforgery) is { } blocked) return blocked;
            if (!IdentityOptions.Roles.Contains(request.Role, StringComparer.Ordinal) || await tenants.GetAsync(request.TenantId, ct) is null)
                return Results.BadRequest(new { error = "Choose a supported demo role and synthetic municipality." });
            var identity = new ClaimsIdentity(EnterpriseIdentity.SessionScheme, ClaimTypes.Name, ClaimTypes.Role);
            // Give every demo browser an independent opaque subject so SOS location/cancel
            // capabilities cannot be replayed by another session using the same role.
            identity.AddClaim(new(ClaimTypes.NameIdentifier, "demo:" + request.Role + ":" + Guid.NewGuid().ToString("N")));
            identity.AddClaim(new(ClaimTypes.Name, "EXPLICIT DEMO " + request.Role + " (synthetic, unverified)"));
            identity.AddClaim(new(ClaimTypes.Role, request.Role)); identity.AddClaim(new("civicops:tenant", request.TenantId)); identity.AddClaim(new("civicops:demo", "true"));
            if (request.Role == "crew")
            {
                using var tenantScope = TenantContext.Use(request.TenantId);
                var crews = await workforce.ListCrewsAsync(ct); var crewId = request.CrewId ?? (crews.Count > 0 ? crews[0].Id : null);
                if (crewId is null || !crews.Any(crew => crew.Id == crewId)) return Results.BadRequest(new { error = "Choose a crew in the demo municipality." });
                identity.AddClaim(new("civicops:crew", crewId));
            }
            await http.SignInAsync(EnterpriseIdentity.SessionScheme, new ClaimsPrincipal(identity), Session(options));
            return Results.Ok(new { authenticated = true, mode = "EXPLICIT DEMO", synthetic = true });
        }).RequireAuthorization("public").RequireRateLimiting("session");
        app.MapPost("/auth/bootstrap-login", async (HttpContext http, BootstrapLoginRequest request, BootstrapUserStore users, IdentityOptions options, TenantStore tenants, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (await CsrfAsync(http, antiforgery) is { } blocked) return blocked;
            var principal = await users.AuthenticateAsync(request.Token ?? "", ct);
            if (principal is null || await tenants.GetAsync(principal.FindFirst("civicops:tenant")!.Value, ct) is null) return Results.Unauthorized();
            await http.SignInAsync(EnterpriseIdentity.SessionScheme, principal, Session(options));
            return Results.Ok(new { authenticated = true });
        }).RequireAuthorization("public").RequireRateLimiting("session");
        app.MapPost("/auth/logout", async (HttpContext http, IdentityOptions options) =>
        {
            var oidc = http.User.HasClaim(claim => claim.Type == "civicops:issuer");
            await http.SignOutAsync(EnterpriseIdentity.SessionScheme);
            return options.Configured && oidc ? Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [EnterpriseIdentity.OidcScheme]) : Results.Ok(new { authenticated = false });
        }).RequireAuthorization("authenticated").RequireRateLimiting("session");
        var accounts = app.MapGroup("/api/admin/service-accounts").RequireAuthorization("admin").RequireRateLimiting("dispatch");
        accounts.MapGet("", async (ServiceAccountStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(TenantContext.CurrentId, ct)));
        accounts.MapPost("", async (HttpContext http, ServiceAccountCreateRequest request, ServiceAccountStore store, CancellationToken ct) =>
            await Mutation(async () => Results.Ok(await store.CreateAsync(TenantContext.CurrentId, request, Actor(http), ct))));
        accounts.MapPost("/{id}/rotate", async (HttpContext http, string id, ServiceAccountRotateRequest request, ServiceAccountStore store, CancellationToken ct) =>
            await Mutation(async () => await store.RotateAsync(TenantContext.CurrentId, id, request, Actor(http), ct) is { } issued ? Results.Ok(issued) : Results.NotFound()));
        accounts.MapPost("/{id}/revoke", async (HttpContext http, string id, ServiceAccountStore store, CancellationToken ct) =>
            await Mutation(async () => await store.RevokeAsync(TenantContext.CurrentId, id, Actor(http), ct) is { } account ? Results.Ok(account) : Results.NotFound()));
        var localUsers = app.MapGroup("/api/admin/users").RequireAuthorization("admin").RequireRateLimiting("dispatch");
        localUsers.MapGet("", async (BootstrapUserStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(TenantContext.CurrentId, ct)));
        localUsers.MapPost("", async (HttpContext http, BootstrapUserCreateRequest request, BootstrapUserStore store, WorkforceStore workforce, CancellationToken ct) =>
            await Mutation(async () => await ValidCrew(request.Roles, request.CrewId, workforce, ct)
                ? Results.Ok(await store.CreateAsync(TenantContext.CurrentId, request, Actor(http), ct)) : Results.BadRequest(new { error = "Crew binding must belong to this municipality." })));
        localUsers.MapPut("/{id}", async (HttpContext http, string id, BootstrapUserUpdateRequest request, BootstrapUserStore store, WorkforceStore workforce, CancellationToken ct) =>
            await Mutation(async () => !await ValidCrew(request.Roles, request.CrewId, workforce, ct) ? Results.BadRequest(new { error = "Crew binding must belong to this municipality." })
                : await store.UpdateAsync(TenantContext.CurrentId, id, request, Actor(http), ct) is { } user ? Results.Ok(user) : Results.NotFound()));
        localUsers.MapPost("/{id}/rotate", async (HttpContext http, string id, BootstrapUserStore store, CancellationToken ct) =>
            await Mutation(async () => await store.RotateAsync(TenantContext.CurrentId, id, Actor(http), ct) is { } issued ? Results.Ok(issued) : Results.NotFound()));
        localUsers.MapPost("/{id}/revoke", async (HttpContext http, string id, BootstrapUserStore store, CancellationToken ct) =>
            await Mutation(async () => await store.RevokeAsync(TenantContext.CurrentId, id, Actor(http), ct) is { } user ? Results.Ok(user) : Results.NotFound()));
        return app;
    }
    private static AuthenticationProperties Session(IdentityOptions options) => new() { IsPersistent = false, AllowRefresh = false,
        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(options.SessionMinutes) };
    private static string Actor(HttpContext http) => http.User.Identity?.Name ?? "Authenticated administrator";
    private static bool LocalReturn(string? value) => value is null || value.Length <= 512 && value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) && !value.Contains('\\') && !value.Any(char.IsControl);
    private static async Task<IResult?> CsrfAsync(HttpContext http, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(http); return null; }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem("Refresh the sign-in page and supply its CSRF token.", statusCode: 400,
                title: "Request verification failed", extensions: new Dictionary<string, object?>
                { ["error"] = "Refresh the sign-in page and supply its CSRF token." });
        }
    }
    private static async Task<IResult> Mutation(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
    }
    private static async Task<bool> ValidCrew(string[]? roles, string? id, WorkforceStore workforce, CancellationToken ct) =>
        roles is not null && (!roles.Contains("crew") && string.IsNullOrWhiteSpace(id) || id is not null && (await workforce.ListCrewsAsync(ct)).Any(crew => crew.Id == id));
}
