using System.Net;

namespace CivicOps.Tests;

public sealed class PublicLandingTests
{
    [Fact]
    public async Task HomePageCarriesSearchMetadataAndHonestDemoNotice()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var html = await client.GetStringAsync("/");
        Assert.Contains("<link href=\"https://civicops.culltron.app/\" rel=\"canonical\"/>", html, StringComparison.Ordinal);
        Assert.Contains("property=\"og:image\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"twitter:card\"", html, StringComparison.Ordinal);
        Assert.Contains("\"@type\":\"SoftwareApplication\"", html, StringComparison.Ordinal);
        Assert.Contains("synthetic data only", html, StringComparison.Ordinal);
        Assert.Contains("Culltron (Pty) Ltd", html, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<h1 id=\"landing-title\""));
        Assert.DoesNotContain("https://fonts.", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/robots.txt", "Sitemap: https://civicops.culltron.app/sitemap.xml")]
    [InlineData("/sitemap.xml", "<loc>https://civicops.culltron.app/</loc>")]
    public async Task CrawlerFilesAreServed(string path, string expected)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/media/hero-dispatchers-1600.webp")]
    [InlineData("/media/og-cover.png")]
    [InlineData("/media/culltron-logo-192.webp")]
    [InlineData("/media/culltron-mark-72.webp")]
    [InlineData("/media/favicon-32.png")]
    [InlineData("/media/apple-touch-icon.png")]
    [InlineData("/media/icon-192.png")]
    [InlineData("/media/icon-512.png")]
    public async Task LandingMediaIsSelfHosted(string path)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
