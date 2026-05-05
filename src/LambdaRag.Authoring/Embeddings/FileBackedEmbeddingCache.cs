using System.Security.Cryptography;
using System.Text;

namespace LambdaRag.Authoring.Embeddings;

/// <summary>
/// File-backed cache for embedding vectors keyed by
/// <c>sha256(modelId || "\n" || normalisedText)</c>. Cache files are flat
/// little-endian float32 blobs. Storage layout:
///
/// <code>
/// &lt;root&gt;/&lt;model-folder&gt;/&lt;sha256-hex&gt;.f32
/// </code>
///
/// The cache is the single source of determinism for embedding-backed
/// rules: hit the cache and the entire evaluation path is offline,
/// byte-identical to the previous run, and survives process restarts.
/// Misses are filled by the underlying provider and persisted before being
/// returned, so the second run is always offline.
///
/// Threading: writes use a temp-file + atomic rename so a partial write
/// never corrupts a key. Concurrent readers see either the previous file
/// or the new one, never a torn one.
/// </summary>
public sealed class FileBackedEmbeddingCache
{
    private readonly string _root;
    private readonly string _modelId;
    private readonly int _dimensions;

    public FileBackedEmbeddingCache(string root, string modelId, int dimensions)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root required", nameof(root));
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("modelId required", nameof(modelId));
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        _root = root;
        _modelId = modelId;
        _dimensions = dimensions;
        Directory.CreateDirectory(Path.Combine(_root, ToFolderName(modelId)));
    }

    /// <summary>
    /// Stable cache key. Normalisation is a strict invariant: trim, collapse
    /// internal runs of whitespace to a single space, and lowercase using
    /// invariant culture. Mismatched normalisation between authoring and
    /// runtime would split the cache silently — change here is a breaking
    /// artifact change.
    /// </summary>
    public static string ComputeKey(string modelId, string text)
    {
        var normalised = NormalizeText(text);
        var bytes = Encoding.UTF8.GetBytes($"{modelId}\n{normalised}");
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (lastWasSpace) continue;
                sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }

    public bool TryRead(string text, out float[] vector)
    {
        vector = Array.Empty<float>();
        var path = PathFor(text);
        if (!File.Exists(path)) return false;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != _dimensions * sizeof(float))
                return false;
            vector = new float[_dimensions];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Write(string text, float[] vector)
    {
        if (vector.Length != _dimensions)
            throw new InvalidOperationException(
                $"Vector dim {vector.Length} does not match cache dim {_dimensions}.");
        var path = PathFor(text);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    private string PathFor(string text)
    {
        var key = ComputeKey(_modelId, text);
        return Path.Combine(_root, ToFolderName(_modelId), key + ".f32");
    }

    private static string ToFolderName(string modelId)
    {
        var sb = new StringBuilder(modelId.Length);
        foreach (var ch in modelId)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        }
        return sb.ToString();
    }
}
