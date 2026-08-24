using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Goldfish.Acp;

public static class AcpSchemaValidator
{
    private static readonly Lazy<JsonObject> SchemaRoot = new(LoadSchema);
    private static readonly Dictionary<string, Json.Schema.JsonSchema> DefinitionSchemas = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    public static bool IsValidDefinition<T>(string definitionName, T value)
        => IsValidDefinition(definitionName, JsonSerializer.SerializeToElement(value, AcpJson.Options));

    public static bool IsValidDefinition(string definitionName, JsonElement value)
    {
        var schema = GetDefinitionSchema(definitionName);
        return schema.Evaluate(value, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid;
    }

    private static Json.Schema.JsonSchema GetDefinitionSchema(string definitionName)
    {
        lock (Gate)
        {
            if (DefinitionSchemas.TryGetValue(definitionName, out var existing)) return existing;
            var root = SchemaRoot.Value;
            var definitions = root["$defs"]?.DeepClone()
                ?? throw new InvalidOperationException("Embedded ACP schema has no $defs.");
            if (root["$defs"]?[definitionName] == null)
                throw new ArgumentOutOfRangeException(nameof(definitionName), definitionName, "Unknown ACP schema definition.");
            var wrapper = new JsonObject
            {
                ["$schema"] = root["$schema"]?.DeepClone(),
                ["$defs"] = definitions,
                ["$ref"] = $"#/$defs/{definitionName}"
            };
            var schema = Json.Schema.JsonSchema.FromText(wrapper.ToJsonString());
            DefinitionSchemas[definitionName] = schema;
            return schema;
        }
    }

    private static JsonObject LoadSchema()
    {
        using var stream = typeof(AcpSchemaValidator).Assembly.GetManifestResourceStream("Goldfish.Acp.Protocol.schema.v1.json")
            ?? throw new InvalidOperationException("Embedded ACP schema is missing.");
        return JsonNode.Parse(stream)?.AsObject()
            ?? throw new InvalidOperationException("Embedded ACP schema is invalid.");
    }
}
