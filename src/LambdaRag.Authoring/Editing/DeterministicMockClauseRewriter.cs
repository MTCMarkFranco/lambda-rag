using LambdaRag.Core.Domain;
using LambdaRag.Markup;

namespace LambdaRag.Authoring.Editing;

/// <summary>
/// Deterministic offline fallback for <see cref="IClauseRewriter"/>.
/// Returns <see cref="Verdict.RemediationText"/> when present so the
/// markup pipeline can exercise the Replace code path without any
/// LLM dependency. Pure code, byte-stable across runs and machines.
/// </summary>
public sealed class DeterministicMockClauseRewriter : IClauseRewriter
{
    public Task<string?> RewriteAsync(
        Verdict verdict, string clauseText, Rule? rule, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(verdict.RemediationText))
            return Task.FromResult<string?>(null);
        if (string.IsNullOrWhiteSpace(clauseText))
            return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(verdict.RemediationText.Trim());
    }
}
