using System.Globalization;
using System.Text.Json.Nodes;

namespace LambdaRag.Selectors;

/// <summary>
/// Parses a JSONPath subset string into a <see cref="JsonPathExpression"/> and
/// evaluates it against a <see cref="JsonNode"/> graph.
///
/// Supported syntax:
///   $                                    root
///   $.field                              child by name
///   $.array[*]                           all array items
///   $.array[N]                           array index (non-negative)
///   $.array[?(@.field op value)]         filter predicate
///   Multi-step combinations of the above
/// </summary>
internal static class JsonPath
{
    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    public static JsonPathExpression Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path[0] != '$')
            throw new ArgumentException("JSONPath expression must start with '$'.", nameof(path));

        var steps = new List<Step> { RootStep.Instance };
        int i = 1;

        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                i++;
                if (i >= path.Length)
                    throw new ArgumentException("Unexpected end of path after '.'.", nameof(path));

                int start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[')
                    i++;

                if (i == start)
                    throw new ArgumentException($"Empty field name at position {start}.", nameof(path));

                steps.Add(new FieldStep(path[start..i]));
            }
            else if (path[i] == '[')
            {
                i++;
                if (i >= path.Length)
                    throw new ArgumentException("Unclosed '[' in path.", nameof(path));

                if (path[i] == '*')
                {
                    i++; // skip '*'
                    ExpectThenAdvance(path, ref i, ']', nameof(path));
                    steps.Add(AllStep.Instance);
                }
                else if (path[i] == '?')
                {
                    i++; // skip '?'
                    ExpectThenAdvance(path, ref i, '(', nameof(path));

                    int filterStart = i;
                    int depth = 1;
                    while (i < path.Length && depth > 0)
                    {
                        if      (path[i] == '(') depth++;
                        else if (path[i] == ')') depth--;
                        if (depth > 0) i++;
                    }
                    if (depth != 0)
                        throw new ArgumentException("Unclosed '(' in filter expression.", nameof(path));

                    string filterExpr = path[filterStart..i];
                    i++; // skip ')'
                    ExpectThenAdvance(path, ref i, ']', nameof(path));
                    steps.Add(new FilterStep(ParseFilter(filterExpr)));
                }
                else if (char.IsAsciiDigit(path[i]))
                {
                    int start = i;
                    while (i < path.Length && char.IsAsciiDigit(path[i]))
                        i++;
                    int index = int.Parse(path[start..i], CultureInfo.InvariantCulture);
                    ExpectThenAdvance(path, ref i, ']', nameof(path));
                    steps.Add(new IndexStep(index));
                }
                else
                {
                    throw new ArgumentException(
                        $"Unexpected character '{path[i]}' in subscript at position {i}.", nameof(path));
                }
            }
            else
            {
                throw new ArgumentException(
                    $"Unexpected character '{path[i]}' at position {i}.", nameof(path));
            }
        }

        return new JsonPathExpression(steps);
    }

    public static IEnumerable<(string Path, JsonNode Node)> Evaluate(
        JsonPathExpression expression, JsonNode root)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(root);

        var current = new List<(string Path, JsonNode Node)> { ("$", root) };

        foreach (var step in expression.Steps)
        {
            if (step is RootStep) continue;

            var next = new List<(string Path, JsonNode Node)>();
            foreach (var (path, node) in current)
                ApplyStep(step, path, node, next);
            current = next;
        }

        return current;
    }

    // -----------------------------------------------------------------------
    // Evaluation helpers
    // -----------------------------------------------------------------------

    private static void ApplyStep(
        Step step, string path, JsonNode node,
        List<(string Path, JsonNode Node)> results)
    {
        switch (step)
        {
            case FieldStep fieldStep:
                if (node is JsonObject obj
                    && obj.TryGetPropertyValue(fieldStep.Name, out var child)
                    && child is not null)
                {
                    results.Add(($"{path}.{fieldStep.Name}", child));
                }
                break;

            case AllStep:
                if (node is JsonArray allArr)
                    for (int i = 0; i < allArr.Count; i++)
                        if (allArr[i] is { } item)
                            results.Add(($"{path}[{i}]", item));
                break;

            case IndexStep idxStep:
                if (node is JsonArray idxArr
                    && idxStep.Index < idxArr.Count
                    && idxArr[idxStep.Index] is { } idxItem)
                {
                    results.Add(($"{path}[{idxStep.Index}]", idxItem));
                }
                break;

            case FilterStep filterStep:
                if (node is JsonArray filterArr)
                    for (int i = 0; i < filterArr.Count; i++)
                        if (filterArr[i] is { } filterItem && filterStep.Predicate.Evaluate(filterItem))
                            results.Add(($"{path}[{i}]", filterItem));
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Parsing helpers
    // -----------------------------------------------------------------------

    private static void ExpectThenAdvance(string path, ref int i, char expected, string paramName)
    {
        if (i >= path.Length || path[i] != expected)
            throw new ArgumentException($"Expected '{expected}' at position {i}.", paramName);
        i++;
    }

    private static FilterPredicate ParseFilter(string expr)
    {
        expr = expr.Trim();
        if (!expr.StartsWith("@.", StringComparison.Ordinal))
            throw new ArgumentException($"Filter expression must start with '@.': {expr}");

        int i = 2; // skip "@."
        var fieldParts = new List<string>();

        while (i < expr.Length)
        {
            int start = i;
            while (i < expr.Length && IsIdentChar(expr[i]))
                i++;
            if (i == start) break;
            fieldParts.Add(expr[start..i]);

            if (i < expr.Length && expr[i] == '.')
                i++;
            else
                break;
        }

        if (fieldParts.Count == 0)
            throw new ArgumentException($"No field path found in filter expression: {expr}");

        while (i < expr.Length && expr[i] == ' ') i++;

        string op;
        if      (i + 1 < expr.Length && expr[i] == '=' && expr[i + 1] == '~') { op = "=~"; i += 2; }
        else if (i + 1 < expr.Length && expr[i] == '=' && expr[i + 1] == '=') { op = "=="; i += 2; }
        else if (i + 1 < expr.Length && expr[i] == '!' && expr[i + 1] == '=') { op = "!="; i += 2; }
        else if (i + 1 < expr.Length && expr[i] == '<' && expr[i + 1] == '=') { op = "<="; i += 2; }
        else if (i + 1 < expr.Length && expr[i] == '>' && expr[i + 1] == '=') { op = ">="; i += 2; }
        else if (i < expr.Length && expr[i] == '<') { op = "<";  i++; }
        else if (i < expr.Length && expr[i] == '>') { op = ">";  i++; }
        else throw new ArgumentException($"Unknown or missing operator in filter: '{expr}'");

        while (i < expr.Length && expr[i] == ' ') i++;

        var value = ParseFilterValue(expr[i..]);
        return new FilterPredicate(fieldParts, op, value);
    }

    private static JsonNode? ParseFilterValue(string s)
    {
        s = s.Trim();
        if (s == "null")  return null;
        if (s == "true")  return JsonValue.Create(true);
        if (s == "false") return JsonValue.Create(false);

        if (s.Length >= 2)
        {
            if (s[0] == '\'' && s[^1] == '\'') return JsonValue.Create(s[1..^1]);
            if (s[0] == '"'  && s[^1] == '"')  return JsonValue.Create(s[1..^1]);
        }

        if (double.TryParse(s,
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out double d))
            return JsonValue.Create(d);

        throw new ArgumentException($"Cannot parse filter value: '{s}'");
    }

    private static bool IsIdentChar(char c)
        => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-';
}
