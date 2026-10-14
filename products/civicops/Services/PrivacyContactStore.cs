using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

/// <summary>Optional contacts live separately from case narratives. Caller owns the incident-store lock.</summary>
public sealed class PrivacyContactStore(string dataDirectory, DurableStorage storage)
{
    private string PathName => Path.Combine(TenantContext.DirectoryFor(dataDirectory), "resident-contacts.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public async Task<ResidentContact?> GetAsync(string incidentId, CancellationToken ct) =>
        (await ReadAsync(ct)).FirstOrDefault(contact => contact.IncidentId == incidentId);
    public async Task<IReadOnlyDictionary<string, ResidentContact>> ReadAllAsync(CancellationToken ct) =>
        (await ReadAsync(ct)).ToDictionary(contact => contact.IncidentId, StringComparer.Ordinal);
    public async Task<string?> SaveAsync(Incident incident, CancellationToken ct)
    {
        var contacts = await ReadAsync(ct);
        contacts.RemoveAll(contact => contact.IncidentId == incident.Id);
        var email = incident.ConsentToContact ? incident.ContactEmail?.Trim() : null;
        var phone = incident.ConsentToContact ? incident.ContactPhone?.Trim() : null;
        ResidentContact? contact = string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone) ? null
            : new(TenantContext.CurrentId, incident.Id, email, phone);
        if (contact is not null) contacts.Add(contact);
        await storage.WriteAsync(PathName, JsonSerializer.Serialize(contacts, Json), ct);
        incident.ContactEmail = contact?.Email; incident.ContactPhone = contact?.Phone;
        return contact is null ? null : Hash(contact);
    }
    public async Task DeleteAsync(IEnumerable<string> incidentIds, CancellationToken ct)
    {
        var removed = incidentIds.ToHashSet(StringComparer.Ordinal);
        var contacts = await ReadAsync(ct);
        contacts.RemoveAll(contact => removed.Contains(contact.IncidentId));
        await storage.WriteAsync(PathName, JsonSerializer.Serialize(contacts, Json), ct);
    }
    public async Task PruneUnreferencedAsync(IReadOnlySet<string> activeIds, CancellationToken ct)
    {
        var contacts = await ReadAsync(ct);
        if (contacts.RemoveAll(contact => !activeIds.Contains(contact.IncidentId)) == 0) return;
        await storage.WriteAsync(PathName, JsonSerializer.Serialize(contacts, Json), ct);
    }
    public static string Hash(ResidentContact contact) => IncidentStore.Hash(JsonSerializer.Serialize(contact));
    private async Task<List<ResidentContact>> ReadAsync(CancellationToken ct)
    {
        var persisted = await storage.ReadAsync(PathName, ct);
        if (persisted is null) return [];
        var contacts = JsonSerializer.Deserialize<List<ResidentContact>>(persisted, Json)
            ?? throw new InvalidDataException("Invalid contact store.");
        if (contacts.Any(contact => contact.TenantId != TenantContext.CurrentId)
            || contacts.Select(contact => contact.IncidentId).Distinct(StringComparer.Ordinal).Count() != contacts.Count)
            throw new InvalidDataException("Contact partition municipality mismatch or duplicate record.");
        return contacts;
    }
}
