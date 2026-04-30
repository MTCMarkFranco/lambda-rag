using System.Security.Cryptography;
using System.Text;

namespace LambdaRag.Authoring;

/// <summary>
/// Computes a dense embedding for a piece of text. Implementations may use
/// a real embedding model (Azure OpenAI text-embedding-3-large, etc.) or
/// a deterministic hash-based fake for unit tests.
///
/// All implementations must be:
/// • Deterministic — same input bytes produce the same vector.
/// • Pure — no I/O at runtime evaluation. Embeddings are computed once at
///   authoring time and stored on the rule.
/// </summary>
public interface IRuleEmbedder
{
    /// <summary>Returns a fixed-length vector for the supplied text.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>The embedding dimensionality. Must be constant for a given embedder.</summary>
    int Dimensions { get; }

    /// <summary>
    /// A stable identifier for this embedder — e.g.
    /// <c>"deterministic-sha256/32"</c> or <c>"azure-openai:text-embedding-3-large"</c>.
    /// Stored alongside vectors so audit can detect mismatches.
    /// </summary>
    string EmbedderId { get; }
}

/// <summary>
/// Deterministic embedder for tests / offline use. Derives a 32-dimensional
/// L2-normalised float vector from the SHA-256 hash of the input. The vector
/// is reproducible across machines, processes, and runs — same string in →
/// byte-identical vector out — which is what unit tests need.
///
/// This is NOT a semantic embedder. It is suitable only for verifying the
/// shape of the coverage / similarity pipeline, not for actual retrieval.
/// </summary>
public sealed class DeterministicHashEmbedder : IRuleEmbedder
{
    public int Dimensions => 32;
    public string EmbedderId => "deterministic-sha256/32";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        Span<byte> hash = stackalloc byte[64]; // 64 bytes from two SHA-256 rounds
        var input = Encoding.UTF8.GetBytes(text ?? string.Empty);
        var h1 = SHA256.HashData(input);
        var h2 = SHA256.HashData(h1);
        h1.AsSpan().CopyTo(hash[..32]);
        h2.AsSpan().CopyTo(hash[32..]);

        var vector = new float[Dimensions];
        for (var i = 0; i < Dimensions; i++)
        {
            // Map a pair of hash bytes to a deterministic float in [-1, 1].
            var raw = (short)((hash[i * 2] << 8) | hash[i * 2 + 1]);
            vector[i] = raw / (float)short.MaxValue;
        }
        return Task.FromResult(L2Normalize(vector));
    }

    private static float[] L2Normalize(float[] v)
    {
        double sumSq = 0;
        for (var i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
        var norm = Math.Sqrt(sumSq);
        if (norm <= double.Epsilon) return v;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return v;
    }

    /// <summary>
    /// Cosine similarity of two unit vectors. Returns 0 for empty / mismatched inputs.
    /// </summary>
    public static double Cosine(IReadOnlyList<float>? a, IReadOnlyList<float>? b)
    {
        if (a is null || b is null || a.Count == 0 || a.Count != b.Count) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
