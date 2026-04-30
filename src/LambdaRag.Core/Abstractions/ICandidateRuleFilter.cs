using System.Text.Json.Nodes;

namespace LambdaRag.Core.Abstractions;

/// <summary>
/// Optional pre-filter consulted by the deterministic evaluator to narrow
/// the rule set down to candidates whose predicate could possibly match the
/// supplied section. Implementations MUST be a strict superset of the actual
/// predicate result — a rule whose compiled predicate would return true must
/// always appear in <see cref="LookupCandidates(JsonNode)"/>.
///
/// The runtime evaluator never trusts this filter to make a decision; the
/// compiled predicate is still evaluated. The filter exists purely to skip
/// predicates that have no chance of matching, turning O(rules × sections)
/// into O(matched-candidates) at enterprise scale.
///
/// When the filter is not registered the evaluator falls back to full
/// iteration — behaviour is byte-identical, only slower.
/// </summary>
public interface ICandidateRuleFilter
{
    /// <summary>A stable identifier so verdict audits can record which filter narrowed the search.</summary>
    string FilterId { get; }

    /// <summary>True if this filter has been built and contains entries; false → evaluator must consider every rule.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Return the candidate rule ids the supplied section could possibly match.
    /// Result must be deterministic and ordered (ordinal by rule id).
    /// </summary>
    IReadOnlyCollection<string> LookupCandidates(JsonNode section);
}
