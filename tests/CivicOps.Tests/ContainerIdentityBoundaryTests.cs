using System.Security.Cryptography;
using CivicOps.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

public sealed class ContainerIdentityBoundaryTests
{
    [Fact]
    public void CookieAndCsrfProtectionWorksInProcessAndExpiresAcrossProcessBoundaries()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CIVICOPS_DEMO_MODE"] = "true" }).Build();
        var firstServices = new ServiceCollection();
        firstServices.AddLogging(); firstServices.AddCivicOpsIdentity(configuration);
        using var first = firstServices.BuildServiceProvider();
        var protector = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("synthetic-cookie-and-csrf");
        var protectedValue = protector.Protect("synthetic-opaque-session-reference");
        Assert.Equal("synthetic-opaque-session-reference", protector.Unprotect(protectedValue));

        var secondServices = new ServiceCollection();
        secondServices.AddLogging(); secondServices.AddCivicOpsIdentity(configuration);
        using var second = secondServices.BuildServiceProvider();
        var restarted = second.GetRequiredService<IDataProtectionProvider>().CreateProtector("synthetic-cookie-and-csrf");
        Assert.Throws<CryptographicException>(() => restarted.Unprotect(protectedValue));
    }
}
