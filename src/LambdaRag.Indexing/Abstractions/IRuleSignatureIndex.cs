using System.Text.Json.Nodes;
using LambdaRag.Core.Domain;
using LambdaRag.Indexing.Signatures;

namespace LambdaRag.Indexing.Abstractions;

/// <summary>
/// Pre-filters a large rule set down to the candidate rule ids whose
/// predicate *could* match a given section. Strict superset of the actual
/// predicate result — i.e., every rule whose compiled predicate would
/// return true is guaranteed to be in the candidate list.
///
/// This is the runtime performance win: instead of compiling and running
/// every predicate against every section, the evaluator only considers
/// candidates the index returns. With well-structured predicates this
/// turns O(rules × sections) into O(matched).
/// </summary>
public interface IRuleSignatureIndex
{
    /// <summary>A stable identifier for this index instance — lets audit detect index swaps.</summary>
    string IndexId { get; }

    /// <summary>The number of rules indexed.</summary>
    int RuleCount { get; }

    /// <summary>The number of rules in the universal bucket (no parseable signature).</summary>
    int UniversalCount { get; }

    /// <summary>Build the index from a rule set. Replaces any existing state.</summary>
    void Build(RuleSet ruleSet);

    /// <summary>
    /// Return candidate rule ids the supplied section could possibly match.
    /// Always includes universal-bucket rules. Order is deterministic
    /// (ordinal by rule id) so downstream evaluation is reproducible.
    /// </summary>
    IReadOnlyList<string> Lookup(JsonNode section);

    /// <summary>Inspect the signature of a specific rule (audit helper).</summary>
    RuleSignature? GetSignature(string ruleId);
}
