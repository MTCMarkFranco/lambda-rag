using Microsoft.Extensions.AI;

namespace LambdaRag.Authoring.Embeddings;

/// <summary>
/// Production embedding provider that delegates to any
/// <c>Microsoft.Extensions.AI.IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>.
/// Pair this with a builder that wires up Azure OpenAI / Azure Foundry
/// (e.g. <c>new AzureOpenAIClient(endpoint, credential).GetEmbeddingClient(deployment).AsIEmbeddingGenerator()</c>).
///
/// Determinism contract:
/// • Every embedding result is L2-normalised before persisting / returning,
///   so cosine = dot product and small numerical noise from the model
///   provider does not drift cosine values across runs.
/// • Vectors are persisted to a <see cref="FileBackedEmbeddingCache"/> on
///   first request; subsequent requests for the same (model, text) pair
///   hit the cache and never call the provider, which is what makes
///   downstream evaluation byte-identical on replay.
/// • <see cref="EmbedderId"/> includes the pinned model identifier so
///   audit can detect drift between authoring time and runtime.
/// </summary>
public sealed class AzureFoundryEmbeddingProvider : IRuleEmbedder
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly FileBackedEmbeddingCache? _cache;

    public AzureFoundryEmbeddingProvider(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string modelId,
        int dimensions,
        FileBackedEmbeddingCache? cache = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required", nameof(modelId));
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        ModelId = modelId;
        Dimensions = dimensions;
        EmbedderId = $"azure-foundry:{modelId}/{dimensions}";
        _cache = cache;
    }

    public int Dimensions { get; }
    public string EmbedderId { get; }
    public string ModelId { get; }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));

        if (_cache is not null && _cache.TryRead(text, out var cached))
            return cached;

        var generated = await _generator.GenerateAsync(new[] { text }, options: null, cancellationToken: ct)
            .ConfigureAwait(false);
        var vector = generated[0].Vector.ToArray();
        if (vector.Length != Dimensions)
            throw new InvalidOperationException(
                $"Provider returned a vector of length {vector.Length} but provider was configured for {Dimensions}. " +
                "Mismatch indicates the deployment serves a different model — fix configuration before continuing.");

        L2NormalizeInPlace(vector);
        _cache?.Write(text, vector);
        return vector;
    }

    private static void L2NormalizeInPlace(float[] v)
    {
        double sumSq = 0;
        for (var i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
        var norm = Math.Sqrt(sumSq);
        if (norm <= double.Epsilon) return;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
    }
}
