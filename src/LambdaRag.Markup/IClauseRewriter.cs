using LambdaRag.Core.Domain;

namespace LambdaRag.Markup;

/// <summary>
/// AI-backed clause re-author seam used by <see cref="AnnotationFactory"/>
/// to turn a Fail / Error verdict into a tracked-change rewrite instead
/// of a bare reviewer comment. Implementations are free to call an LLM
/// (production) or return deterministic text (tests / offline runs).
///
/// Contract:
/// <list type="bullet">
///   <item>
///   <description>Return <c>null</c> when no rewrite should be applied
///   (the caller falls back to <see cref="AnnotationKind.Comment"/>).
///   This is the safe default for the <c>Noop</c> implementation and
///   for any verdict the rewriter cannot confidently re-author.</description>
///   </item>
///   <item>
///   <description>The returned string MUST be the *new* clause text only
///   — not a diff, not a JSON envelope, not a leading "Rewrite:" prefix.
///   <see cref="OpenXmlMarkupService"/> wraps the original span in a
///   tracked-change deletion and inserts the returned text adjacent to
///   it.</description>
///   </item>
///   <item>
///   <description>Implementations should be deterministic for a given
///   <c>(rule.Id, rule.Version, clauseText, verdict.RemediationText)</c>
///   tuple so the markup pipeline stays byte-reproducible. Production
///   agents enforce this with a content-hashed disk cache.</description>
///   </item>
/// </list>
/// </summary>
public interface IClauseRewriter
{
    /// <summary>
    /// Produce a tracked-change rewrite for the spanned clause, or
    /// <c>null</c> to opt out (the caller will emit a Comment instead).
    /// </summary>
    /// <param name="verdict">The failing verdict that triggered the rewrite.</param>
    /// <param name="clauseText">
    /// The original clause text that lies under the verdict's source
    /// span. May be empty when the span resolves to nothing (e.g. a
    /// gap-style verdict with zero length) — the rewriter is free to
    /// return <c>null</c> in that case.
    /// </param>
    /// <param name="rule">
    /// The matching <see cref="Rule"/> for context (may be <c>null</c>
    /// when the rule was disabled between report and markup).
    /// </param>
    Task<string?> RewriteAsync(
        Verdict verdict,
        string clauseText,
        Rule? rule,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default no-op rewriter used when no AI-backed
/// <see cref="IClauseRewriter"/> is configured. Always returns
/// <c>null</c> so <see cref="AnnotationFactory.FromReport"/> falls back
/// to the historical <see cref="AnnotationKind.Comment"/> behavior. This
/// keeps the offline / unit-test / CI replay story unchanged — no AI
/// dependency is forced on consumers of <c>LambdaRag.Markup</c>.
/// </summary>
public sealed class NoopClauseRewriter : IClauseRewriter
{
    public static readonly NoopClauseRewriter Instance = new();

    public Task<string?> RewriteAsync(
        Verdict verdict, string clauseText, Rule? rule, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);
}
