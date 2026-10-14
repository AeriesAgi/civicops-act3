using System.Security.Claims;
using CivicOps.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;

namespace CivicOps.Tests;

public sealed class IdentityTests
{
    private static Dictionary<string, string?> ValidSettings() => new()
    {
        ["CIVICOPS_OIDC_PROVIDER"] = "generic", ["CIVICOPS_OIDC_AUTHORITY"] = "https://issuer.synthetic",
        ["CIVICOPS_OIDC_CLIENT_ID"] = "synthetic-client", ["CIVICOPS_OIDC_CLIENT_SECRET"] = "synthetic-confidential-client",
        ["CIVICOPS_OIDC_ROLE_MAP"] = "CouncilDispatch=dispatcher;CouncilSupervisor=supervisor",
        ["CIVICOPS_OIDC_GROUP_MAP"] = "AuditCouncil=auditor"
    };
    private static IdentityOptions Options(Dictionary<string, string?> settings) => IdentityOptions.From(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    [Fact] public void UnconfiguredIssuerIsHonestlyDisabled() => Assert.False(Options(new()).Configured);
    [Fact] public void BootstrapAndDemoSessionsRespectConfiguredLifetimeWithoutIssuer()
    { var options = Options(new() { ["CIVICOPS_OIDC_SESSION_MINUTES"] = "15" }); Assert.False(options.Configured); Assert.Equal(15, options.SessionMinutes); }
    [Theory] [InlineData("0")] [InlineData("1441")] [InlineData("not-minutes")]
    public void DisabledIssuerStillRejectsInvalidSessionLifetime(string value) =>
        Assert.Throws<InvalidOperationException>(() => Options(new() { ["CIVICOPS_OIDC_SESSION_MINUTES"] = value }));
    [Theory]
    [InlineData("CIVICOPS_OIDC_PROVIDER", "unexpected")]
    [InlineData("CIVICOPS_OIDC_AUTHORITY", "http://issuer.synthetic")]
    [InlineData("CIVICOPS_OIDC_AUTHORITY", "https://username:password@issuer.synthetic")]
    [InlineData("CIVICOPS_OIDC_AUTHORITY", "https://issuer.synthetic?tenant=default")]
    [InlineData("CIVICOPS_OIDC_CLIENT_ID", "")]
    [InlineData("CIVICOPS_OIDC_CLIENT_SECRET", "")]
    [InlineData("CIVICOPS_OIDC_ROLE_MAP", "CouncilDispatch=superuser")]
    [InlineData("CIVICOPS_OIDC_ROLE_MAP", "CouncilDispatch=admin;CouncilDispatch=resident")]
    [InlineData("CIVICOPS_OIDC_ROLE_MAP", "CouncilDispatch=admin,admin")]
    [InlineData("CIVICOPS_OIDC_ROLE_MAP", "CouncilDispatch")]
    [InlineData("CIVICOPS_OIDC_TENANT_ID", "../other")]
    [InlineData("CIVICOPS_OIDC_SESSION_MINUTES", "0")]
    [InlineData("CIVICOPS_OIDC_SESSION_MINUTES", "1441")]
    [InlineData("CIVICOPS_OIDC_HOSTED_DOMAIN", "example.invalid")]
    public void InvalidIssuerConfigurationFailsClosed(string key, string value)
    { var settings = ValidSettings(); settings[key] = value; Assert.Throws<InvalidOperationException>(() => Options(settings)); }
    [Fact] public void MissingExplicitRoleMapsFailsClosed()
    { var settings = ValidSettings(); settings["CIVICOPS_OIDC_ROLE_MAP"] = ""; settings["CIVICOPS_OIDC_GROUP_MAP"] = ""; Assert.Throws<InvalidOperationException>(() => Options(settings)); }
    [Fact] public void EntraPresetRequiresSpecificDirectory()
    { var settings = ValidSettings(); settings["CIVICOPS_OIDC_PROVIDER"] = "entra"; settings["CIVICOPS_OIDC_AUTHORITY"] = ""; settings["CIVICOPS_OIDC_DIRECTORY_ID"] = "common"; Assert.Throws<InvalidOperationException>(() => Options(settings)); }
    [Fact] public void EntraPresetBuildsTenantSpecificAuthority()
    {
        var settings = ValidSettings(); settings["CIVICOPS_OIDC_PROVIDER"] = "entra"; settings["CIVICOPS_OIDC_AUTHORITY"] = "";
        settings["CIVICOPS_OIDC_DIRECTORY_ID"] = "10000000-0000-0000-0000-000000000001";
        Assert.Equal("https://login.microsoftonline.com/10000000-0000-0000-0000-000000000001/v2.0", Options(settings).Authority);
    }
    [Fact] public void GoogleWorkspacePresetRestrictsHostedDomainClaim()
    {
        var settings = ValidSettings(); settings["CIVICOPS_OIDC_PROVIDER"] = "google"; settings["CIVICOPS_OIDC_AUTHORITY"] = "";
        settings["CIVICOPS_OIDC_HOSTED_DOMAIN"] = "council.synthetic"; var options = Options(settings);
        Assert.Equal("https://accounts.google.com", options.Authority);
        Assert.Throws<InvalidOperationException>(() => options.MapPrincipal(External()));
        var principal = External(new Claim("hd", "council.synthetic")); Assert.True(options.MapPrincipal(principal).IsInRole("dispatcher"));
    }
    [Fact] public void ExplicitClaimAndGroupMappingsProduceOnlyConfiguredLocalRoles()
    {
        var principal = Options(ValidSettings()).MapPrincipal(External(new("roles", "admin"), new("groups", "AuditCouncil"), new(ClaimTypes.Role, "admin")));
        Assert.True(principal.IsInRole("dispatcher")); Assert.True(principal.IsInRole("auditor")); Assert.False(principal.IsInRole("admin"));
        Assert.Equal("default", principal.FindFirst("civicops:tenant")?.Value);
        Assert.Equal("synthetic-staff-1", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Null(principal.FindFirst("access_token"));
    }
    [Fact] public void MissingOrAmbiguousMunicipalityClaimFailsClosed()
    {
        var options = Options(ValidSettings()); var external = External();
        ((ClaimsIdentity)external.Identity!).RemoveClaim(external.FindFirst("municipality_id")!);
        Assert.Throws<InvalidOperationException>(() => options.MapPrincipal(external));
        Assert.Throws<InvalidOperationException>(() => options.MapPrincipal(External(new Claim("municipality_id", "synthetic-coast"))));
    }
    [Fact] public void FixedConfiguredMunicipalityCannotBeOverriddenByIssuerClaim()
    {
        var settings = ValidSettings(); settings["CIVICOPS_OIDC_TENANT_ID"] = "synthetic-coast";
        Assert.Equal("synthetic-coast", Options(settings).MapPrincipal(External()).FindFirst("civicops:tenant")?.Value);
    }
    [Fact] public void UnmappedIdentityFailsClosed()
    {
        var principal = External(); var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst("roles")!); identity.AddClaim(new("roles", "admin"));
        Assert.Throws<InvalidOperationException>(() => Options(ValidSettings()).MapPrincipal(principal));
    }
    [Fact] public async Task SessionStoreHoldsClaimsServerSideAndRejectsExpiredOrRemovedSessions()
    {
        var store = new ServerSessionStore(TimeProvider.System);
        var key = await store.StoreAsync(new(External(), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1) }, EnterpriseIdentity.SessionScheme));
        Assert.DoesNotContain("synthetic-staff", key, StringComparison.Ordinal); Assert.NotNull(await store.RetrieveAsync(key));
        await store.RemoveAsync(key); Assert.Null(await store.RetrieveAsync(key));
        var expired = await store.StoreAsync(new(External(), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }, EnterpriseIdentity.SessionScheme));
        Assert.Null(await store.RetrieveAsync(expired));
    }
    [Fact] public void CrewMappingRequiresSingleExplicitCrewBinding()
    {
        var settings = ValidSettings(); settings["CIVICOPS_OIDC_ROLE_MAP"] = "CouncilDispatch=crew"; var options = Options(settings);
        Assert.Throws<InvalidOperationException>(() => options.MapPrincipal(External()));
        Assert.Throws<InvalidOperationException>(() => options.MapPrincipal(External(new Claim("crew_id", "crew-a"), new Claim("crew_id", "crew-b"))));
        var crew = options.MapPrincipal(External(new Claim("crew_id", "crew-a"))); Assert.True(crew.IsInRole("crew")); Assert.Equal("crew-a", crew.FindFirst("civicops:crew")?.Value);
    }
    private static ClaimsPrincipal External(params Claim[] additional) => new(new ClaimsIdentity([
        new("sub", "synthetic-staff-1"), new("name", "Synthetic Council Staff"), new("municipality_id", "default"), new("roles", "CouncilDispatch"), .. additional], "trusted-test-issuer"));
}
