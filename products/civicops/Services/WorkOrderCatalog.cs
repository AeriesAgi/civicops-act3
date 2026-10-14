using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CivicOps.Models;

namespace CivicOps.Services;

/// <summary>Versioned shared contract for intake, dispatcher assignment and training layers.</summary>
public sealed class WorkOrderCatalog
{
    private static readonly JsonSerializerOptions CatalogJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static readonly Lazy<WorkOrderCatalog> BuiltIn = new(() => Load(FindPath()));
    private static readonly AsyncLocal<WorkOrderCatalog?> Active = new();
    public static WorkOrderCatalog Default => Active.Value ?? BuiltIn.Value;
    public static WorkOrderCatalog Baseline => BuiltIn.Value;
    private bool IsBaseline => BuiltIn.IsValueCreated && ReferenceEquals(this, BuiltIn.Value);
    private WorkOrderCatalog Current => IsBaseline && Active.Value is WorkOrderCatalog active && !ReferenceEquals(active, this) ? active : this;
    private readonly string _version;
    private readonly string _etag;
    private readonly IReadOnlyList<WorkOrderType> _types;
    private readonly IReadOnlyList<Skill> _skills;
    private readonly IReadOnlyList<CertificationType> _certifications;
    public string Version => ReferenceEquals(Current, this) ? _version : Current.Version;
    public string ETag => ReferenceEquals(Current, this) ? _etag : Current.ETag;
    public IReadOnlyList<WorkOrderType> WorkOrderTypes => ReferenceEquals(Current, this) ? _types : Current.WorkOrderTypes;
    public IReadOnlyList<Skill> Skills => ReferenceEquals(Current, this) ? _skills : Current.Skills;
    public IReadOnlyList<CertificationType> CertificationTypes => ReferenceEquals(Current, this) ? _certifications : Current.CertificationTypes;
    public WorkOrderCatalog(string version, IReadOnlyList<WorkOrderType> workOrderTypes,
        IReadOnlyList<Skill> skills, IReadOnlyList<CertificationType> certificationTypes)
    {
        _version = version; _types = workOrderTypes; _skills = skills; _certifications = certificationTypes;
        Validate();
        _etag = "\"" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { Version, WorkOrderTypes, Skills, CertificationTypes })))) + "\"";
    }
    public WorkOrderType? Find(string? code) => WorkOrderTypes.FirstOrDefault(type => type.Code == code);
    public WorkOrderType ForService(string code) => Find(code) ?? WorkOrderTypes.First(type => type.ServiceCode == code);
    public static WorkOrderCatalog Load(string path)
    {
        return FromJson(File.ReadAllText(path));
    }
    public static WorkOrderCatalog FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        ValidateJsonKeys(document.RootElement);
        var snapshot = document.RootElement.Deserialize<CatalogSnapshot>(
            CatalogJson)
            ?? throw new InvalidDataException("Catalogue must contain an object.");
        return new(snapshot.Version, snapshot.WorkOrderTypes, snapshot.Skills, snapshot.CertificationTypes);
    }
    public static IDisposable Use(WorkOrderCatalog catalog)
    {
        var previous = Active.Value; Active.Value = catalog; return new ActiveLease(previous);
    }
    private sealed class ActiveLease(WorkOrderCatalog? previous) : IDisposable
    { public void Dispose() => Active.Value = previous; }
    private static void ValidateJsonKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate catalogue JSON property.");
                ValidateJsonKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateJsonKeys(item);
    }
    private sealed record CatalogSnapshot(string Version, List<WorkOrderType> WorkOrderTypes,
        List<Skill> Skills, List<CertificationType> CertificationTypes);
    private static string FindPath()
    {
        foreach (var path in new[] { Path.Combine(AppContext.BaseDirectory, "catalog", "work-orders.v1.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "catalog", "work-orders.v1.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "products", "civicops", "catalog", "work-orders.v1.json") })
            if (File.Exists(path)) return path;
        throw new InvalidDataException("Versioned work-order catalogue is missing.");
    }
    private void Validate()
    {
        static bool Text(string? text, int max = 300) => !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl);
        static bool Code(string? text) => text is { Length: <= 80 } && Regex.IsMatch(text, @"^[A-Z][A-Z0-9_]*(?:\.[A-Z][A-Z0-9_]*)+$", RegexOptions.CultureInvariant);
        static void Unique(IEnumerable<string> codes) { if (codes.Distinct(StringComparer.Ordinal).Count() != codes.Count()) throw new InvalidDataException("Catalogue codes must be unique."); }
        if (Version is null || !Regex.IsMatch(Version, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)
            || WorkOrderTypes is null || Skills is null || CertificationTypes is null
            || WorkOrderTypes.Count is < 1 or > 500 || Skills.Count is < 1 or > 500 || CertificationTypes.Count is < 1 or > 500)
            throw new InvalidDataException("Invalid catalogue version or collections.");
        if (WorkOrderTypes.Any(item => item is null) || Skills.Any(item => item is null) || CertificationTypes.Any(item => item is null))
            throw new InvalidDataException("Catalogue entries cannot be null.");
        Unique(WorkOrderTypes.Select(item => item.Code)); Unique(Skills.Select(item => item.Code)); Unique(CertificationTypes.Select(item => item.Code));
        foreach (var skill in Skills)
            if (!Code(skill.Code) || !Text(skill.Name) || !Text(skill.Discipline)) throw new InvalidDataException("Invalid skill.");
        foreach (var cert in CertificationTypes)
            if (!Code(cert.Code) || !Text(cert.Name) || cert.ValidityMonths is < 1 or > 120
                || cert.IssuingBasis is not ("classroom" or "field-assessment" or "simulation" or "external")) throw new InvalidDataException("Invalid certification type.");
        foreach (var type in WorkOrderTypes)
        {
            if (!Code(type.Code) || !Text(type.Title) || !Text(type.Description, 1000) || !Text(type.Category)
                || !Text(type.Department) || !Text(type.ServiceCode, 40) || type.SlaTargetMinutes is < 1 or > 43200
                || type.HazardClass is not ("low" or "moderate" or "high" or "critical")
                || type.RequiredSkills is null || type.RequiredCertifications is null || type.Ppe is null || type.ProcedureSteps is null
                || type.RequiredSkills.Count is < 1 or > 500 || type.RequiredCertifications.Count > 500
                || type.Ppe.Count is < 1 or > 500 || type.ProcedureSteps.Count is < 1 or > 500
                || type.RequiredSkills.Any(required => required is null || Skills.All(skill => skill.Code != required.Code) || required.MinimumLevel is < 1 or > 5)
                || type.RequiredCertifications.Any(code => CertificationTypes.All(cert => cert.Code != code))
                || type.Ppe.Any(item => !Text(item)) || type.ProcedureSteps.Any(item => !Text(item, 1000)))
                throw new InvalidDataException($"Invalid work-order type or missing catalogue reference: {type.Code}.");
            Unique(type.RequiredSkills.Select(item => item.Code)); Unique(type.RequiredCertifications);
        }
        if (WorkOrderTypes.GroupBy(type => type.ServiceCode).Any(group => group.Select(type => (type.Category, type.Department)).Distinct().Count() > 1))
            throw new InvalidDataException("Service codes must map to one category and department.");
    }
}
