using System.Text.RegularExpressions;
using LambdaRag.Core.Domain;

namespace LambdaRag.Indexing.Signatures;

/// <summary>
/// Pure-code extractor that pulls a structural <see cref="RuleSignature"/>
/// out of a RulesEngine bool LambdaExpression string. Recognises the two
/// most common shapes used by lambda-rag predicates:
/// <list type="bullet">
///   <item><c>input1.path == "literal"</c> → <see cref="EqualityConstraint"/></item>
///   <item><c>input1.path.Contains("literal")</c> → <see cref="ContainsConstraint"/></item>
/// </list>
///
/// When the predicate is the literal <c>true</c> or cannot be parsed,
/// the rule is marked <see cref="RuleSignature.Universal"/> so the index
/// always returns it as a candidate. This conservative-by-default design
/// guarantees that pre-filter ⊇ predicate result — i.e., the index never
/// hides a rule the predicate would have matched.
///
/// Determinism: same predicate string → byte-identical RuleSignature.
/// No locale, no culture, no time. Regex matching uses ordinal semantics.
/// </summary>
public static class PredicateSignatureExtractor
{
    private static readonly Regex EqualsRegex = new(
        "input1((?:\\.[A-Za-z_][A-Za-z0-9_]*)+)\\s*==\\s*\"([^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EqualsReverseRegex = new(
        "\"([^\"]*)\"\\s*==\\s*input1((?:\\.[A-Za-z_][A-Za-z0-9_]*)+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ContainsRegex = new(
        "input1((?:\\.[A-Za-z_][A-Za-z0-9_]*)+)\\.Contains\\(\"([^\"]*)\"\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FieldPathRegex = new(
        "input1((?:\\.[A-Za-z_][A-Za-z0-9_]*)+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RuleSignature Extract(Rule rule)
    {
        var predicate = (rule.Predicate ?? "true").Trim();
        if (string.IsNullOrEmpty(predicate)
            || string.Equals(predicate, "true", StringComparison.Ordinal))
        {
            return RuleSignature.UniversalFor(rule.Id);
        }

        var equals = new List<EqualityConstraint>();
        foreach (Match m in EqualsRegex.Matches(predicate))
        {
            equals.Add(new EqualityConstraint(NormalisePath(m.Groups[1].Value), m.Groups[2].Value));
        }
        foreach (Match m in EqualsReverseRegex.Matches(predicate))
        {
            equals.Add(new EqualityConstraint(NormalisePath(m.Groups[2].Value), m.Groups[1].Value));
        }

        var contains = new List<ContainsConstraint>();
        foreach (Match m in ContainsRegex.Matches(predicate))
        {
            contains.Add(new ContainsConstraint(NormalisePath(m.Groups[1].Value), m.Groups[2].Value));
        }

        var fieldPaths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in FieldPathRegex.Matches(predicate))
        {
            fieldPaths.Add(NormalisePath(m.Groups[1].Value));
        }

        // If the predicate is non-trivial but we extracted nothing recognisable,
        // fall back to Universal so the index stays a strict superset.
        if (equals.Count == 0 && contains.Count == 0)
        {
            return RuleSignature.UniversalFor(rule.Id);
        }

        equals.Sort((a, b) =>
            string.CompareOrdinal(a.FieldPath, b.FieldPath) is var c and not 0
                ? c
                : string.CompareOrdinal(a.Literal, b.Literal));
        contains.Sort((a, b) =>
            string.CompareOrdinal(a.FieldPath, b.FieldPath) is var c and not 0
                ? c
                : string.CompareOrdinal(a.Literal, b.Literal));

        return new RuleSignature(
            RuleId: rule.Id,
            Universal: false,
            Equalities: equals,
            Containments: contains,
            FieldPaths: fieldPaths.ToList());
    }

    private static string NormalisePath(string raw)
    {
        // raw begins with '.', e.g. ".category" or ".user.email"
        return "input1" + raw;
    }
}
