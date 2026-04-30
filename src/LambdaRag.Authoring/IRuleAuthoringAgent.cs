using System.Text.Json.Nodes;
using LambdaRag.Core.Domain;

namespace LambdaRag.Authoring;

/// <summary>
/// Authors lambda-rag <see cref="Rule"/> candidates from a chunk of source
/// policy text. Implementations may be deterministic heuristics (for tests)
/// or LLM-backed (for production).
///
/// IMPORTANT — the authoring agent is the *only* place where natural-
/// language interpretation is allowed in this system. Its output is always
/// (predicate, lambda, remediation, evidence) — compiled software functions
/// the runtime can execute deterministically. Once authored and approved,
/// the rule never needs an LLM again.
/// </summary>
public interface IRuleAuthoringAgent
{
    /// <summary>
    /// Author one or more rule candidates from the supplied chunk. Returns
    /// an empty list when the chunk does not appear to express any rule
    /// (rather than fabricating one). All returned suggestions are
    /// drafts — a human review step is mandatory before publishing.
    /// </summary>
    Task<IReadOnlyList<RuleAuthoringSuggestion>> AuthorAsync(
        RuleAuthoringRequest request,
        CancellationToken ct = default);
}

/// <summary>Input to the authoring agent.</summary>
public sealed record RuleAuthoringRequest(
    string SourceContent,
    string Domain,
    string RuleIdPrefix,
    SourceSpan SourceSpan,
    IReadOnlyDictionary<string, string>? Hints = null);

/// <summary>
/// One drafted rule plus the agent's confidence and rationale. The
/// <see cref="Rule"/> is fully populated — predicate, lambda, remediation
/// template, evidence quote — and must compile.
/// </summary>
public sealed record RuleAuthoringSuggestion(
    Rule Rule,
    double Confidence,
    string Rationale)
{
    /// <summary>
    /// Convenience: the source content embedding the agent attached, if any.
    /// </summary>
    public IReadOnlyList<float>? SourceEmbedding => Rule.SourceEmbedding;
}
