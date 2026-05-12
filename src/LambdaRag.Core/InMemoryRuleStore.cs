using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;

namespace LambdaRag.Core;

/// <summary>
/// In-memory IRuleStore implementation for testing.
/// Constructed with a fixture list of rules. Provides deterministic,
/// hermetic retrieval without external dependencies.
/// </summary>
public sealed class InMemoryRuleStore : IRuleStore
{
    private readonly IReadOnlyList<RuleDocument> _documents;

    public InMemoryRuleStore(IReadOnlyList<RuleDocument> documents)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
    }

    public Task<IReadOnlyList<string>> GetAvailableVersionsAsync(
        string rulesetName,
        CancellationToken ct = default)
    {
        var versions = _documents
            .Where(d => d.Status == "approved" && d.RulesetName == rulesetName)
            .Select(d => d.RulesetVersion)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(versions);
    }

    public Task<RuleQueryResult> RetrieveAsync(
        RuleQuery query,
        CancellationToken ct = default)
    {
        // Filter to approved rules matching ruleset name/version
        var candidates = _documents
            .Where(d => d.Status == "approved"
                     && d.RulesetName == query.RulesetName
                     && d.RulesetVersion == query.RulesetVersion)
            .ToList();

        // Simple hybrid retrieval simulation:
        // Score = BM25-like token overlap + cosine similarity (if vectors present)
        var scored = candidates
            .Select(d => new
            {
                Doc = d,
                Score = ComputeScore(d, query.QueryText, query.QueryVector)
            })
            .OrderByDescending(x => x.Score)
            .Take(query.TopK)
            .Select(x => x.Doc)
            .ToList();

        var rules = scored.Select(d => d.Rule).ToList();
        var contentHashes = scored.Select(d => (d.Rule.Id, d.ContentHash)).ToList();
        var snapshotHash = ComputeSnapshotHash(contentHashes);

        var metadata = new RulesetMetadata(
            query.RulesetName,
            query.RulesetVersion,
            "in-memory",
            snapshotHash);

        return Task.FromResult(new RuleQueryResult(rules, metadata));
    }

    public Task<RuleQueryResult> RetrieveAllAsync(
        string rulesetName,
        string rulesetVersion,
        CancellationToken ct = default)
    {
        var filtered = _documents
            .Where(d => d.Status == "approved"
                     && d.RulesetName == rulesetName
                     && d.RulesetVersion == rulesetVersion)
            .OrderBy(d => d.Rule.Id, StringComparer.Ordinal)
            .ToList();

        var rules = filtered.Select(d => d.Rule).ToList();
        var contentHashes = filtered.Select(d => (d.Rule.Id, d.ContentHash)).ToList();
        var snapshotHash = ComputeSnapshotHash(contentHashes);

        var metadata = new RulesetMetadata(
            rulesetName,
            rulesetVersion,
            "in-memory",
            snapshotHash);

        return Task.FromResult(new RuleQueryResult(rules, metadata));
    }

    private static double ComputeScore(RuleDocument doc, string queryText, IReadOnlyList<float>? queryVector)
    {
        double score = 0.0;

        // BM25-like: count token overlaps (case-insensitive)
        var queryTokens = queryText.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', ',', '.', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        var docText = (doc.Rule.NaturalLanguage + " " + doc.Rule.Predicate).ToLowerInvariant();
        var docTokens = docText
            .Split(new[] { ' ', '\t', '\n', ',', '.', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);

        var overlap = docTokens.Count(t => queryTokens.Contains(t));
        score += overlap * 1.0;

        // Vector similarity (if both present)
        if (queryVector is not null && doc.ConceptsVector is not null
            && queryVector.Count == doc.ConceptsVector.Count)
        {
            var cosine = CosineSimilarity(queryVector, doc.ConceptsVector);
            score += cosine * 10.0;  // Weight vector similarity higher
        }

        return score;
    }

    private static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count) return 0.0;

        double dot = 0.0, magA = 0.0, magB = 0.0;
        for (int i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0) return 0.0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    private static string ComputeSnapshotHash(List<(string ruleId, string contentHash)> hashes)
    {
        if (hashes.Count == 0)
            return string.Empty;

        var sorted = hashes.OrderBy(h => h.ruleId, StringComparer.Ordinal).ToList();
        var json = JsonSerializer.Serialize(
            sorted.Select(h => new { ruleId = h.ruleId, contentHash = h.contentHash }),
            new JsonSerializerOptions { WriteIndented = false });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// Rule document for in-memory store fixture.
/// </summary>
public sealed record RuleDocument(
    Rule Rule,
    string Status,
    string RulesetName,
    string RulesetVersion,
    string ContentHash,
    IReadOnlyList<float>? ConceptsVector);
