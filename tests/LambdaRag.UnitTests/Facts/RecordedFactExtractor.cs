// Pillar 12 / Pillar 4 (Flexibility) — a canned IFactExtractor that
// returns pre-recorded per-section fact bags. Used to isolate the
// downstream plumbing (sidecar → FactBag merge → EvaluationService fact
// path) from any live LLM call.
//
// Design rules:
//   * Byte-identical output for the same input map. No wall-clock, no
//     randomness. Determinism pillar.
//   * ModelId + PromptHash are constants so the fingerprint story stays
//     honest even in tests — the sidecar produced by this extractor
//     carries a computed fingerprint just like the Foundry one, so
//     LoadOrThrow-based tests still exercise the fail-loud path.
//   * The extractor is stateless across ExtractAsync invocations except
//     for its constructor-time recording. Building one from an existing
//     SectionFactSidecar is also supported for "replay this canned
//     sidecar verbatim" scenarios.

using System.Text;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Facts;
using LambdaRag.Core.Hashing;

namespace LambdaRag.UnitTests.Facts;

/// <summary>
/// Deterministic, in-memory <see cref="IFactExtractor"/>. Given a map of
/// section-id → fact-bag, replays it verbatim on every call. Used in
/// Pillar 4 (Flexibility) tests to prove the sidecar plumbing is
/// paraphrase-invariant given a correct Pass-1 emission — the LLM
/// itself is out of scope for these tests (see
/// <c>ParaphraseInvarianceTests.LLM_*</c> for the env-gated
/// live-model version).
/// </summary>
internal sealed class RecordedFactExtractor : IFactExtractor
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> _bags;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>>? _ruleScope;

    public string ModelId { get; }
    public string PromptHash { get; }

    public RecordedFactExtractor(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> bags,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ruleScope = null,
        string modelId = "recorded-model",
        string promptHash = "recorded-prompt")
    {
        _bags = bags;
        _ruleScope = ruleScope;
        ModelId = modelId;
        PromptHash = promptHash;
    }

    public Task<SectionFactSidecar> ExtractAsync(
        ProjectedDocument document,
        FactSchema schema,
        CancellationToken ct = default)
    {
        var docHashStr = document.SourceId.Value;
        var schemaHash = schema.Fingerprint().Value;
        // Ordering hash mirrors the FoundrySectionFactExtractor computation
        // (id + '\u001f' + text.Length + '\u001f') so a caller that later
        // switches to the real extractor gets a compatible fingerprint.
        var orderingHash = ComputeOrderingHash(document);
        var fp = SectionFactSidecar.ComputeFingerprint(
            docHashStr, schemaHash, ModelId, PromptHash, orderingHash);

        var sidecar = new SectionFactSidecar(
            SidecarVersion: "1.0",
            DocumentId: docHashStr,
            FactSchemaId: schema.Id,
            FactSchemaHash: schemaHash,
            ModelId: ModelId,
            PromptHash: PromptHash,
            GeneratedAt: "2000-01-01T00:00:00+00:00",
            Sections: _bags)
        {
            Fingerprint = fp.Value,
            RuleScope = _ruleScope,
        };
        return Task.FromResult(sidecar);
    }

    private static string ComputeOrderingHash(ProjectedDocument document)
    {
        var sb = new StringBuilder();
        if (document.Graph["sections"] is System.Text.Json.Nodes.JsonArray arr)
        {
            var i = 0;
            foreach (var node in arr)
            {
                if (node is not System.Text.Json.Nodes.JsonObject obj) { i++; continue; }
                var id = (string?)obj["id"] ?? $"s_{i:D8}";
                var text = (string?)obj["text"] ?? string.Empty;
                sb.Append(id).Append('\u001f').Append(text.Length).Append('\u001f');
                i++;
            }
        }
        return ContentHash.OfString(sb.ToString()).Value;
    }
}
