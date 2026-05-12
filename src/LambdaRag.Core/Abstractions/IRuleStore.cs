namespace LambdaRag.Core.Abstractions;

/// <summary>
/// Query abstraction for retrieving approved rules from the authoritative store.
/// Production implementation: AzureSearchRuleStore (queries lambda-rag-rules index).
/// Test implementation: InMemoryRuleStore (fixture-backed, deterministic).
/// </summary>
public interface IRuleStore
{
    /// <summary>
    /// Returns distinct rulesetVersion values for the specified ruleset name,
    /// filtered to status='approved'. Used by CLI to enumerate available versions
    /// when no version is pinned.
    /// </summary>
    Task<IReadOnlyList<string>> GetAvailableVersionsAsync(
        string rulesetName,
        CancellationToken ct = default);

    /// <summary>
    /// Hybrid retrieval: BM25 + vector over approved rules for the specified
    /// ruleset name and version. Returns up to topK rules ranked by relevance.
    /// </summary>
    Task<RuleQueryResult> RetrieveAsync(
        RuleQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all approved rules for the specified ruleset name and version.
    /// Used by tests and for small rulesets where exhaustive evaluation is feasible.
    /// </summary>
    Task<RuleQueryResult> RetrieveAllAsync(
        string rulesetName,
        string rulesetVersion,
        CancellationToken ct = default);
}

/// <summary>
/// Query parameters for hybrid rule retrieval.
/// </summary>
public sealed record RuleQuery(
    string RulesetName,
    string RulesetVersion,
    string QueryText,
    IReadOnlyList<float>? QueryVector,
    int TopK);

/// <summary>
/// Result of a rule query, including matched rules and resolved metadata.
/// </summary>
public sealed record RuleQueryResult(
    IReadOnlyList<Domain.Rule> Rules,
    RulesetMetadata Metadata);

/// <summary>
/// Resolved metadata for a ruleset retrieved from the store.
/// </summary>
public sealed record RulesetMetadata(
    string RulesetName,
    string RulesetVersion,
    string IndexEndpoint,
    string SnapshotHash);
