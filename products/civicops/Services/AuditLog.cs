using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CivicOps.Models;

namespace CivicOps.Services;

public sealed record AuditRecord(long Sequence, string CaseId, string ReferenceNumber, string Action,
    string Actor, string Detail, DateTimeOffset At, string PreviousHash, string StateHash, string Hash,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? TenantId = null);
public sealed record AuditVerification(bool Valid, int Entries, long? FailedSequence, string Message);

public static class AuditLog
{
    public const string Genesis = "GENESIS";
    public static AuditRecord Append(IReadOnlyList<AuditRecord> records, Incident incident, AuditEvent entry)
    {
        var previous = records.Count == 0 ? Genesis : records[^1].Hash;
        var record = new AuditRecord(records.Count + 1L, incident.Id, incident.ReferenceNumber, entry.Action,
            entry.Actor, entry.Detail, entry.At, previous, StateHash(incident), "", incident.TenantId);
        return record with { Hash = Compute(record) };
    }
    public static AuditVerification Verify(IReadOnlyList<AuditRecord> records)
    {
        var previous = Genesis;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (r.Sequence != i + 1L || r.PreviousHash != previous || r.Hash != Compute(r))
                return new(false, records.Count, r.Sequence, "Audit chain integrity check failed.");
            previous = r.Hash;
        }
        return new(true, records.Count, null, "Audit chain verified. External anchoring is not configured.");
    }
    public static AuditVerification VerifySnapshot(IReadOnlyList<AuditRecord> records, IReadOnlyList<Incident> incidents)
    {
        var chain = Verify(records);
        if (!chain.Valid) return chain;
        foreach (var incident in incidents)
        {
            var last = records.LastOrDefault(record => record.CaseId == incident.Id);
            if (last is null || last.StateHash != StateHash(incident))
                return new(false, records.Count, last?.Sequence, "Case snapshot differs from its last authenticated audit state.");
        }
        return chain;
    }
    public static string StateHash(Incident incident) => Hash(SerializeState(incident));
    public static string SerializeState(Incident incident)
    {
        if (!incident.PrivacySeparated) return JsonSerializer.Serialize(incident);
        var state = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(incident))!;
        state[nameof(incident.ContactEmail)] = null;
        state[nameof(incident.ContactPhone)] = null;
        return state.ToJsonString();
    }
    private static string Compute(AuditRecord r)
    {
        var fields = new Dictionary<string, object?>
        {
            [nameof(r.Sequence)] = r.Sequence, [nameof(r.CaseId)] = r.CaseId, [nameof(r.ReferenceNumber)] = r.ReferenceNumber,
            [nameof(r.Action)] = r.Action, [nameof(r.Actor)] = r.Actor, [nameof(r.Detail)] = r.Detail, [nameof(r.At)] = r.At,
            [nameof(r.PreviousHash)] = r.PreviousHash, [nameof(r.StateHash)] = r.StateHash
        };
        if (r.TenantId is not null) fields[nameof(r.TenantId)] = r.TenantId;
        return Hash(JsonSerializer.Serialize(fields));
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
