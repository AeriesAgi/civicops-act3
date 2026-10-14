using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Claims;

namespace CivicOps.Services;

public sealed record TrainingRequired;

public sealed class TrainingAccess
{
    public const string DemoToken = "civicops-demo-training";
    private readonly List<(string Name, byte[] Hash)> _clients;
    public bool DemoMode { get; }
    public bool Configured => _clients.Count > 0;
    public int RequestLimit { get; }
    public double PassScore { get; }
    public bool CertificationGrantsEnabled { get; }

    public TrainingAccess(IConfiguration configuration)
    {
        DemoMode = bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demo) && demo;
        var clients = new List<(string Name, byte[] Hash)>();
        foreach (var entry in (SecretValues.Read(configuration, "CIVICOPS_TRAINING_CLIENTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = entry.Split(':', 2);
            if (pair.Length != 2 || !Regex.IsMatch(pair[0], "^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")
                || !Regex.IsMatch(pair[1], "^[A-Fa-f0-9]{64}$"))
                throw new InvalidOperationException("CIVICOPS_TRAINING_CLIENTS requires unique name:sha256(token) pairs separated by semicolons.");
            var hash = Convert.FromHexString(pair[1]);
            if (clients.Any(client => client.Name == pair[0] || CryptographicOperations.FixedTimeEquals(client.Hash, hash)))
                throw new InvalidOperationException("CIVICOPS_TRAINING_CLIENTS contains duplicate names or token hashes.");
            clients.Add((pair[0], hash));
        }
        if (DemoMode && clients.Count == 0) clients.Add(("demo-training", SHA256.HashData(Encoding.UTF8.GetBytes(DemoToken))));
        var dispatcherHashes = (SecretValues.Read(configuration, "CIVICOPS_DISPATCHERS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split(':', 2)).Where(pair => pair.Length == 2 && Regex.IsMatch(pair[1], "^[A-Fa-f0-9]{64}$"))
            .Select(pair => Convert.FromHexString(pair[1])).ToList();
        if (!string.IsNullOrWhiteSpace(SecretValues.Read(configuration, "CIVICOPS_DISPATCH_TOKEN")))
            dispatcherHashes.Add(SHA256.HashData(Encoding.UTF8.GetBytes(SecretValues.Read(configuration, "CIVICOPS_DISPATCH_TOKEN")!)));
        if (clients.Any(client => dispatcherHashes.Any(hash => CryptographicOperations.FixedTimeEquals(hash, client.Hash))))
            throw new InvalidOperationException("Training and dispatcher tokens must be distinct to preserve scope boundaries.");
        _clients = clients;
        RequestLimit = int.TryParse(configuration["CIVICOPS_TRAINING_LIMIT"], out var limit) && limit is > 0 and <= 10000 ? limit : 60;
        PassScore = double.TryParse(configuration["CIVICOPS_TRAINING_PASS_SCORE"], System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var score) && score is >= 0 and <= 100 ? score : 80;
        CertificationGrantsEnabled = !bool.TryParse(configuration["CIVICOPS_TRAINING_CERTIFICATIONS_ENABLED"], out var enabled) || enabled;
    }

    public string Identity(HttpContext context) => context.Items["trainingIdentity"] as string ?? context.User.Identity?.Name ?? "Unknown training client";

    public ClaimsPrincipal? AuthenticateBootstrap(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization.Length is <= 7 or > 1031) return null;
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..].Trim()));
        var matched = _clients.FirstOrDefault(client => CryptographicOperations.FixedTimeEquals(client.Hash, supplied));
        return matched.Name is null ? null : new(new ClaimsIdentity([new(ClaimTypes.Name, matched.Name),
            new(ClaimTypes.NameIdentifier, Convert.ToHexString(supplied)), new(ClaimTypes.Role, "training-client"), new("civicops:tenant", "default")], "training-bearer"));
    }

    public IResult? Check(HttpContext context)
    {
        var required = context.GetEndpoint()?.Metadata.GetMetadata<TrainingRequired>() is not null;
        if (required && context.User.Identity?.IsAuthenticated is true
            && context.User.HasClaim(claim => claim.Type == "civicops:service-account") && !context.User.IsInRole("training-client"))
            return Results.StatusCode(403);
        if (context.User.Identity?.IsAuthenticated is true && context.User.IsInRole("training-client"))
        {
            if (!required && context.User.Identity.AuthenticationType == "civicops-session" && context.Request.Path.StartsWithSegments("/auth")) return null;
            if (!required && !context.User.HasClaim(claim => claim.Type == "civicops:service-account")) return Results.StatusCode(403);
            context.Items["trainingIdentity"] = context.User.Identity.Name;
            context.Items["trainingPartition"] = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return null;
        }
        var authorization = context.Request.Headers.Authorization.ToString();
        (string Name, byte[] Hash)? matched = null;
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && authorization.Length is > 7 and <= 1031)
        {
            var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..].Trim()));
            foreach (var client in _clients)
                if (CryptographicOperations.FixedTimeEquals(client.Hash, supplied)) matched = client;
        }
        // A recognized training bearer never acquires demo dispatcher or public intake permissions.
        if (matched is not null && !required) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!required) return null;
        if (!Configured) return Results.Problem("Training client access is not configured. Set CIVICOPS_TRAINING_CLIENTS.", statusCode: 503);
        if (matched is null) return Results.Unauthorized();
        context.Items["trainingIdentity"] = matched.Value.Name;
        context.Items["trainingPartition"] = Convert.ToHexString(matched.Value.Hash);
        return null;
    }
}
