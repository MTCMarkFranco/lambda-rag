using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LambdaRag.Core.Selectors;

namespace LambdaRag.Core;

/// <summary>
/// Deterministic JSON serialization options used everywhere:
///  • property names preserved (no camelCase mangling — selectors path-match
///    on canonical field names from the projector schema)
///  • indented output for golden-master diffability
///  • UTF-8 with no BOM, LF line endings — stable across OSes
///  • selector polymorphism is registered
/// </summary>
public static class CanonicalJson
{
    public static JsonSerializerOptions Options { get; } = Build(indented: true);
    public static JsonSerializerOptions Compact { get; } = Build(indented: false);

    private static JsonSerializerOptions Build(bool indented) => new()
    {
        WriteIndented = indented,
        // Force LF newlines so indented JSON is byte-identical on Windows
        // and Linux. STJ defaults this to Environment.NewLine.
        NewLine = "\n",
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value, bool indented = true)
        => JsonSerializer.Serialize(value, indented ? Options : Compact);

    public static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options)!;

    public static JsonObject Clone(JsonObject obj) => (JsonObject)obj.DeepClone();
}
