using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LambdaRag.Core.Facts;

/// <summary>
/// Pillar 12 (#153) — deterministic phrase-to-ISO-8601-duration normalizer.
/// The LLM classifier emits verbatim phrases from section text ("every 90
/// days", "quarterly"); this component converts them to canonical
/// durations (<c>P90D</c>) so lambda expressions can compare integers.
///
/// <para>Determinism guarantees:
/// <list type="bullet">
///   <item>The phrase → ISO map is shipped as an embedded resource
///     (<c>normalizer.v1.json</c>). The mapping-table hash is part of
///     the sidecar fingerprint so table updates invalidate stale caches.</item>
///   <item>Fallback to regex-extracted integers ("every 45 days" → P45D)
///     is deterministic and versioned via <see cref="Version"/>.</item>
///   <item>Unrecognized input returns null; callers decide whether that
///     is a Fail or a passthrough.</item>
/// </list>
/// </para>
/// </summary>
public sealed class DurationNormalizer
{
    private static readonly Lazy<DurationNormalizer> _default =
        new(() => Load(embeddedResource: "LambdaRag.Core.Facts.normalizer.v1.json"));

    /// <summary>Process-wide default normalizer, loaded from the embedded map.</summary>
    public static DurationNormalizer Default => _default.Value;

    /// <summary>Mapping-table version string (from the JSON <c>version</c> field).</summary>
    public string Version { get; }

    /// <summary>SHA-256 of the raw mapping-table bytes (folded into promptHash).</summary>
    public Hashing.ContentHash TableHash { get; }

    private readonly IReadOnlyDictionary<string, string> _map;

    private static readonly Regex EveryNDays =
        new(@"^\s*every\s+(\d{1,4})[\-\s]?day", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Pillar 4 (Flexibility): match "on a N-day <anything>" including
    // "cycle", "rotation", "window", "cadence", "interval", "period",
    // "retention window", etc. Policy language uses many trailing
    // nouns; the cadence is fully specified by "N-day" so the trailing
    // noun is decorative. Bumping normalizer.Version invalidates any
    // stale sidecar cached against the narrower v1 grammar.
    private static readonly Regex OnANDayCycle =
        new(@"^\s*on\s+a\s+(\d{1,4})[\-\s]?day\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private DurationNormalizer(
        string version,
        IReadOnlyDictionary<string, string> map,
        Hashing.ContentHash tableHash)
    {
        Version = version;
        _map = map;
        TableHash = tableHash;
    }

    /// <summary>
    /// Load a normalizer from a JSON stream containing
    /// <c>{ "version": "...", "durations": { "phrase": "P30D", ... } }</c>.
    /// </summary>
    public static DurationNormalizer Load(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var doc = JsonDocument.Parse(bytes);
        var version = doc.RootElement.GetProperty("version").GetString()
                      ?? throw new InvalidDataException("normalizer.json missing 'version'.");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.GetProperty("durations").EnumerateObject())
        {
            var value = prop.Value.GetString()
                        ?? throw new InvalidDataException($"normalizer.json '{prop.Name}' value must be a string.");
            map[prop.Name.Trim()] = value;
        }
        return new DurationNormalizer(version, map, Hashing.ContentHash.OfBytes(bytes));
    }

    internal static DurationNormalizer Load(string embeddedResource)
    {
        var asm = typeof(DurationNormalizer).Assembly;
        using var s = asm.GetManifestResourceStream(embeddedResource)
                      ?? throw new InvalidOperationException(
                          $"Embedded resource '{embeddedResource}' not found. Available: "
                          + string.Join(", ", asm.GetManifestResourceNames()));
        return Load(s);
    }

    /// <summary>
    /// Convert a phrase to an ISO-8601 duration. Returns null when the
    /// input is unrecognized.
    /// </summary>
    public string? Normalize(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return null;
        var canonical = Canonicalize(phrase);

        if (_map.TryGetValue(canonical, out var hit))
            return hit;

        var m = EveryNDays.Match(canonical);
        if (m.Success && int.TryParse(m.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d1) && d1 > 0)
            return $"P{d1}D";

        m = OnANDayCycle.Match(canonical);
        if (m.Success && int.TryParse(m.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d2) && d2 > 0)
            return $"P{d2}D";

        return null;
    }

    /// <summary>
    /// Convert a phrase (or an ISO-8601 duration) to whole days. Returns
    /// null when unrecognized.
    /// </summary>
    public int? NormalizeToDays(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return null;
        var iso = LooksLikeIso(phrase) ? phrase.Trim() : Normalize(phrase);
        if (iso is null) return null;
        return IsoToDays(iso);
    }

    private static bool LooksLikeIso(string s)
        => s.Length >= 3 && (s[0] == 'P' || s[0] == 'p');

    private static int? IsoToDays(string iso)
    {
        var m = Regex.Match(iso, @"^P(?:(\d+)W|(\d+)D)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        if (m.Groups[1].Success && int.TryParse(m.Groups[1].ValueSpan, out var w)) return w * 7;
        if (m.Groups[2].Success && int.TryParse(m.Groups[2].ValueSpan, out var d)) return d;
        return null;
    }

    private static string Canonicalize(string s)
    {
        var trimmed = s.Trim().TrimEnd('.', ',', ';');
        return Regex.Replace(trimmed, @"\s+", " ");
    }
}
