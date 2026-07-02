using System.Text.Json;
using System.Text.Json.Nodes;
using LambdaRag.Core;
using LambdaRag.Core.Facts;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Authoring;

/// <summary>
/// Pillar 12 (#153) — read/write for <see cref="SectionFactSidecar"/> on
/// disk. Uses <see cref="CanonicalJson.Options"/> (camelCase, indented, LF,
/// nulls omitted) so byte-identity replay is guaranteed across OSes.
///
/// <para>Cache path convention:
/// <c>%USERPROFILE%\.lambda-rag\facts\&lt;docHash&gt;.&lt;factSchemaHash&gt;.facts.json</c>.
/// One sidecar per (doc, schema) pair, globally cached so every review of
/// the same doc + schema reuses the extraction.</para>
/// </summary>
public static class SectionFactSidecarIO
{
    public const string DefaultCacheDirName = ".lambda-rag";
    public const string FactsSubDir = "facts";

    public static string ResolveCacheDir(string? overrideDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, DefaultCacheDirName, FactsSubDir);
    }

    /// <summary>
    /// Build the on-disk path for a sidecar given its (docHash, schemaHash)
    /// tuple. Callers can then <see cref="TryLoad"/>, <see cref="Save"/>, or
    /// simply delete the file to force re-extraction.
    /// </summary>
    public static string CachePath(string cacheDir, ContentHash docHash, ContentHash schemaHash)
    {
        Directory.CreateDirectory(cacheDir);
        // Strip the "lr1:" prefix to keep filenames tidy.
        static string Slug(string v) => v.StartsWith(ContentHash.Prefix, StringComparison.Ordinal)
            ? v[ContentHash.Prefix.Length..] : v;
        var name = $"{Slug(docHash.Value)}.{Slug(schemaHash.Value)}.facts.json";
        return Path.Combine(cacheDir, name);
    }

    public static void Save(SectionFactSidecar sidecar, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Normalize section-fact values to a JsonObject shape so on-disk
        // representation is stable (STJ would otherwise write CLR types
        // unpredictably for object?).
        var wire = ToWire(sidecar);
        var json = JsonSerializer.Serialize(wire, CanonicalJson.Options);
        File.WriteAllText(path, json);
    }

    public static SectionFactSidecar? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        var wire = JsonSerializer.Deserialize<WireSidecar>(json, CanonicalJson.Options)
                   ?? throw new InvalidDataException($"Sidecar at {path} did not deserialize.");
        return FromWire(wire);
    }

    /// <summary>
    /// Loads and verifies the cached sidecar. Throws
    /// <see cref="SectionFactSidecarMismatchException"/> when the expected
    /// fingerprint does not match the cached one — the CLI surfaces this
    /// to the operator with a <c>--refresh-facts</c> hint.
    /// </summary>
    public static SectionFactSidecar LoadOrThrow(
        string path,
        ContentHash expectedFingerprint)
    {
        var sidecar = TryLoad(path)
                      ?? throw new FileNotFoundException($"Sidecar not found: {path}");
        if (!string.Equals(sidecar.Fingerprint, expectedFingerprint.Value, StringComparison.Ordinal))
        {
            throw new SectionFactSidecarMismatchException(
                $"Fact sidecar fingerprint mismatch at '{path}'. " +
                $"Expected {expectedFingerprint.Value}, cached {sidecar.Fingerprint ?? "(unset)"}. " +
                "Rerun with --refresh-facts to invalidate, or pin the drifted component.");
        }
        return sidecar;
    }

    // ── Wire types ─────────────────────────────────────────────────────────

    private sealed record WireSidecar(
        string SidecarVersion,
        string DocumentId,
        string FactSchemaId,
        string FactSchemaHash,
        string ModelId,
        string PromptHash,
        string GeneratedAt,
        Dictionary<string, JsonObject> Sections)
    {
        public string? ModelSnapshot { get; init; }
        public string? Fingerprint { get; init; }
        public Dictionary<string, List<string>>? RuleScope { get; init; }
        public List<string>? Warnings { get; init; }
    }

    private static WireSidecar ToWire(SectionFactSidecar s)
    {
        var sections = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (id, bag) in s.Sections)
        {
            var obj = new JsonObject();
            foreach (var (k, v) in bag)
                obj[k] = ToJsonNode(v);
            sections[id] = obj;
        }
        return new WireSidecar(
            s.SidecarVersion, s.DocumentId, s.FactSchemaId, s.FactSchemaHash,
            s.ModelId, s.PromptHash, s.GeneratedAt, sections)
        {
            ModelSnapshot = s.ModelSnapshot,
            Fingerprint = s.Fingerprint,
            RuleScope = s.RuleScope?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal),
            Warnings = s.Warnings?.ToList(),
        };
    }

    private static SectionFactSidecar FromWire(WireSidecar w)
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var (id, obj) in w.Sections)
        {
            var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kvp in obj)
                bag[kvp.Key] = FromJsonNode(kvp.Value);
            sections[id] = bag;
        }
        return new SectionFactSidecar(
            w.SidecarVersion, w.DocumentId, w.FactSchemaId, w.FactSchemaHash,
            w.ModelId, w.PromptHash, w.GeneratedAt, sections)
        {
            ModelSnapshot = w.ModelSnapshot,
            Fingerprint = w.Fingerprint,
            RuleScope = w.RuleScope?.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value,
                StringComparer.Ordinal),
            Warnings = w.Warnings,
        };
    }

    private static JsonNode? ToJsonNode(object? v) => v switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(v.ToString()),
    };

    private static object? FromJsonNode(JsonNode? node)
    {
        if (node is not JsonValue jv) return node?.ToString();
        if (jv.TryGetValue<bool>(out var b)) return b;
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<int>(out var i)) return (long)i;
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<string>(out var s)) return s;
        return jv.ToString();
    }
}
