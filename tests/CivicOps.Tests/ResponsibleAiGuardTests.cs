using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CivicOps.Models;
using CivicOps.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CivicOps.Tests;

/// <summary>Independent release guards for the municipal safety and privacy boundary.</summary>
public sealed class ResponsibleAiGuardTests
{
    [Fact]
    public void PublicModelsAndRequestResponseRecordsContainNoProhibitedIdentityOrPersonScoringFields()
    {
        var contracts = typeof(Incident).Assembly.GetExportedTypes().Where(IsContract).ToArray();
        Assert.Contains(typeof(IncidentRequest), contracts);
        Assert.Contains(typeof(ServiceAccountCreateRequest), contracts);
        Assert.Contains(typeof(MapHazardRequest), contracts);
        var violations = contracts.SelectMany(ContractViolations).ToArray();
        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void EveryPortableSchemaAndCommittedOpenApiContractExcludesProhibitedFields()
    {
        var schemas = Directory.GetFiles(Path.Combine(SchemaContract.RepositoryRoot, "docs", "schemas"), "*.json", SearchOption.AllDirectories);
        Assert.NotEmpty(schemas);
        var paths = schemas.Append(Path.Combine(SchemaContract.RepositoryRoot, "docs", "api", "openapi.json"));
        var violations = new List<string>();
        foreach (var path in paths)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            violations.AddRange(SchemaViolations(document.RootElement, Path.GetFileNameWithoutExtension(path))
                .Select(field => Path.GetRelativePath(SchemaContract.RepositoryRoot, path) + ": " + field));
        }
        Assert.Empty(violations);
    }

