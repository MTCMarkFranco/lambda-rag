using System.Dynamic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LambdaRag.Evaluation.Workflow;

/// <summary>
/// Converts a JsonNode (typically a sub-graph of a ProjectedDocument) into
/// a System.Dynamic.ExpandoObject that Microsoft RulesEngine can consume
/// via its DynamicClassFactory path.
///
/// Determinism notes:
///   • Property iteration order on JsonObject preserves insertion order.
///   • We materialize children eagerly so the resulting ExpandoObject has
///     stable property order across runs.
///   • Numbers are mapped to long when integral, double otherwise — so a
///     lambda like "input1.amount > 100" works without manual casts.
///   • Arrays become List&lt;object?&gt;.
/// </summary>
public static class JsonToExpando
{
    public static object? Convert(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonObject obj => ConvertObject(obj),
            JsonArray arr => ConvertArray(arr),
            JsonValue val => ConvertValue(val),
            _ => null,
        };
    }

    private static ExpandoObject ConvertObject(JsonObject obj)
    {
        var expando = new ExpandoObject();
        var dict = (IDictionary<string, object?>)expando;
        foreach (var kvp in obj)
            dict[kvp.Key] = Convert(kvp.Value);
        return expando;
    }

    private static List<object?> ConvertArray(JsonArray arr)
    {
        var list = new List<object?>(arr.Count);
        foreach (var item in arr)
            list.Add(Convert(item));
        return list;
    }

    private static object? ConvertValue(JsonValue val)
    {
        // JsonValue may wrap a JsonElement *or* a CLR primitive depending on
        // how the JsonNode was constructed (parsed vs. literal). Try
        // JsonElement first; fall back to TryGetValue<T> for the common
        // primitive shapes.
        if (val.TryGetValue<JsonElement>(out var element))
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.ToString(),
            };
        }

        if (val.TryGetValue<string>(out var s)) return s;
        if (val.TryGetValue<bool>(out var b)) return b;
        if (val.TryGetValue<long>(out var l2)) return l2;
        if (val.TryGetValue<int>(out var i)) return (long)i;
        if (val.TryGetValue<double>(out var d)) return d;
        if (val.TryGetValue<decimal>(out var m)) return (double)m;
        return val.ToString();
    }
}
