using System.Security.Claims;
using System.Text.RegularExpressions;

namespace CivicOps.Services;

/// <summary>Validated trusted-issuer settings. An entirely unconfigured issuer remains disabled.</summary>
public sealed class IdentityOptions
{
    public static readonly string[] Roles = PermissionMatrix.Roles;
    public bool DemoMode { get; init; }
    public bool Configured { get; init; }
    public string Provider { get; init; } = "disabled";
    public string Authority { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string RoleClaim { get; init; } = "roles";
    public string GroupClaim { get; init; } = "groups";
    public string TenantClaim { get; init; } = "municipality_id";
    public string? FixedTenantId { get; init; }
    public string? HostedDomain { get; init; }
    public IReadOnlyDictionary<string, string[]> RoleMap { get; init; } = new Dictionary<string, string[]>();
    public IReadOnlyDictionary<string, string[]> GroupMap { get; init; } = new Dictionary<string, string[]>();
    public int SessionMinutes { get; init; } = 480;

    public static IdentityOptions From(IConfiguration configuration)
    {
        string Read(string key) => (key == "CLIENT_SECRET" ? SecretValues.Read(configuration, "CIVICOPS_OIDC_CLIENT_SECRET") : configuration["CIVICOPS_OIDC_" + key])?.Trim() ?? "";
        var demo = bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var enabled) && enabled;
        var session = Read("SESSION_MINUTES"); var sessionMinutes = 480;
        if (session.Length > 0 && (!int.TryParse(session, out sessionMinutes) || sessionMinutes is < 5 or > 1440))
            throw new InvalidOperationException("CIVICOPS_OIDC_SESSION_MINUTES must be between 5 and 1440.");
        var provider = Read("PROVIDER").ToLowerInvariant();
        var configured = new[] { provider, Read("AUTHORITY"), Read("CLIENT_ID"), Read("CLIENT_SECRET"), Read("DIRECTORY_ID") }.Any(value => value.Length > 0);
        if (!configured) return new() { DemoMode = demo, SessionMinutes = sessionMinutes };
        if (provider.Length == 0) provider = "generic";
        if (provider is not ("generic" or "entra" or "google")) throw new InvalidOperationException("CIVICOPS_OIDC_PROVIDER must be generic, entra or google.");
        var authority = Read("AUTHORITY");
        if (authority.Length == 0 && provider == "google") authority = "https://accounts.google.com";
        if (authority.Length == 0 && provider == "entra")
        {
            var directory = Read("DIRECTORY_ID");
            if (!Guid.TryParse(directory, out _)) throw new InvalidOperationException("Entra requires a specific CIVICOPS_OIDC_DIRECTORY_ID; common and organizations are not accepted.");
            authority = $"https://login.microsoftonline.com/{directory}/v2.0";
        }
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("CIVICOPS_OIDC_AUTHORITY must be an absolute HTTPS issuer URL without credentials, query or fragment.");
        var clientId = Read("CLIENT_ID"); var secret = Read("CLIENT_SECRET");
        if (clientId.Length is < 1 or > 256 || secret.Length is < 1 or > 4096 || clientId.Any(char.IsControl) || secret.Any(char.IsControl))
            throw new InvalidOperationException("Configured OIDC requires bounded CIVICOPS_OIDC_CLIENT_ID and CIVICOPS_OIDC_CLIENT_SECRET.");
        var roleMap = ParseMap(Read("ROLE_MAP")); var groupMap = ParseMap(Read("GROUP_MAP"));
        if (roleMap.Count + groupMap.Count == 0) throw new InvalidOperationException("Configure an explicit OIDC role or group mapping. Unmapped accounts cannot sign in.");
        var fixedTenant = Read("TENANT_ID");
        if (fixedTenant.Length > 0 && !ValidTenantId(fixedTenant)) throw new InvalidOperationException("Invalid CIVICOPS_OIDC_TENANT_ID.");
        string Claim(string key, string fallback)
        {
            var value = Read(key); if (value.Length == 0) return fallback;
            if (value.Length > 256 || value.Any(char.IsControl)) throw new InvalidOperationException("Invalid OIDC claim name.");
            return value;
        }
        var domain = Read("HOSTED_DOMAIN");
        if (domain.Length > 0 && (provider != "google" || !Regex.IsMatch(domain, "^[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}$")))
            throw new InvalidOperationException("CIVICOPS_OIDC_HOSTED_DOMAIN is a Google Workspace domain restriction.");
        return new() { DemoMode = demo, Configured = true, Provider = provider, Authority = authority.TrimEnd('/'),
            ClientId = clientId, ClientSecret = secret, RoleMap = roleMap, GroupMap = groupMap,
            RoleClaim = Claim("ROLE_CLAIM", "roles"), GroupClaim = Claim("GROUP_CLAIM", "groups"),
            TenantClaim = Claim("TENANT_CLAIM", "municipality_id"), FixedTenantId = fixedTenant.Length == 0 ? null : fixedTenant,
            HostedDomain = domain.Length == 0 ? null : domain, SessionMinutes = sessionMinutes };
    }

