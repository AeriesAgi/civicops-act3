using CivicOps.Models;

namespace CivicOps.Services;

public sealed class CrewEligibilityService
{
    private readonly WorkforceStore _workforce;
    private readonly WorkOrderCatalog _catalog;
    private readonly TimeProvider _time;
    public string CertificationPolicy { get; }
    public CrewEligibilityService(WorkforceStore workforce, WorkOrderCatalog catalog, TimeProvider time, IConfiguration configuration)
    {
        _workforce = workforce; _catalog = catalog; _time = time;
        var configured = configuration["CIVICOPS_CERT_POLICY"];
        CertificationPolicy = string.IsNullOrWhiteSpace(configured)
            ? bool.TryParse(configuration["CIVICOPS_DEMO_MODE"], out var demo) && demo ? "warn" : "block"
            : configured.ToLowerInvariant();
        if (CertificationPolicy is not ("warn" or "block")) throw new InvalidDataException("CIVICOPS_CERT_POLICY must be warn or block.");
    }
    public async Task<IReadOnlyList<CrewEligibility>> ListAsync(Incident incident, CancellationToken ct)
    {
        var crews = await _workforce.ListCrewsAsync(ct); var workers = await _workforce.ListWorkersAsync(ct);
        var type = _catalog.Find(incident.WorkOrderTypeCode) ?? _catalog.Find(new DeterministicClassifier(_catalog).Classify(incident.Description).WorkOrderTypeCode)!;
        return crews.Select(crew => Evaluate(type, crew, workers, _time.GetUtcNow())).ToArray();
    }
    public async Task<CrewEligibility?> EvaluateAsync(Incident incident, string crewId, CancellationToken ct) =>
        (await ListAsync(incident, ct)).FirstOrDefault(value => value.Crew.Id.Equals(crewId, StringComparison.OrdinalIgnoreCase));
    public CrewEligibility Evaluate(WorkOrderType type, Crew crew, IReadOnlyList<Worker> workers, DateTimeOffset now)
    {
        var members = workers.Where(worker => worker.CrewId == crew.Id && (crew.MemberIds?.Contains(worker.Id, StringComparer.Ordinal) ?? true)).ToArray();
        var reasons = new List<string>(); var blocked = false; var certGap = false;
        if (crew.Department != type.Department) { reasons.Add("Department does not match " + type.Department + "."); blocked = true; }
        if (crew.Availability != "available") { reasons.Add("Crew is " + crew.Availability + " (shift: " + crew.Shift + ")."); blocked = true; }
        if (members.Length == 0) { reasons.Add("Crew has no available member qualifications."); blocked = true; }
        foreach (var required in type.RequiredSkills)
        {
            var holder = members.FirstOrDefault(worker => worker.Skills.Any(skill => skill.Code == required.Code && skill.Level >= required.MinimumLevel));
            if (holder is null) { reasons.Add($"Missing skill {required.Code} at level {required.MinimumLevel}."); blocked = true; }
            else reasons.Add($"{required.Code} level {required.MinimumLevel} met by {holder.DisplayName}.");
        }
        foreach (var required in type.RequiredCertifications)
        {
            var valid = members.SelectMany(worker => worker.Certifications).Where(cert => cert.Code == required && cert.IssuedAt <= now && cert.ExpiresAt > now).OrderByDescending(cert => cert.ExpiresAt).FirstOrDefault();
            if (valid is null)
            {
                var expired = members.SelectMany(worker => worker.Certifications).Any(cert => cert.Code == required && cert.ExpiresAt <= now);
                reasons.Add((expired ? "Expired certification " : "Missing certification ") + required + "."); certGap = true;
            }
            else reasons.Add($"{required} valid until {valid.ExpiresAt:yyyy-MM-dd} ({valid.Source}).");
        }
        if (certGap && CertificationPolicy == "block") blocked = true;
        if (certGap) reasons.Add("Certification policy: " + CertificationPolicy + "; dispatcher must review qualifications.");
        return new(crew, blocked ? "blocked" : certGap ? "warned" : "eligible", !blocked, reasons, members);
    }
}
