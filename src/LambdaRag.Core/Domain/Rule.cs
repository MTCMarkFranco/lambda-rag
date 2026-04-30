using System.Text.Json.Nodes;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;

namespace LambdaRag.Core.Domain;

public enum RuleSeverity { Suggestion, Deviation, Violation, Critical }

/// <summary>
/// A rule extracted from a source policy. The combination of
/// (selector, lambda, applies_to_schema) is what makes rule application
/// 100% deterministic at runtime.
///
/// • The selector projects the rule onto the right slice of a
///   ProjectedDocument (pure code, no LLM).
/// • The applies_to_schema describes the input shape the lambda expects;
///   the matcher reshapes the matched sub-graph to satisfy it.
/// • The lambda is a Microsoft RulesEngine LambdaExpression evaluated
///   over that input — no NL interpretation at runtime.
/// </summary>
public sealed record Rule(
    string Id,
    string Version,
    string NaturalLanguage,
    string Lambda,
    JsonObject AppliesToSchema,
    Selector Selector,
    RuleSeverity Severity,
    SourceSpan SourceSpan,
    string EvidenceQuote,
    IReadOnlyDictionary<string, string> Metadata)
{
    public ContentHash Fingerprint() => ContentHash.Compose(
        Id, Version, Lambda, AppliesToSchema.ToJsonString(), Severity.ToString());
}

/// <summary>
/// A versioned collection of rules. Once published, a RuleSet is immutable;
/// edits produce a new version. Verdicts always cite (rule_id, ruleset_version).
/// </summary>
public sealed record RuleSet(
    string Id,
    string Version,
    string Domain,
    DateTimeOffset PublishedAt,
    IReadOnlyList<Rule> Rules,
    IReadOnlyDictionary<string, string> Metadata)
{
    public ContentHash Fingerprint()
    {
        var parts = new List<string> { Id, Version, Domain };
        foreach (var r in Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
            parts.Add(r.Fingerprint().Value);
        return ContentHash.Compose(parts.ToArray());
    }
}
