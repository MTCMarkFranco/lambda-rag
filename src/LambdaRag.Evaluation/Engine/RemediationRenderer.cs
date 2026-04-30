using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;

namespace LambdaRag.Evaluation.Engine;

/// <summary>
/// Renders a rule's remediation template into a concrete suggestion in the
/// language of the source document. Pure-code, deterministic — given the
/// same template, rule, and matched section, the rendered output is
/// byte-identical across runs.
///
/// Supported placeholders:
/// <list type="bullet">
///   <item><c>{rule.id}</c>, <c>{rule.naturalLanguage}</c>, <c>{rule.evidenceQuote}</c></item>
///   <item><c>{section.id}</c>, <c>{section.heading}</c>, <c>{section.category}</c>,
///         <c>{section.text}</c>, <c>{section.firstSentence}</c></item>
///   <item><c>{meta.&lt;key&gt;}</c> — value from <see cref="Rule.Metadata"/>
///         (e.g., <c>{meta.requiredJurisdiction}</c>)</item>
///   <item><c>{input.&lt;path&gt;}</c> — any leaf path in the matched JSON
///         node (e.g., <c>{input.text}</c>)</item>
/// </list>
///
/// Any placeholder that cannot be resolved is left in place verbatim so
/// authors notice the typo in review.
/// </summary>
public static class RemediationRenderer
{
    /// <summary>
    /// Renders <paramref name="template"/> against the rule and matched section.
    /// Returns null when the template is null/empty so callers can simply
    /// pass through <see cref="Rule.Remediation"/>.
    /// </summary>
    public static string? Render(string? template, Rule rule, MatchedSection? section)
    {
        if (string.IsNullOrEmpty(template)) return null;

        var sb = new StringBuilder(template.Length + 64);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            sb.Append(template, i, open - i);
            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                sb.Append(template, open, template.Length - open);
                break;
            }
            var placeholder = template.Substring(open + 1, close - open - 1);
            var resolved = Resolve(placeholder, rule, section);
            if (resolved is null)
            {
                // Unresolved — keep the original placeholder so authors see it.
                sb.Append(template, open, close - open + 1);
            }
            else
            {
                sb.Append(resolved);
            }
            i = close + 1;
        }
        return sb.ToString();
    }

    private static string? Resolve(string placeholder, Rule rule, MatchedSection? section)
    {
        if (placeholder.Length == 0) return null;
        var dot = placeholder.IndexOf('.');
        var head = dot < 0 ? placeholder : placeholder[..dot];
        var tail = dot < 0 ? string.Empty : placeholder[(dot + 1)..];

        return head switch
        {
            "rule" => ResolveRule(tail, rule),
            "section" => ResolveSection(tail, section),
            "meta" => ResolveMeta(tail, rule),
            "input" => ResolveInput(tail, section),
            _ => null,
        };
    }

    private static string? ResolveRule(string field, Rule rule) => field switch
    {
        "id" => rule.Id,
        "version" => rule.Version,
        "naturalLanguage" => rule.NaturalLanguage,
        "evidenceQuote" => rule.EvidenceQuote,
        "severity" => rule.Severity.ToString(),
        _ => null,
    };

    private static string? ResolveSection(string field, MatchedSection? section)
    {
        if (section is null) return null;
        if (section.Node is not JsonObject obj) return field == "text" ? section.Node.ToJsonString() : null;
        return field switch
        {
            "id" => obj["id"]?.GetValue<string>(),
            "heading" => obj["heading"]?.GetValue<string>(),
            "headingPath" => obj["heading_path"]?.GetValue<string>(),
            "category" => obj["category"]?.GetValue<string>(),
            "text" => obj["text"]?.GetValue<string>(),
            "firstSentence" => FirstSentence(obj["text"]?.GetValue<string>()),
            _ => null,
        };
    }

    private static string? ResolveMeta(string key, Rule rule)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return rule.Metadata.TryGetValue(key, out var value) ? value : null;
    }

    private static string? ResolveInput(string path, MatchedSection? section)
    {
        if (section is null || string.IsNullOrEmpty(path)) return null;
        // Walk a dotted path through the matched node.
        JsonNode? cursor = section.Node;
        foreach (var part in path.Split('.'))
        {
            if (cursor is not JsonObject obj) return null;
            cursor = obj[part];
            if (cursor is null) return null;
        }
        return cursor switch
        {
            JsonValue jv => jv.ToString(),
            null => null,
            _ => cursor.ToJsonString(),
        };
    }

    private static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Find first sentence terminator; ASCII-only by design (deterministic).
        var span = text.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c is '.' or '!' or '?')
            {
                // Include the terminator, trim trailing whitespace.
                return text.Substring(0, i + 1).TrimEnd();
            }
        }
        return text.Trim();
    }

    /// <summary>
    /// Number formatting helper for templates. Always uses InvariantCulture
    /// so rendered remediation text is the same on any machine.
    /// </summary>
    public static string FormatNumber(double value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);
}