    private static Dictionary<string, string[]> ParseMap(string value)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split('=', 2); var roles = pair.Length == 2 ? pair[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
            if (pair.Length != 2 || pair[0].Length is < 1 or > 256 || pair[0].Any(char.IsControl)
                || roles.Length == 0 || roles.Any(role => !Roles.Contains(role, StringComparer.Ordinal)) || roles.Distinct().Count() != roles.Length
                || !result.TryAdd(pair[0], roles)) throw new InvalidOperationException("OIDC mappings require unique external-value=local-role[,local-role] entries separated by semicolons.");
        }
        return result;
    }

    public static bool ValidTenantId(string? value) => value is not null && TenantContext.IsValidId(value) && Regex.IsMatch(value, "^[a-z0-9][a-z0-9-]{0,63}$");
    public ClaimsPrincipal MapPrincipal(ClaimsPrincipal external)
    {
        if (HostedDomain is not null && external.FindFirst("hd")?.Value != HostedDomain)
            throw new InvalidOperationException("This identity does not belong to the configured Google Workspace domain.");
        var subject = external.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || subject.Any(char.IsControl)) throw new InvalidOperationException("Issuer did not supply a valid subject.");
        var tenants = external.FindAll(TenantClaim).Select(claim => claim.Value).Distinct().ToArray();
        var tenant = FixedTenantId ?? (tenants.Length == 1 ? tenants[0] : null);
        if (!ValidTenantId(tenant)) throw new InvalidOperationException("Issuer did not supply a single valid municipality claim.");
        var roles = external.FindAll(RoleClaim).SelectMany(claim => RoleMap.TryGetValue(claim.Value, out var mapped) ? mapped : [])
            .Concat(external.FindAll(GroupClaim).SelectMany(claim => GroupMap.TryGetValue(claim.Value, out var mapped) ? mapped : [])).Distinct().ToArray();
        if (roles.Length == 0) throw new InvalidOperationException("Identity has no configured CivicOps role mapping.");
        var identity = new ClaimsIdentity(EnterpriseIdentity.SessionScheme, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new(ClaimTypes.NameIdentifier, subject));
        identity.AddClaim(new(ClaimTypes.Name, (external.FindFirst("name")?.Value ?? subject)[..Math.Min(160, (external.FindFirst("name")?.Value ?? subject).Length)]));
        identity.AddClaim(new("civicops:tenant", tenant!));
        identity.AddClaim(new("civicops:issuer", Authority));
        foreach (var role in roles) identity.AddClaim(new(ClaimTypes.Role, role));
        if (roles.Contains("crew"))
        {
            var crews = external.FindAll("crew_id").Select(claim => claim.Value).Distinct().ToArray();
            if (crews.Length != 1 || string.IsNullOrWhiteSpace(crews[0]) || crews[0].Length > 80 || crews[0].Any(char.IsControl))
                throw new InvalidOperationException("Crew identity requires a single bounded crew_id claim.");
            identity.AddClaim(new("civicops:crew", crews[0]));
            var workers = external.FindAll("worker_id").Select(claim => claim.Value).Distinct().ToArray();
            if (workers.Length > 1 || workers.Length == 1 && !TenantContext.IsValidId(workers[0])) throw new InvalidOperationException("Crew worker_id claim must be a single municipality-local worker identifier.");
            if (workers.Length == 1) identity.AddClaim(new("civicops:worker", workers[0]));
        }
        return new(identity);
    }
}
