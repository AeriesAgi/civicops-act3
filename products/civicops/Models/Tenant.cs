namespace CivicOps.Models;

public sealed record TenantLocaleDefaults(string Language = "en", string TimeZone = "Africa/Johannesburg");
public sealed record Tenant(string Id, string Name, string Slug, IReadOnlyList<string> Departments,
    TenantLocaleDefaults LocaleDefaults, string ServiceArea, bool Synthetic = true,
    string? ResidentHost = null, int Version = 1);
