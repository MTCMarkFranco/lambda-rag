using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LambdaRag.Selectors;

internal sealed class FilterPredicate
{
    private readonly Regex? _compiledRegex;

    public IReadOnlyList<string> FieldPath { get; }
    public string Op { get; }
    public JsonNode? Value { get; }

    public FilterPredicate(IReadOnlyList<string> fieldPath, string op, JsonNode? value)
    {
        FieldPath = fieldPath;
        Op = op;
        Value = value;

        if (op == "=~"
            && value is JsonValue jv
            && jv.TryGetValue<string>(out var pattern)
            && pattern is not null)
        {
            _compiledRegex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
    }

    public bool Evaluate(JsonNode? item)
    {
        JsonNode? current = item;
        foreach (var part in FieldPath)
        {
            if (current is not JsonObject obj) return false;
            if (!obj.TryGetPropertyValue(part, out current)) return false;
        }

        return Op switch
        {
            "==" => JsonNodeEquals(current, Value),
            "!=" => !JsonNodeEquals(current, Value),
            "<"  => CompareNumeric(current, Value) < 0,
            "<=" => CompareNumeric(current, Value) <= 0,
            ">"  => CompareNumeric(current, Value) > 0,
            ">=" => CompareNumeric(current, Value) >= 0,
            "=~" => MatchesRegex(current),
            _    => false,
        };
    }

    private static bool JsonNodeEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return JsonNode.DeepEquals(a, b);
    }

    private bool MatchesRegex(JsonNode? node)
    {
        if (_compiledRegex is null || node is null) return false;
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s) && s is not null)
            return _compiledRegex.IsMatch(s);
        return false;
    }

    private static int CompareNumeric(JsonNode? left, JsonNode? right)
        => ToDouble(left).CompareTo(ToDouble(right));

    private static double ToDouble(JsonNode? n)
    {
        if (n is not JsonValue jv) return double.NaN;
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<long>(out var l))   return (double)l;
        if (jv.TryGetValue<int>(out var i))    return (double)i;
        return double.NaN;
    }
}
