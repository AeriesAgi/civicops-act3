using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;

namespace CivicOps.Services;

public sealed class DispatcherAccess(IConfiguration configuration)
{
    private readonly IReadOnlyList<string[]> _identities = ValidateIdentities(SecretValues.Read(configuration, "CIVICOPS_DISPATCHERS"));
    public bool DemoMode => bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demo) && demo;
    public IResult? Check(HttpRequest request)
    {
        var context = request.HttpContext;
        if (context.User.Identity?.IsAuthenticated is true)
        {
            context.Items["dispatcherIdentity"] = context.User.Identity.Name ?? "Authenticated staff";
            context.Items["dispatcherPartition"] = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.Identity.Name;
            return null; // The endpoint's explicit permission policy decides access.
        }
        if (DemoMode)
        {
            context.Items["dispatcherIdentity"] = "Demo supervisor (not signed in, unverified)";
            context.Items["dispatcherPartition"] = "demo:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return null;
        }
        var identities = _identities;
        var legacy = SecretValues.Read(configuration, "CIVICOPS_DISPATCH_TOKEN");
        if (identities.Count == 0 && string.IsNullOrWhiteSpace(legacy))
            return Results.Problem("Dispatcher access is not configured.", statusCode: 503);
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization.Length > 1031)
            return Results.Unauthorized();
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..].Trim()));
        foreach (var entry in identities)
        {
            if (!TryHash(entry[1], out var expectedHash)) continue;
            if (CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash))
            {
                context.Items["dispatcherIdentity"] = entry[0].Trim();
                context.Items["dispatcherPartition"] = Convert.ToHexString(suppliedHash);
                return null;
            }
        }
        if (!string.IsNullOrWhiteSpace(legacy) && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(legacy)), suppliedHash))
        {
            context.Items["dispatcherIdentity"] = "Configured dispatcher";
            context.Items["dispatcherPartition"] = Convert.ToHexString(suppliedHash);
            return null;
        }
        return Results.Unauthorized();
    }
    public ClaimsPrincipal? AuthenticateBootstrap(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization.Length is <= 7 or > 1031) return null;
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..].Trim()));
        string? name = _identities.FirstOrDefault(entry => TryHash(entry[1], out var expected) && CryptographicOperations.FixedTimeEquals(expected, suppliedHash))?[0].Trim();
        var legacy = SecretValues.Read(configuration, "CIVICOPS_DISPATCH_TOKEN");
        if (name is null && !string.IsNullOrWhiteSpace(legacy) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(legacy)), suppliedHash)) name = "Configured dispatcher";
        if (name is null) return null;
        return new(new ClaimsIdentity([new(ClaimTypes.Name, name), new(ClaimTypes.NameIdentifier, Convert.ToHexString(suppliedHash)),
            new(ClaimTypes.Role, "dispatcher"), new(ClaimTypes.Role, "supervisor"), new("civicops:tenant", "default"), new("civicops:bootstrap", "dispatcher")], "bootstrap-bearer"));
    }
    public string Identity(HttpContext context) => context.Items["dispatcherIdentity"] as string ?? context.User.Identity?.Name ?? "Unknown dispatcher";
    private static string[][] ValidateIdentities(string? setting)
    {
        var identities = (setting ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':', 2)).ToArray();
        if (identities.Any(x => x.Length != 2 || string.IsNullOrWhiteSpace(x[0]) || x[0].Length > 80
            || x[0].Any(char.IsControl) || !TryHash(x[1], out _))
            || identities.Select(x => x[0].Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Length
            || identities.Select(x => x[1]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != identities.Length)
            throw new InvalidOperationException("CIVICOPS_DISPATCHERS requires unique verified name:SHA256(token) pairs separated by semicolons.");
        return identities;
    }
    private static bool TryHash(string value, out byte[] hash)
    {
        try { hash = Convert.FromHexString(value); return hash.Length == 32; }
        catch (FormatException) { hash = []; return false; }
    }
}
