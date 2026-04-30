using System.Reflection;

namespace LambdaRag.Projection;

/// <summary>
/// Resolves topic maps by ID (embedded resource) or file path. Lists all
/// topic maps shipped with the assembly. Designed so a customer can drop
/// a JSON file next to the binary to onboard a new domain — no recompile.
///
/// Resolution order for <see cref="Load(string)"/>:
///   1. If the spec is a path that exists on disk → load that file.
///   2. If the spec matches an embedded resource id (e.g. "fsi", "fsi.v1",
///      "contract") → load that.
///   3. Otherwise → throw with a list of available IDs.
/// </summary>
public static class TopicMapRegistry
{
    private const string ResourcePrefix = "LambdaRag.Projection.TopicMaps.";
    private const string ResourceSuffix = ".json";

    /// <summary>
    /// Lists embedded topic-map IDs (e.g. "contract.v1", "fsi.v1").
    /// </summary>
    public static IReadOnlyList<string> ListEmbedded()
    {
        var asm = typeof(TopicMapRegistry).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                     && n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            .Select(n => n.Substring(ResourcePrefix.Length, n.Length - ResourcePrefix.Length - ResourceSuffix.Length))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Load a topic map by spec. Spec may be a file path, a full embedded
    /// id ("fsi.v1"), or a domain root ("fsi" — picks newest version).
    /// </summary>
    public static TopicMap Load(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new ArgumentException("Topic map spec required.", nameof(spec));

        if (File.Exists(spec))
        {
            return TopicMap.LoadFromJson(File.ReadAllText(spec));
        }

        var ids = ListEmbedded();
        // Exact match on id
        var match = ids.FirstOrDefault(i => string.Equals(i, spec, StringComparison.OrdinalIgnoreCase));
        // Domain-root match (e.g. "fsi" → "fsi.v1") — pick alphabetically last (= newest semver-string)
        match ??= ids
            .Where(i => i.StartsWith(spec + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i, StringComparer.Ordinal)
            .FirstOrDefault();

        if (match is null)
        {
            throw new FileNotFoundException(
                $"Topic map '{spec}' not found. Not a file on disk and not a known embedded id. "
                + $"Available embedded: {string.Join(", ", ids)}");
        }

        var resourceName = ResourcePrefix + match + ResourceSuffix;
        var asm = typeof(TopicMapRegistry).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not open embedded resource {resourceName}");
        using var reader = new StreamReader(stream);
        return TopicMap.LoadFromJson(reader.ReadToEnd());
    }
}
