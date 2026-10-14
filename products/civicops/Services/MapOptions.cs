namespace CivicOps.Services;

/// <summary>Public, credential-free map configuration. One HTTPS tile origin is allowed by CSP.</summary>
public sealed class MapOptions
{
    public const string DefaultTileUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    public const string OpenStreetMapAttribution = "© OpenStreetMap contributors";
    public string TileUrl { get; private init; } = DefaultTileUrl;
    public string Attribution { get; private init; } = OpenStreetMapAttribution;
    public string? TileOrigin { get; private init; } = "https://tile.openstreetmap.org";
    public bool TileEnabled => TileOrigin is not null;
    public int CrewStaleMinutes { get; private init; } = 15;

    public static MapOptions From(IConfiguration config)
    {
        var tileUrl = string.IsNullOrWhiteSpace(config["CIVICOPS_MAP_TILE_URL"]) ? DefaultTileUrl : config["CIVICOPS_MAP_TILE_URL"]!.Trim();
        string? origin = null;
        if (!string.Equals(tileUrl, "none", StringComparison.OrdinalIgnoreCase))
        {
            var substituted = tileUrl.Replace("{z}", "0", StringComparison.Ordinal).Replace("{x}", "0", StringComparison.Ordinal).Replace("{y}", "0", StringComparison.Ordinal);
            if (tileUrl.Length > 512 || tileUrl.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '\\' or '\'' or '"' or '<' or '>' or ';')
                || !tileUrl.Contains("{z}", StringComparison.Ordinal) || !tileUrl.Contains("{x}", StringComparison.Ordinal) || !tileUrl.Contains("{y}", StringComparison.Ordinal)
                || substituted.Contains('{', StringComparison.Ordinal) || substituted.Contains('}', StringComparison.Ordinal)
                || !Uri.TryCreate(substituted, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
                || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidOperationException("CIVICOPS_MAP_TILE_URL must be none or one credential-free HTTPS URL containing {z}, {x} and {y}; subdomain templates, queries and fragments are not supported.");
            origin = uri.GetLeftPart(UriPartial.Authority);
        }
        else tileUrl = "none";
        var attribution = string.IsNullOrWhiteSpace(config["CIVICOPS_MAP_ATTRIBUTION"]) ? OpenStreetMapAttribution : config["CIVICOPS_MAP_ATTRIBUTION"]!.Trim();
        if (attribution.Length > 240 || attribution.Any(character => char.IsControl(character) || character is '<' or '>'))
            throw new InvalidOperationException("CIVICOPS_MAP_ATTRIBUTION must be at most 240 characters of plain text.");
        if (origin == "https://tile.openstreetmap.org" && !attribution.Contains(OpenStreetMapAttribution, StringComparison.Ordinal))
            attribution = OpenStreetMapAttribution + " · " + attribution;
        var staleMinutes = 15;
        if (!string.IsNullOrWhiteSpace(config["CIVICOPS_MAP_CREW_STALE_MINUTES"])
            && (!int.TryParse(config["CIVICOPS_MAP_CREW_STALE_MINUTES"], out staleMinutes) || staleMinutes is < 1 or > 240))
            throw new InvalidOperationException("CIVICOPS_MAP_CREW_STALE_MINUTES must be between 1 and 240.");
        return new() { TileUrl = tileUrl, TileOrigin = origin, Attribution = attribution, CrewStaleMinutes = staleMinutes };
    }
}
