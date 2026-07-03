using System;

namespace LambdaRag.Core.Domain;

/// <summary>
/// Thrown when the caller declares a review domain that does not match
/// the ruleset's authored domain. Issue #159 — domain-scoped review.
/// Applying a ruleset to a document outside its authored domain is a
/// user error, not an accuracy problem to be optimized. Failing loud
/// at the entry point is safer than producing potentially-nonsensical
/// verdicts.
/// </summary>
public sealed class DomainMismatchException : InvalidOperationException
{
    public string DeclaredDomain { get; }
    public string RulesetDomain { get; }
    public string RulesetId { get; }

    public DomainMismatchException(string declaredDomain, string rulesetDomain, string rulesetId)
        : base(BuildMessage(declaredDomain, rulesetDomain, rulesetId))
    {
        DeclaredDomain = declaredDomain;
        RulesetDomain = rulesetDomain;
        RulesetId = rulesetId;
    }

    private static string BuildMessage(string declared, string ruleset, string id) =>
        $"Domain mismatch: caller declared domain '{declared}' but ruleset '{id}' " +
        $"is authored for domain '{ruleset}'. Lambda-rag does not perform cross-domain " +
        "evaluation — pick a ruleset whose domain matches, or remove the --domain override " +
        "to inherit the ruleset's domain.";
}

/// <summary>
/// Validates that a caller-declared review domain matches the ruleset's
/// authored domain. Issue #159 — cross-domain evaluation is out of scope
/// for lambda-rag; the caller declares intent and we enforce it at the
/// entry point of every review.
/// </summary>
public static class DomainScopeValidator
{
    /// <summary>
    /// Throws <see cref="DomainMismatchException"/> if
    /// <paramref name="declaredDomain"/> is non-null/non-whitespace and
    /// does not match <c>ruleSet.Domain</c> under
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>. A null or
    /// whitespace declared domain is treated as "inherit from ruleset"
    /// and passes silently — that is the intended default for callers
    /// who don't want to type the domain twice.
    /// </summary>
    public static void RequireMatch(string? declaredDomain, RuleSet ruleSet)
    {
        if (ruleSet is null) throw new ArgumentNullException(nameof(ruleSet));
        if (string.IsNullOrWhiteSpace(declaredDomain)) return;
        if (!string.Equals(declaredDomain, ruleSet.Domain, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainMismatchException(declaredDomain, ruleSet.Domain, ruleSet.Id);
        }
    }
}
