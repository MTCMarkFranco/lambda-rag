using System.Globalization;

namespace LambdaRag.Authoring.Dsl;

/// <summary>
/// Fluent, type-safe authoring surface for rule lambdas. Produces strings in
/// the exact shape RulesEngine expects, so a rule written like
///
/// <code>
/// var lambda = Lambda
///     .Section("input1")
///     .ContainsMeaning("contract tax bracket")
///     .And(Lambda.Field("input1", "tax").LessThan(100))
///     .ToExpression();
/// // → "ContainsMeaning(input1.id, \"contract tax bracket\", 0.78) &amp;&amp; input1.tax &lt; 100"
/// </code>
///
/// reads naturally in C#, compiles, and serialises to the JSON ruleset
/// without a parser. The methods below are pure string composition — they
/// do not touch the LLM or compute embeddings; vectors are resolved at
/// evaluation time by the registered <c>ContainsMeaning</c> RulesEngine
/// function against a precomputed <see cref="LambdaRag.Authoring.Semantic.ISemanticVectorStore"/>.
/// </summary>
public static class Lambda
{
    /// <summary>
    /// Default cosine-similarity threshold for <c>ContainsMeaning</c>. Tuned
    /// for <c>text-embedding-3-large</c>. Per-rule overrides expected.
    /// </summary>
    public const double DefaultMeaningThreshold = 0.78;

    /// <summary>Start a fluent chain rooted at the given section variable (default <c>input1</c>).</summary>
    public static SectionRef Section(string variable = "input1") => new(variable);

    /// <summary>Reference a scalar field on the input — e.g. <c>Lambda.Field("input1","tax")</c>.</summary>
    public static FieldRef Field(string variable, string field) => new(variable, field);

    /// <summary>
    /// Bare semantic predicate against the default section variable. Useful
    /// for inlining: <c>$"input1.text.Contains(\"Contoso\") &amp;&amp; {Lambda.ContainsMeaning(\"works made for hire\")}"</c>.
    /// Resolves at evaluation time to a precomputed cosine lookup.
    /// </summary>
    public static string ContainsMeaning(string concept, double threshold = DefaultMeaningThreshold, string variable = "input1")
        => Section(variable).ContainsMeaning(concept, threshold).ToExpression();

    internal static string EscapeStringLiteral(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    internal static string Inv(double d) => d.ToString("0.################", CultureInfo.InvariantCulture);
    internal static string Inv(long l) => l.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A reference to the section variable in scope (typically <c>input1</c>).
/// Methods on this type produce <see cref="LambdaPredicate"/> values that
/// compose with <c>And</c> / <c>Or</c> / <c>Not</c>.
/// </summary>
public readonly record struct SectionRef(string Variable)
{
    /// <summary>
    /// "Does this section express the concept?". Compiles to a call into the
    /// registered <c>SemanticFunctions.ContainsMeaning</c>; the function reads
    /// precomputed vectors and returns a deterministic boolean
    /// (cosine ≥ threshold). No LLM call at evaluation time.
    /// </summary>
    public LambdaPredicate ContainsMeaning(string concept, double threshold = Lambda.DefaultMeaningThreshold)
    {
        if (string.IsNullOrWhiteSpace(concept))
            throw new ArgumentException("concept must be non-empty", nameof(concept));
        var expr = $"ContainsMeaning({Variable}.id, \"{Lambda.EscapeStringLiteral(concept)}\", {Lambda.Inv(threshold)})";
        return new LambdaPredicate(expr);
    }

    /// <summary>
    /// "Does this section express any of the given concepts?". Lowest cosine
    /// across the concept list ≥ threshold = false; any concept ≥ threshold = true.
    /// Equivalent to OR-ing individual <c>ContainsMeaning</c> calls but
    /// evaluated in a single function call (cheaper, identical semantics).
    /// </summary>
    public LambdaPredicate MatchesAny(double threshold, params string[] concepts)
    {
        if (concepts is null || concepts.Length == 0)
            throw new ArgumentException("at least one concept required", nameof(concepts));
        var joined = string.Join("|", concepts.Select(Lambda.EscapeStringLiteral));
        var expr = $"MatchesAnyMeaning({Variable}.id, \"{joined}\", {Lambda.Inv(threshold)})";
        return new LambdaPredicate(expr);
    }

    /// <summary>Convenience: <c>section.MatchesAny(concepts)</c> at default threshold.</summary>
    public LambdaPredicate MatchesAny(params string[] concepts) => MatchesAny(Lambda.DefaultMeaningThreshold, concepts);

    /// <summary>Tier-2 lexical leaf: <c>input1.text.Contains("...")</c>.</summary>
    public LambdaPredicate TextContains(string literal)
        => new($"{Variable}.text.Contains(\"{Lambda.EscapeStringLiteral(literal)}\")");
}

/// <summary>A reference to a scalar field on the input — produces comparison predicates.</summary>
public readonly record struct FieldRef(string Variable, string Field)
{
    public string Path => $"{Variable}.{Field}";
    public LambdaPredicate LessThan(long v)        => new($"{Path} < {Lambda.Inv(v)}");
    public LambdaPredicate LessThanOrEqual(long v) => new($"{Path} <= {Lambda.Inv(v)}");
    public LambdaPredicate GreaterThan(long v)        => new($"{Path} > {Lambda.Inv(v)}");
    public LambdaPredicate GreaterThanOrEqual(long v) => new($"{Path} >= {Lambda.Inv(v)}");
    public LambdaPredicate EqualsValue(long v) => new($"{Path} == {Lambda.Inv(v)}");
    public LambdaPredicate EqualsValue(string v) => new($"{Path} == \"{Lambda.EscapeStringLiteral(v)}\"");
}

/// <summary>
/// Composable boolean predicate over a section. Carries the RulesEngine
/// expression text and exposes <c>And</c> / <c>Or</c> / <c>Not</c> combinators
/// that emit fully-parenthesised expressions for unambiguous precedence.
/// </summary>
public readonly record struct LambdaPredicate(string Expression)
{
    public LambdaPredicate And(LambdaPredicate other) => new($"({Expression}) && ({other.Expression})");
    public LambdaPredicate Or(LambdaPredicate other)  => new($"({Expression}) || ({other.Expression})");
    public LambdaPredicate Not()                      => new($"!({Expression})");

    /// <summary>The RulesEngine-ready expression string. Aliased as <c>ToString</c>.</summary>
    public string ToExpression() => Expression;
    public override string ToString() => Expression;

    public static implicit operator string(LambdaPredicate p) => p.Expression;
}
