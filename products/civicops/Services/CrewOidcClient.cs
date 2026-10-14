using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CivicOps.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CivicOps.Services;

/// <summary>Native public-client ID tokens are exchanged once after trusted discovery/JWKS validation. No issuer writes.</summary>
public sealed class CrewOidcClient(HttpClient http, IConfiguration configuration, IdentityOptions identity,
    CrewStore crews, TenantStore tenants, WorkforceStore workforce, TimeProvider time)
{
    private readonly string _clientId = ClientId(configuration);
    private readonly ConcurrentDictionary<string, CrewOidcChallenge> _challenges = new(StringComparer.Ordinal);
    private ConfigurationManager<OpenIdConnectConfiguration>? _manager;
    public CrewAuthConfiguration Configuration => new(identity.Configured && _clientId.Length > 0, identity.Configured ? identity.Authority : "", _clientId);
    public CrewOidcChallenge Challenge()
    {
        if (!Configuration.Enabled) throw new CrewRequestException(503, "Native OIDC is disabled. Configure a separate public native client registration or use supervised device pairing.");
        foreach (var pair in _challenges) if (pair.Value.ExpiresAt <= time.GetUtcNow()) _challenges.TryRemove(pair.Key, out _);
        if (_challenges.Count >= 1024) throw new CrewRequestException(503, "Sign-in challenge capacity reached. Retry later.");
        var challenge = new CrewOidcChallenge("challenge-" + Guid.NewGuid().ToString("N"), WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)), time.GetUtcNow().AddMinutes(5));
        _challenges[challenge.ChallengeId] = challenge; return challenge;
    }
    public async Task<CrewTokenIssued> ExchangeAsync(CrewOidcRequest request, string? expectedTenant, CancellationToken ct)
    {
        if (!Configuration.Enabled) throw new CrewRequestException(503, "Native OIDC is disabled; no identity-provider network request was made.");
        if (request.ChallengeId is null || !_challenges.TryRemove(request.ChallengeId, out var challenge) || challenge.ExpiresAt <= time.GetUtcNow())
            throw new CrewRequestException(401, "Sign-in challenge is missing, expired or already consumed. Start sign-in again.");
        CrewStore.Text(request.IdToken, "idToken", 16000, 32);
        _manager ??= new(identity.Authority + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(http) { RequireHttps = true });
        try
        {
            var metadata = await _manager.GetConfigurationAsync(ct);
            if (metadata.Issuer.TrimEnd('/') != identity.Authority) throw new CrewRequestException(401, "Trusted discovery issuer does not match the configured authority.");
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 16000 };
            var external = handler.ValidateToken(request.IdToken, new TokenValidationParameters
            {
                RequireSignedTokens = true, RequireExpirationTime = true, ValidateIssuerSigningKey = true, IssuerSigningKeys = metadata.SigningKeys,
                ValidateIssuer = true, ValidIssuer = metadata.Issuer, ValidateAudience = true, ValidAudience = _clientId,
                ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30), ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512]
            }, out var validated);
            ValidateNativeAudience((JwtSecurityToken)validated);
            var nonces = external.FindAll("nonce").Select(value => value.Value).ToArray();
            if (nonces.Length != 1 || nonces[0] != challenge.Nonce) throw new CrewRequestException(401, "Sign-in nonce does not match the one-use server challenge.");
            var principal = identity.MapPrincipal(external);
            if (!principal.IsInRole("crew")) throw new CrewRequestException(403, "Native credentials require an explicitly mapped crew role.");
            var tenantId = principal.FindFirstValue("civicops:tenant")!;
            if (expectedTenant is not null && tenantId != expectedTenant)
                throw new CrewRequestException(403, "Trusted identity municipality does not match the explicitly requested municipality. No device credential was issued.");
            if (await tenants.GetAsync(tenantId, ct) is null) throw new CrewRequestException(403, "Identity municipality is unavailable.");
            var workerIds = external.FindAll("worker_id").Select(value => value.Value).Distinct().ToArray();
            if (workerIds.Length != 1 || !TenantContext.IsValidId(workerIds[0])) throw new CrewRequestException(403, "A single trusted issuer worker_id binding is required.");
            using var scope = TenantContext.Use(tenantId);
            var worker = await workforce.GetWorkerAsync(workerIds[0], ct);
            if (worker is null || worker.CrewId != principal.FindFirstValue("civicops:crew")) throw new CrewRequestException(403, "Trusted issuer worker and crew binding is unavailable in this municipality.");
            return await crews.IssueOidcAsync(worker, new DateTimeOffset(validated.ValidTo, TimeSpan.Zero), ct);
        }
        catch (Exception error) when (error is SecurityTokenException or ArgumentException or InvalidOperationException && error is not CrewRequestException)
        { throw new CrewRequestException(401, "Native sign-in validation failed. Start a new challenge; check trusted issuer, signature, audience, expiry and explicit identity mappings."); }
    }
    private void ValidateNativeAudience(JwtSecurityToken validated)
    {
        // CivicOps trusts only this separate native registration. OIDC Core 3.1.3.7 allows
        // additional trusted audiences; our municipal policy deliberately configures none.
        // Inspect the signed raw payload so repeated JSON claim names cannot disappear during mapping.
        using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(validated.RawPayload));
        var audience = document.RootElement.EnumerateObject().Where(value => value.Name == "aud").ToArray();
        var authorizedParty = document.RootElement.EnumerateObject().Where(value => value.Name == "azp").ToArray();
        var singleAudience = audience.Length == 1 && (audience[0].Value.ValueKind == JsonValueKind.String
            ? audience[0].Value.GetString() == _clientId
            : audience[0].Value.ValueKind == JsonValueKind.Array && audience[0].Value.GetArrayLength() == 1
                && audience[0].Value[0].ValueKind == JsonValueKind.String && audience[0].Value[0].GetString() == _clientId);
        if (!singleAudience || authorizedParty.Length > 1 || authorizedParty.Length == 1
            && (authorizedParty[0].Value.ValueKind != JsonValueKind.String || authorizedParty[0].Value.GetString() != _clientId))
            throw new CrewRequestException(401, "Native sign-in requires exactly the configured native audience and, when supplied, its single matching authorized party.");
    }
    private static string ClientId(IConfiguration configuration)
    {
        var value = configuration["CIVICOPS_CREW_OIDC_CLIENT_ID"]?.Trim() ?? "";
        if (value.Length > 256 || value.Any(char.IsControl)) throw new InvalidOperationException("CIVICOPS_CREW_OIDC_CLIENT_ID must be a bounded public native client registration identifier."); return value;
    }
}
