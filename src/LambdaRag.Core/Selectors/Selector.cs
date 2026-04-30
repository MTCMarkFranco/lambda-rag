using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LambdaRag.Core.Selectors;

/// <summary>
/// Deterministic selector DSL. Selectors are produced by the extraction
/// agent at authoring time and stored alongside the lambda expression.
/// At runtime they are matched against a ProjectedDocument by a pure-code
/// matcher — no LLM is involved.
///
/// Serialization: tagged union via the "kind" discriminator.
/// </summary>
[JsonConverter(typeof(SelectorJsonConverter))]
public abstract record Selector
{
    public abstract string Kind { get; }
}

public sealed record PathSelector(string Path) : Selector
{
    public override string Kind => "path";
}

public sealed record RegexSelector(string Pattern, string? Path = null) : Selector
{
    public override string Kind => "regex";
}

public sealed record HasFieldSelector(string Path, string Field) : Selector
{
    public override string Kind => "hasField";
}

public sealed record ValueInSelector(string Path, IReadOnlyList<JsonNode?> Values) : Selector
{
    public override string Kind => "valueIn";
}

public sealed record AllOfSelector(IReadOnlyList<Selector> Of) : Selector
{
    public override string Kind => "all";
}

public sealed record AnyOfSelector(IReadOnlyList<Selector> Of) : Selector
{
    public override string Kind => "any";
}

public sealed record NotSelector(Selector Of) : Selector
{
    public override string Kind => "not";
}

internal sealed class SelectorJsonConverter : JsonConverter<Selector>
{
    public override Selector? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader) as JsonObject
            ?? throw new JsonException("Selector must be an object");
        var kind = node["kind"]?.GetValue<string>()
            ?? throw new JsonException("Selector missing 'kind'");
        return kind switch
        {
            "path" => new PathSelector(node["path"]!.GetValue<string>()),
            "regex" => new RegexSelector(node["pattern"]!.GetValue<string>(), node["path"]?.GetValue<string>()),
            "hasField" => new HasFieldSelector(node["path"]!.GetValue<string>(), node["field"]!.GetValue<string>()),
            "valueIn" => new ValueInSelector(
                node["path"]!.GetValue<string>(),
                (node["values"] as JsonArray)?.Select(n => n?.DeepClone()).ToList() ?? []),
            "all" => new AllOfSelector(ReadList(node["of"], options)),
            "any" => new AnyOfSelector(ReadList(node["of"], options)),
            "not" => new NotSelector(JsonSerializer.Deserialize<Selector>(node["of"]!.ToJsonString(), options)!),
            _ => throw new JsonException($"Unknown selector kind '{kind}'"),
        };
    }

    private static List<Selector> ReadList(JsonNode? node, JsonSerializerOptions options)
    {
        if (node is not JsonArray arr) throw new JsonException("Composite selector requires 'of' array");
        var list = new List<Selector>(arr.Count);
        foreach (var item in arr)
            list.Add(JsonSerializer.Deserialize<Selector>(item!.ToJsonString(), options)!);
        return list;
    }

    public override void Write(Utf8JsonWriter writer, Selector value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        switch (value)
        {
            case PathSelector p:
                writer.WriteString("path", p.Path);
                break;
            case RegexSelector r:
                if (r.Path is not null) writer.WriteString("path", r.Path);
                writer.WriteString("pattern", r.Pattern);
                break;
            case HasFieldSelector h:
                writer.WriteString("path", h.Path);
                writer.WriteString("field", h.Field);
                break;
            case ValueInSelector v:
                writer.WriteString("path", v.Path);
                writer.WritePropertyName("values");
                writer.WriteStartArray();
                foreach (var item in v.Values)
                    JsonSerializer.Serialize(writer, item, options);
                writer.WriteEndArray();
                break;
            case AllOfSelector a:
                writer.WritePropertyName("of");
                JsonSerializer.Serialize(writer, a.Of, options);
                break;
            case AnyOfSelector a:
                writer.WritePropertyName("of");
                JsonSerializer.Serialize(writer, a.Of, options);
                break;
            case NotSelector n:
                writer.WritePropertyName("of");
                JsonSerializer.Serialize(writer, n.Of, options);
                break;
        }
        writer.WriteEndObject();
    }
}
