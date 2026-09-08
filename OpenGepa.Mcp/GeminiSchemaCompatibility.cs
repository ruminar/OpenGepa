using System.Text.Json.Nodes;

public static class GeminiSchemaCompatibility
{
    public static JsonNode NormalizeNullableArray(JsonNode node)
    {
        if (node is not JsonObject schema || !HasType(schema["type"], "array")) return node;

        schema["type"] = "array";
        schema["default"] = new JsonArray();
        if (schema["items"] is JsonObject items && HasType(items["type"], "string")) items["type"] = "string";
        return schema;
    }

    private static bool HasType(JsonNode? node, string type) => node is JsonArray values && values.Any(value => value?.GetValue<string>() == type);
}
