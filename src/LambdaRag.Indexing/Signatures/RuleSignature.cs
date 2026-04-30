namespace LambdaRag.Indexing.Signatures;

/// <summary>
/// A deterministic, structural signature extracted from a rule's predicate
/// expression. Signatures are how the index narrows tens of thousands of
/// rules down to a handful of candidates *without ever evaluating natural
/// language*. They are a strict superset filter — the compiled predicate
/// still decides applicability.
///
/// Design constraints:
/// • Pure, deterministic extraction. Same predicate text in → same
///   <see cref="RuleSignature"/> bytes out.
/// • Conservative when the predicate cannot be parsed: emit
///   <see cref="Universal"/> = true so the rule is *always* a candidate.
///   Determinism is preserved at the cost of selectivity.
/// • Field paths are normalised to dotted form starting at <c>input1.</c>.
/// </summary>
public sealed record RuleSignature(
    string RuleId,
    bool Universal,
    IReadOnlyList<EqualityConstraint> Equalities,
    IReadOnlyList<ContainsConstraint> Containments,
    IReadOnlyList<string> FieldPaths)
{
    public static RuleSignature UniversalFor(string ruleId) =>
        new(ruleId, Universal: true, [], [], []);
}

/// <summary>An <c>input1.path == "literal"</c> constraint extracted from the predicate.</summary>
public sealed record EqualityConstraint(string FieldPath, string Literal);

/// <summary>An <c>input1.path.Contains("literal")</c> constraint extracted from the predicate.</summary>
public sealed record ContainsConstraint(string FieldPath, string Literal);
