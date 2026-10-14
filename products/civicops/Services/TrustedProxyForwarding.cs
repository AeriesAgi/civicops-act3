using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace CivicOps.Services;

public static class TrustedProxyForwarding
{
    public static IServiceCollection AddCivicOpsForwarding(this IServiceCollection services, IConfiguration configuration)
    {
        // Parse now so an invalid trust boundary fails startup instead of failing on the first request.
        var settings = CreateOptions(configuration["CIVICOPS_TRUSTED_PROXY_NETWORKS"]);
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = settings.ForwardedHeaders;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in settings.KnownProxies) options.KnownProxies.Add(proxy);
            foreach (var network in settings.KnownIPNetworks) options.KnownIPNetworks.Add(network);
        });
        return services;
    }

    public static ForwardedHeadersOptions CreateOptions(string? configured)
    {
        var options = new ForwardedHeadersOptions { ForwardLimit = 1, RequireHeaderSymmetry = true };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        if (string.IsNullOrWhiteSpace(configured)) return options;
        var entries = configured.Split(',', StringSplitOptions.TrimEntries);
        if (entries.Length > 16) throw InvalidConfiguration();
        foreach (var entry in entries)
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                if (!System.Net.IPNetwork.TryParse(entry, out var network) || network.PrefixLength == 0
                    || CoversEveryMappedIpv4Address(network))
                    throw InvalidConfiguration();
                options.KnownIPNetworks.Add(network);
            }
            else
            {
                if (!IPAddress.TryParse(entry, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                    throw InvalidConfiguration();
                options.KnownProxies.Add(address);
            }
        }
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        return options;
    }

    private static InvalidOperationException InvalidConfiguration() => new(
        "CIVICOPS_TRUSTED_PROXY_NETWORKS requires at most 16 explicit IP addresses or CIDRs; empty entries and unrestricted networks are rejected.");

    private static bool CoversEveryMappedIpv4Address(System.Net.IPNetwork network)
    {
        if (network.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6
            || network.PrefixLength > 96) return false;
        // Compare the RFC 4291 mapped-address prefix directly. Framework Contains
        // special-cases mapped peers as IPv4, so it is unsuitable for this IPv6 range check.
        ReadOnlySpan<byte> mappedPrefix = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 255];
        var networkBytes = network.BaseAddress.GetAddressBytes();
        var completeBytes = network.PrefixLength / 8;
        if (!networkBytes.AsSpan(0, completeBytes).SequenceEqual(mappedPrefix[..completeBytes])) return false;
        var remainingBits = network.PrefixLength % 8;
        if (remainingBits == 0) return true;
        var mask = 255 << (8 - remainingBits);
        return (networkBytes[completeBytes] & mask) == (mappedPrefix[completeBytes] & mask);
    }
}
