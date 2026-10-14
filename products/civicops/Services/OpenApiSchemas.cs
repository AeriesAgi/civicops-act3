using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CivicOps.Services;

/// <summary>Deterministic JSON Schema registry; model properties use the application's JSON naming and enum rules.</summary>
internal sealed class OpenApiSchemas
{
    internal JsonObject Definitions { get; } = new();
    private readonly NullabilityInfoContext _nullability = new();
    internal static JsonObject String(string? format = null) => format is null ? new() { ["type"] = "string" } : new() { ["type"] = "string", ["format"] = format };
    internal static JsonObject Boolean() => new() { ["type"] = "boolean" };
    internal static JsonObject Integer() => new() { ["type"] = "integer" };
    internal static JsonObject Number() => new() { ["type"] = "number" };
    internal static JsonObject Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };
    internal static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items.DeepClone() };
    internal static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema.DeepClone(), new JsonObject { ["type"] = "null" }) };
    internal static JsonObject Object(params (string Name, JsonObject Schema)[] fields) => new()
    {
        ["type"] = "object", ["properties"] = new JsonObject(fields.Select(field => KeyValuePair.Create<string, JsonNode?>(field.Name, field.Schema.DeepClone()))),
        ["required"] = new JsonArray(fields.Select(field => (JsonNode)JsonValue.Create(field.Name)!).ToArray()), ["additionalProperties"] = false
    };
    internal JsonObject Add(string name, JsonObject definition)
    {
        definition["description"] ??= "CivicOps " + name + " contract. Examples contain synthetic values only.";
        Definitions[name] = definition;
        return Ref(name);
    }
    internal JsonObject Projection(string name, object projection) => Add(name, Properties(projection.GetType()));
    internal JsonObject Type(Type type)
    {
        if (System.Nullable.GetUnderlyingType(type) is Type underlying) return Nullable(Type(underlying));
        if (type == typeof(string)) return String();
        if (type == typeof(DateTimeOffset) || type == typeof(DateTime)) return String("date-time");
        if (type == typeof(Guid)) return String("uuid");
        if (type == typeof(bool)) return Boolean();
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return Integer();
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return Number();
        if (type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type) || type == typeof(object))
            return new() { ["type"] = "object", ["additionalProperties"] = true, ["description"] = "Extensible JSON object; endpoint-specific validation and bounds still apply." };
        if (type.IsEnum) return new() { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(type).Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()) };
        if (type.IsArray) return Array(Type(type.GetElementType()!));
        var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(value => value.IsGenericType && value.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        if (dictionary is not null) return new() { ["type"] = "object", ["additionalProperties"] = Type(dictionary.GetGenericArguments()[1]) };
        var sequence = type.GetInterfaces().Append(type).FirstOrDefault(value => value.IsGenericType && value.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (sequence is not null) return Array(Type(sequence.GetGenericArguments()[0]));
        if (type.Name.Contains("AnonymousType", StringComparison.Ordinal)) return Properties(type);
        var name = type.Name;
        if (!Definitions.ContainsKey(name))
        {
            Definitions[name] = new JsonObject(); // Reserve before traversal for recursive models.
            Add(name, Properties(type));
        }
        return Ref(name);
    }
    private JsonObject Properties(Type type)
    {
        var fields = new JsonObject(); var required = new JsonArray();
        var constructor = type.GetConstructors().OrderByDescending(value => value.GetParameters().Length).FirstOrDefault();
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(value => value.GetIndexParameters().Length == 0 && value.GetMethod is not null))
        {
            var ignored = property.GetCustomAttribute<JsonIgnoreAttribute>();
            if (ignored?.Condition == JsonIgnoreCondition.Always) continue;
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var schema = Type(property.PropertyType);
            if (!property.PropertyType.IsValueType && _nullability.Create(property).ReadState != NullabilityState.NotNull) schema = Nullable(schema);
            fields[name] = schema;
            var parameter = constructor?.GetParameters().FirstOrDefault(value => string.Equals(value.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            if (ignored is null && parameter is { HasDefaultValue: false }) required.Add(name);
        }
        return new() { ["type"] = "object", ["properties"] = fields, ["required"] = required, ["additionalProperties"] = false };
    }
    internal JsonNode? Example(JsonNode schema, string field = "", int depth = 0)
    {
        if (depth > 20) return null;
        if (schema["example"] is JsonNode example) return example.DeepClone();
        if (schema["$ref"] is JsonValue reference)
        {
            JsonNode target = Definitions;
            foreach (var part in reference.GetValue<string>()["#/components/schemas/".Length..].Split('/')) target = target[part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)]!;
            return Example(target, field, depth + 1);
        }
        if (schema["const"] is JsonNode constant) return constant.DeepClone();
        if (schema["enum"] is JsonArray choices) return choices[0]?.DeepClone();
        if (schema["anyOf"] is JsonArray alternatives) return Example(alternatives[0]!, field, depth + 1);
        var type = schema["type"]?.GetValue<string>();
        if (type == "object")
        {
            var value = new JsonObject();
            if (schema["properties"] is JsonObject properties)
                foreach (var property in properties) value[property.Key] = Example(property.Value!, property.Key, depth + 1);
            return value;
        }
        if (type == "array")
        {
            var value = new JsonArray();
            if (schema["prefixItems"] is JsonArray tuple)
                for (var index = 0; index < tuple.Count; index++) value.Add(Example(tuple[index]!, field == "coordinates" ? index == 0 ? "longitude" : "latitude" : field, depth + 1));
            else for (var index = 0; index < Math.Max(1, schema["minItems"]?.GetValue<int>() ?? 0); index++) value.Add(Example(schema["items"]!, field, depth + 1));
            return value;
        }
        if (type == "boolean") return JsonValue.Create(true);
        if (type == "integer" || type == "number") return JsonValue.Create(field switch
        {
            "score" => 90, "longitude" => 30.975, "latitude" => -29.91,
            _ => Math.Max(1, schema["minimum"]?.Deserialize<double>() ?? 0)
        });
        if (type == "null") return null;
        if (schema["format"]?.GetValue<string>() == "date-time") return JsonValue.Create("2026-10-08T12:00:00Z");
        if (schema["format"]?.GetValue<string>() == "uuid") return JsonValue.Create("00000000-0000-4000-8000-000000000001");
        if (schema["format"]?.GetValue<string>() == "email") return JsonValue.Create("synthetic@example.invalid");
        if (schema["format"]?.GetValue<string>() == "uri") return JsonValue.Create("https://synthetic.example.invalid/contract");
        if (schema["pattern"]?.GetValue<string>().Contains("{64}", StringComparison.Ordinal) is true) return JsonValue.Create(new string('a', 64));
        if (schema["pattern"]?.GetValue<string>().Contains("\\d+", StringComparison.Ordinal) is true) return JsonValue.Create("1.0.0");
        if (schema["pattern"]?.GetValue<string>().Contains("[A-Z]", StringComparison.Ordinal) is true)
            return JsonValue.Create(field switch
            {
                "workOrderTypeCode" => WorkOrderCatalog.Baseline.WorkOrderTypes[0].Code,
                "skillsPractised" => WorkOrderCatalog.Baseline.WorkOrderTypes[0].RequiredSkills[0].Code,
                "certificationTypeCode" => WorkOrderCatalog.Baseline.CertificationTypes[0].Code,
                _ => "SYNTHETIC.CODE"
            });
        var sample = field switch
        {
            "token" or "csrfToken" or "signed_url" => "", "referenceNumber" or "reference_number" or "caseReference" => "CIV-20261008-SYNTH001",
            "language" or "residentLanguage" => "en", "privacyConsentVersion" or "privacy_consent_version" => PrivacyConsent.Version,
            "contactEmail" or "contact_email" or "email" => "synthetic@example.invalid",
            "serviceCode" or "service_code" => WorkOrderCatalog.Baseline.WorkOrderTypes[0].ServiceCode,
            "tenantId" => "default", "workerId" => "synthetic-water-01-a", "crewId" => "water-01",
            "contactPhone" or "contact_phone" => "", "description" => "Synthetic municipal water leak for testing.", _ => "Synthetic example"
        };
        return JsonValue.Create(sample);
    }
}
