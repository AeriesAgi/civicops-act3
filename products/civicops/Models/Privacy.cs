namespace CivicOps.Models;

public sealed record ConsentReceipt(string Version, string Purpose, string Text, bool Accepted,
    DateTimeOffset RecordedAt, string Capture, string ContactChoice);
public sealed record ResidentContact(string TenantId, string IncidentId, string? Email, string? Phone);
public sealed record DataSubjectRequest(string Id, string TenantId, string IncidentId, string ReferenceNumber,
    string Action, string Status, string RequestNote, string? VerificationNote, string RequestedBy,
    DateTimeOffset RequestedAt, string? VerifiedBy = null, DateTimeOffset? VerifiedAt = null,
    string? FulfilledBy = null, DateTimeOffset? FulfilledAt = null);
public sealed record DataSubjectRequestCreate(string ReferenceNumber, string Action, string RequestNote);
public sealed record DataSubjectVerification(string VerificationNote);
public sealed record DataSubjectFulfilment(int? ExpectedVersion = null, string? Description = null,
    string? ContactEmail = null, string? ContactPhone = null, bool ConsentToContact = false);
