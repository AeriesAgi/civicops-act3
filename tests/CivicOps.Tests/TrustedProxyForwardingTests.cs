using System.Net;
using CivicOps.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CivicOps.Tests;

public sealed class TrustedProxyForwardingTests
{
    [Theory]
    [InlineData(null, "10.20.0.2", false)]
    [InlineData("", "127.0.0.1", false)]
    [InlineData("10.20.0.0/24", "10.20.0.2", true)]
    [InlineData("10.20.0.2", "10.20.0.2", true)]
    [InlineData("10.20.0.0/24", "10.21.0.2", false)]
    [InlineData("2001:db8:1::/64", "2001:db8:1::2", true)]
    [InlineData("2001:db8:1::/64", "2001:db8:2::2", false)]
    [InlineData("::ffff:10.20.0.0/120", "::ffff:10.20.0.2", true)]
    public async Task OnlyExplicitTrustedProxyCanSetSchemeAndClientAddress(string? configured, string remote, bool trusted)
    {
        var context = Context(remote);
        await Middleware(configured).Invoke(context);
        Assert.Equal(trusted ? "https" : "http", context.Request.Scheme);
        Assert.Equal(trusted ? "192.0.2.44" : remote, context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("civicops.example.invalid", context.Request.Host.Value);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("::ffff:0.0.0.0/96")]
    [InlineData("::/80")]
    [InlineData("::/1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("10.20.0.0/33")]
    [InlineData("not-a-network")]
    [InlineData("10.20.0.2,")]
    public void InvalidOrUnrestrictedTrustConfigurationFailsClosed(string configured) =>
        Assert.Throws<InvalidOperationException>(() => TrustedProxyForwarding.CreateOptions(configured));

    [Fact]
    public void ExcessiveTrustEntriesFailClosed() => Assert.Throws<InvalidOperationException>(() =>
        TrustedProxyForwarding.CreateOptions(string.Join(',', Enumerable.Repeat("10.20.0.2", 17))));

    [Theory]
    [InlineData("", "https")]
    [InlineData("192.0.2.44", "")]
    [InlineData("192.0.2.44, 192.0.2.45", "https")]
    public async Task MissingOrAsymmetricHeadersCannotAlterTrustedRequest(string forwardedFor, string forwardedProto)
    {
        var context = Context("10.20.0.2");
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        await Middleware("10.20.0.2").Invoke(context);
        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal("10.20.0.2", context.Connection.RemoteIpAddress!.ToString());
    }

    [Fact]
    public async Task OnlyOneForwardingHopIsConsumed()
    {
        var context = Context("10.20.0.2");
        context.Request.Headers["X-Forwarded-For"] = "192.0.2.44, 10.20.0.3";
        context.Request.Headers["X-Forwarded-Proto"] = "https, http";
        await Middleware("10.20.0.0/24").Invoke(context);
        Assert.Equal("http", context.Request.Scheme);
        Assert.Equal("10.20.0.3", context.Connection.RemoteIpAddress!.ToString());
        Assert.Equal("192.0.2.44", context.Request.Headers["X-Forwarded-For"].ToString());
    }

    private static DefaultHttpContext Context(string remote)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Scheme = "http";
        context.Request.Host = new("civicops.example.invalid");
        context.Request.Headers["X-Forwarded-For"] = "192.0.2.44";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "spoofed.example.invalid";
        return context;
    }

    private static ForwardedHeadersMiddleware Middleware(string? configured) => new(
        _ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(TrustedProxyForwarding.CreateOptions(configured)));
}
