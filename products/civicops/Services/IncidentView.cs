using CivicOps.Models;
namespace CivicOps.Services;

public static class IncidentView
{
    // Public lookup deliberately contains no description, contact, location, transcript, actors or internal notes.
    public static object Public(Incident incident) => new
    {
        incident.Id, incident.ReferenceNumber, incident.CreatedAt, incident.UpdatedAt, incident.Version,
        incident.Category, incident.Department, incident.Priority, incident.Status,
        workOrderTypeCode = TypeCode(incident),
        publicUpdate = SafeUpdate(incident.Status, incident.ResidentLanguage), residentLanguage = ResidentMessages.Normalize(incident.ResidentLanguage), incident.NeedsHumanReview, locationVerified = false
    };
    public static object Dispatcher(Incident incident) => new
    {
        incident.Id, incident.ReferenceNumber, incident.CreatedAt, incident.UpdatedAt, incident.Version,
        incident.Source, incident.SourceLabel, incident.Description, incident.Area, incident.NormalizedArea,
        incident.LocationProvenance, incident.Category, incident.Department, incident.Priority, incident.Status,
        location = LocationVisible(incident) ? incident.Location : null, locationShared = LocationVisible(incident) && incident.LocationShared,
        municipalityId = LocationVisible(incident) || incident.Location is null ? incident.MunicipalityId : null,
        wardId = LocationVisible(incident) || incident.Location is null ? incident.WardId : null, incident.Synthetic,
        workOrderTypeCode = TypeCode(incident), workOrderType = WorkOrderCatalog.Default.Find(TypeCode(incident)),
        incident.PublicUpdate, incident.NeedsHumanReview, incident.DuplicateOf, incident.DuplicateConfidence,
        incident.Approval, incident.AssignedCrewId, incident.AssignedCrewName, incident.Audit,
        crewReport = incident.CrewWork is null ? null : new
        {
            incident.CrewWork.Status, noteCount = incident.CrewWork.Notes.Count, photoCount = incident.CrewWork.Photos.Count,
            reportedAt = incident.Audit.LastOrDefault(entry => entry.Action == "crew.job-status")?.At,
            reportedBy = incident.Audit.LastOrDefault(entry => entry.Action == "crew.job-status")?.Actor
        },
        incident.ResidentLanguage, incident.IntakeSummary, incident.Hazards, incident.ContactPreference,
        incident.ConsentToContact, incident.CallbackConsent, incident.ConversationId, incident.TranscriptSummary,
        incident.AgentRuns, incident.CommsDrafts, incident.RecommendedCrewId, incident.RecommendedCrewReason, incident.ZendeskTicketId, incident.ZendeskTicketUrl,
        slaDueAt = incident.CreatedAt.AddMinutes(WorkOrderCatalog.Default.Find(TypeCode(incident))?.SlaTargetMinutes ?? 1440)
    };
    private static string TypeCode(Incident incident) => incident.WorkOrderTypeCode ?? new DeterministicClassifier().Classify(incident.Description).WorkOrderTypeCode;
    private static bool LocationVisible(Incident incident) => incident.Location is not null && incident.LocationShared
        && incident.CreatedAt >= DateTimeOffset.UtcNow - GeoStore.ReportLocationRetention;
    public static string SafeUpdate(IncidentStatus status, string? language = null) => ResidentMessages.Status(status, language);
}