    [Fact]
    public void GeneratedOpenApiSchemaAlsoExcludesProhibitedFields()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();
        var endpoints = app.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        using var document = JsonDocument.Parse(OpenApiDocument.Create(endpoints).ToJsonString());
        Assert.Empty(SchemaViolations(document.RootElement, "OpenApi"));
    }

    [Fact]
    public void HazardEventAllowListIsReviewedAndCannotBeMutatedToAcceptIdentityDetection()
    {
        string[] reviewed = ["crash", "stalled-vehicle", "flooding", "fire", "smoke", "blocked-lane", "debris",
            "downed-line", "open-trench", "water", "burst-main", "road-blocked"];
        Assert.Equal(reviewed.Order(StringComparer.Ordinal), GeoPrivacyGuards.HazardEventTypes.Order(StringComparer.Ordinal));
        if (GeoPrivacyGuards.HazardEventTypes is ICollection<string> collection)
        {
            Assert.True(collection.IsReadOnly, "The safety allow-list must not expose a mutable collection.");
            Assert.Throws<NotSupportedException>(() => collection.Add("face-match"));
        }
        Assert.False(GeoPrivacyGuards.HazardEventTypes.Contains("face-match"));
    }

    [Theory]
    [InlineData("face-detection")]
    [InlineData("face-match")]
    [InlineData("biometric-identification")]
    [InlineData("gait-identification")]
    [InlineData("voiceprint-match")]
    [InlineData("licence-plate-person-lookup")]
    [InlineData("person-risk")]
    [InlineData("suspicious-person")]
    [InlineData("predicted-crime")]
    [InlineData("neighbourhood-crime-risk")]
    [InlineData("Fire")]
    [InlineData("fire ")]
    [InlineData("")]
    public void DetectorsCannotExtendTheObjectAndSituationAllowList(string eventType) =>
        Assert.False(GeoPrivacyGuards.HazardEventTypes.Contains(eventType));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void WorkerLocationRequiresEveryConsentConditionAndLiveIngestionRemainsDisabled(bool onShift, bool consent, bool indicator)
    {
        Assert.Equal(onShift && consent && indicator, GeoPrivacyGuards.WorkerConsentAllowsLocation(onShift, consent, indicator));
        var rejection = GeoPrivacyGuards.RejectLiveWorkerLocation(onShift, consent, indicator);
        Assert.False(string.IsNullOrWhiteSpace(rejection));
        Assert.Contains(onShift && consent && indicator ? "disabled" : "revocable consent", rejection, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Face")]
    [InlineData("face_embedding")]
    [InlineData("faceembeddinghash")]
    [InlineData("facialtemplate")]
    [InlineData("facialRecognitionTemplate")]
    [InlineData("BiometricTemplate")]
    [InlineData("biometrics")]
    [InlineData("GaitSignature")]
    [InlineData("voice_print")]
    [InlineData("voiceprintHash")]
    [InlineData("SpeakerEmbedding")]
    [InlineData("IrisTemplate")]
    [InlineData("retinaScan")]
    [InlineData("FingerprintHash")]
    [InlineData("fingerprinthash")]
    [InlineData("licencePlateToPerson")]
    [InlineData("license_plate_owner_id")]
    [InlineData("PersonRiskScore")]
    [InlineData("individualThreatScore")]
    [InlineData("residentCriminalityRating")]
    [InlineData("person_suspicion_flag")]
    [InlineData("WorkerPriorityScore")]
    [InlineData("neighbourhoodCrimePrediction")]
    [InlineData("neighborhood_crime_risk")]
    [InlineData("predictivePolicingScore")]
    public void FieldGuardRecognizesProhibitedNamesAcrossJsonNamingConventions(string field) =>
        Assert.True(Prohibited(field, "Fixture"), field);

    [Theory]
    [InlineData("Interface")]
    [InlineData("SurfaceArea")]
    [InlineData("RoadSurface")]
    [InlineData("IntakeFingerprint")]
    [InlineData("VoiceId")]
    [InlineData("TrainingScore")]
    [InlineData("IncidentPriorityScore")]
    [InlineData("HazardRiskScore")]
    [InlineData("WardFloodRisk")]
    [InlineData("ResidentLanguage")]
    [InlineData("CrewDistanceRank")]
    [InlineData("HumanApproval")]
    [InlineData("GaitersRequired")]
    public void FieldGuardPreservesNonBiometricAndIncidentOnlyContracts(string field) =>
        Assert.False(Prohibited(field, field == nameof(Incident.IntakeFingerprint) ? nameof(Incident) : "Fixture"), field);

    [Fact]
    public void GuardChecksActualJsonAliasesPublicFieldsAndPersonScoringContext()
    {
        Assert.Single(ContractViolations(typeof(ForbiddenJsonAliasFixture)));
        Assert.Single(ContractViolations(typeof(ForbiddenPublicFieldFixture)));
        Assert.Single(ContractViolations(typeof(PersonScoringFixture)));
        Assert.True(Prohibited("RiskScore", "WorkerResponse"));
        Assert.True(Prohibited("ThreatRating", "ResidentResponse"));
        Assert.True(Prohibited("Probability", "NeighbourhoodCrimePredictionResponse"));
        Assert.True(Prohibited("IntakeFingerprint", "BiometricResponse"));
        Assert.True(Prohibited("Payload", "FaceIdentificationRequest"));
        Assert.True(Prohibited("Payload", "BiometricResponse"));
        Assert.False(Prohibited("Score", "TrainingCompletionRequest"));
    }

    [Fact]
    public void SchemaGuardTraversesNestedDefinitionsArraysAndRequiredPropertyNames()
    {
        const string schema = """
            {"$defs":{"Camera":{"properties":{"observations":{"type":"array","items":{"type":"object","properties":{"face_embedding":{"type":"string"}}}}}}},
             "definitions":{"PersonThreat":{"properties":{"rating":{"type":"integer"}}}},
             "required":["biometric_template"]}
            """;
        using var document = JsonDocument.Parse(schema);
        var violations = SchemaViolations(document.RootElement, "Fixture").ToArray();
        Assert.Equal(3, violations.Length);
        Assert.Contains(violations, field => field.EndsWith("face_embedding", StringComparison.Ordinal));
        Assert.Contains(violations, field => field.EndsWith("rating", StringComparison.Ordinal));
        Assert.Contains(violations, field => field.EndsWith("biometric_template", StringComparison.Ordinal));
    }

    private sealed record ForbiddenJsonAliasFixture([property: JsonPropertyName("face_embedding")] string Payload);
    private sealed record PersonScoringFixture(int RiskScore);
    private sealed class ForbiddenPublicFieldFixture
    {
        [JsonPropertyName("voice_print")]
        public const string Payload = "synthetic-prohibited-contract-fixture";
    }

    private static bool IsContract(Type type) => !type.IsEnum &&
        (type.Namespace is "CivicOps.Models" || type.Namespace?.StartsWith("CivicOps.Models.", StringComparison.Ordinal) is true
         || type.Name.Contains("Request", StringComparison.Ordinal) || type.Name.Contains("Response", StringComparison.Ordinal)
         || type.Name.EndsWith("Dto", StringComparison.OrdinalIgnoreCase)
         || type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null);

    private static IEnumerable<string> ContractViolations(Type type)
    {
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(member => member is PropertyInfo or FieldInfo);
        foreach (var member in members)
        {
            foreach (var name in new[] { member.Name, member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name }.OfType<string>().Distinct(StringComparer.Ordinal))
                if (Prohibited(name, type.Name)) yield return type.FullName + "." + name;
        }
    }

    private static IEnumerable<string> SchemaViolations(JsonElement node, string context, string path = "$")
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
                foreach (var violation in SchemaViolations(child, context, path + "[]")) yield return violation;
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                var childPath = path + "." + property.Name;
                if (property.Name is "properties" or "patternProperties" && property.Value.ValueKind == JsonValueKind.Object)
                    foreach (var field in property.Value.EnumerateObject())
                        if (Prohibited(field.Name, context)) yield return childPath + "." + field.Name;
                if (property.Name == "required" && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var field in property.Value.EnumerateArray().Where(field => field.ValueKind == JsonValueKind.String))
                        if (Prohibited(field.GetString()!, context)) yield return childPath + "." + field.GetString();
                if (property.Name is "schemas" or "definitions" or "$defs" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var definition in property.Value.EnumerateObject())
                        foreach (var violation in SchemaViolations(definition.Value, definition.Name, childPath + "." + definition.Name)) yield return violation;
                }
                else foreach (var violation in SchemaViolations(property.Value, context, childPath)) yield return violation;
            }
        }
    }

    private static bool Prohibited(string field, string context)
    {
        var tokens = Tokens(field);
        var compact = new string(field.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        // This exact existing field is a request-deduplication digest, not an anatomical identifier.
        if (compact == "intakefingerprint" && context == nameof(Incident)) return false;
        var combined = tokens.Concat(Tokens(context)).ToHashSet(StringComparer.Ordinal);
        if (combined.Any(token => token is "face" or "faces" or "facial" or "gait"
            || token.StartsWith("biometric", StringComparison.Ordinal) || token.StartsWith("gaitsignature", StringComparison.Ordinal)
            || token.StartsWith("gaitembedding", StringComparison.Ordinal) || token.StartsWith("gaitrecognition", StringComparison.Ordinal)
            || token.StartsWith("gaitidentification", StringComparison.Ordinal) || token.StartsWith("gaitanalysis", StringComparison.Ordinal)
            || token.StartsWith("iris", StringComparison.Ordinal) || token.StartsWith("retina", StringComparison.Ordinal)
            || token.StartsWith("voiceprint", StringComparison.Ordinal) || token.StartsWith("fingerprint", StringComparison.Ordinal)
            || token.StartsWith("facial", StringComparison.Ordinal)
            || token.StartsWith("faceembedding", StringComparison.Ordinal) || token.StartsWith("facerecognition", StringComparison.Ordinal)
            || token.StartsWith("facedetection", StringComparison.Ordinal) || token.StartsWith("facematch", StringComparison.Ordinal)
            || token.StartsWith("faceprint", StringComparison.Ordinal) || token.StartsWith("facetemplate", StringComparison.Ordinal))) return true;
        if (tokens.Overlaps(["voice", "speaker"]) && tokens.Overlaps(["print", "embedding", "fingerprint", "identity", "identification", "recognition", "verification", "match", "signature", "template"])) return true;
        if (combined.Contains("plate") && combined.Overlaps(["person", "people", "individual", "owner", "identity"])) return true;
        if (tokens.Overlaps(["criminality", "suspicion", "suspicious"])) return true;
        if (combined.Contains("policing") && combined.Overlaps(["predictive", "prediction", "predicted"])) return true;
        if (combined.Overlaps(["person", "people", "individual", "resident", "citizen", "worker", "suspect", "offender", "human"])
            && combined.Overlaps(["risk", "threat", "criminality", "suspicion", "suspicious", "priority"])) return true;
        return combined.Overlaps(["neighbourhood", "neighborhood", "ward", "district", "community", "precinct"])
            && combined.Overlaps(["crime", "criminal"]) && combined.Overlaps(["risk", "score", "rank", "priority", "forecast", "probability", "predictive", "prediction", "predicted"]);
    }

    private static HashSet<string> Tokens(string value)
    {
        var text = new StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsLetterOrDigit(character)) { text.Append(' '); continue; }
            if (char.IsUpper(character) && index > 0 && (char.IsLower(value[index - 1]) || char.IsDigit(value[index - 1])
                || char.IsUpper(value[index - 1]) && index + 1 < value.Length && char.IsLower(value[index + 1]))) text.Append(' ');
            text.Append(char.ToLowerInvariant(character));
        }
        return text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }
}
