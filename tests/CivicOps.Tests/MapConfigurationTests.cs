using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.Configuration;

namespace CivicOps.Tests;

public sealed class MapConfigurationTests
{
    private static MapOptions Options(string? url = null, string? attribution = null, string? stale = null) =>
        MapOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CIVICOPS_MAP_TILE_URL"] = url, ["CIVICOPS_MAP_ATTRIBUTION"] = attribution, ["CIVICOPS_MAP_CREW_STALE_MINUTES"] = stale
        }).Build());

    [Fact]
    public void ZeroConfigurationUsesExactOpenStreetMapUrlAndRequiredAttribution()
    {
        var options = Options();
        Assert.Equal(MapOptions.DefaultTileUrl, options.TileUrl);
        Assert.Equal(MapOptions.OpenStreetMapAttribution, options.Attribution);
        Assert.Equal("https://tile.openstreetmap.org", options.TileOrigin);
        Assert.True(options.TileEnabled); Assert.Equal(15, options.CrewStaleMinutes);
        Assert.Contains(MapOptions.OpenStreetMapAttribution, Options(MapOptions.DefaultTileUrl, "Synthetic overlay").Attribution);
    }

    [Fact]
    public void OfflineModePermitsNoTileOrigin()
    {
        var options = Options("none"); Assert.False(options.TileEnabled); Assert.Null(options.TileOrigin);
        var policy = SecurityHeaders.ContentSecurityPolicy(new ElevenLabsOptions(), "synthetic-nonce", options);
        Assert.Contains("img-src 'self';", policy);
        Assert.DoesNotContain("https:", policy);
    }

    [Fact]
    public void CustomTileHostIsAllowedOnlyAsAnImageOrigin()
    {
        var options = Options("https://maps.example.invalid/tiles/{z}/{x}/{y}.png", "Synthetic cartography", "25");
        var policy = SecurityHeaders.ContentSecurityPolicy(new ElevenLabsOptions(), "synthetic-nonce", options);
        Assert.Equal(25, options.CrewStaleMinutes);
        Assert.Contains("img-src 'self' https://maps.example.invalid;", policy);
        Assert.Contains("connect-src 'self';", policy);
        Assert.Contains("style-src-attr 'none'", policy);
        Assert.DoesNotContain("unsafe-inline", policy); Assert.DoesNotContain("*", policy);
        Assert.Equal(1, policy.Split("https://maps.example.invalid", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("http://tiles.example.invalid/{z}/{x}/{y}.png")]
    [InlineData("https://tiles.example.invalid:444/{z}/{x}/{y}.png")]
    [InlineData("https://user:synthetic-sentinel@tiles.example.invalid/{z}/{x}/{y}.png")]
    [InlineData("https://tiles.example.invalid/{z}/{x}/{y}.png?key=synthetic-sentinel")]
    [InlineData("https://tiles.example.invalid/{z}/{x}/{y}.png#synthetic-sentinel")]
    [InlineData("https://{s}.tiles.example.invalid/{z}/{x}/{y}.png")]
    [InlineData("https://*.tiles.example.invalid/{z}/{x}/{y}.png")]
    [InlineData("https://tiles.example.invalid/{z}/{x}/{y}.png; https://attacker.invalid")]
    [InlineData("https://tiles.example.invalid/{z}/{x}/{y}.png\nhttps://attacker.invalid")]
    [InlineData("https://tiles.example.invalid/no-template.png")]
    [InlineData("data:image/png;synthetic")]
    public void UntrustedTileConfigurationCannotInjectOriginsOrExposeCredentials(string url) =>
        Assert.Throws<InvalidOperationException>(() => Options(url));

    [Theory]
    [InlineData("<img src=\"synthetic\">")]
    [InlineData("Synthetic\nattribution")]
    public void AttributionIsBoundedPlainText(string attribution) => Assert.Throws<InvalidOperationException>(() => Options("none", attribution));

    [Theory]
    [InlineData("0")][InlineData("241")][InlineData("invalid")]
    public void CrewFreshnessThresholdIsBounded(string minutes) => Assert.Throws<InvalidOperationException>(() => Options("none", stale: minutes));
}
