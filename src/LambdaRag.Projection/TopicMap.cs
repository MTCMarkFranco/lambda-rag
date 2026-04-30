using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LambdaRag.Projection;

/// <summary>
/// Data-driven topic map. Replaces the hardcoded keyword arrays in the
/// projector so new domains (DPA, architecture-review, clinical-trial, etc.)
/// can be onboarded by dropping a JSON file — no recompile.
///
/// Schema (informal):
/// <code>
/// {
///   "domain": "contract",
///   "version": "1.0.0",
///   "topics": [
///     { "id": "liability", "axis": "clause", "keywords": ["liabil"] },
///     ...
///   ],
///   "axes": {
///     "jurisdiction": {
///       "headingPatterns": ["australia","austria","canada",...]
///     }
///   },
///   "amendmentPatterns": [ "(?i)(replace|supplement)\\s+...titled\\s+\"([^\"]+)\"" ]
/// }
/// </code>
///
/// Behaviour:
///   * Topics whose <c>axis</c> is null (the default) participate in primary
///     classification — first-match-wins in declared order.
///   * Topics on a non-null axis (e.g. <c>jurisdiction</c>) are added to the
///     section's multi-label topic vector but do NOT become the primary topic.
///   * <c>amendmentPatterns</c> are applied to the section body. The first
///     capture group is treated as the parent-section heading; the projector
///     resolves it back to a previously-projected section and inherits the
///     primary topic.
/// </summary>
public sealed record TopicMap(
    string Domain,
    string Version,
    IReadOnlyList<TopicDefinition> Topics,
    IReadOnlyDictionary<string, AxisDefinition> Axes,
    IReadOnlyList<string> AmendmentPatterns)
{
    private Regex[]? _compiledAmendments;

    public Regex[] CompiledAmendmentPatterns =>
        _compiledAmendments ??= AmendmentPatterns
            .Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.CultureInvariant))
            .ToArray();

    public static TopicMap LoadFromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<TopicMapDto>(json, JsonOpts)
            ?? throw new InvalidOperationException("Topic map JSON deserialised to null.");
        return new TopicMap(
            Domain: dto.Domain ?? throw new InvalidOperationException("Topic map missing 'domain'."),
            Version: dto.Version ?? "1.0.0",
            Topics: (dto.Topics ?? new List<TopicDefinitionDto>())
                .Select(t => new TopicDefinition(
                    Id: t.Id ?? throw new InvalidOperationException("Topic missing 'id'."),
                    Axis: t.Axis,
                    Keywords: (IReadOnlyList<string>)(t.Keywords ?? new List<string>())))
                .ToList(),
            Axes: (dto.Axes ?? new Dictionary<string, AxisDefinitionDto>())
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => new AxisDefinition(
                        HeadingPatterns: (IReadOnlyList<string>)(kvp.Value?.HeadingPatterns ?? new List<string>()))),
            AmendmentPatterns: (IReadOnlyList<string>)(dto.AmendmentPatterns ?? new List<string>()));
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class TopicMapDto
    {
        public string? Domain { get; set; }
        public string? Version { get; set; }
        public List<TopicDefinitionDto>? Topics { get; set; }
        public Dictionary<string, AxisDefinitionDto>? Axes { get; set; }
        public List<string>? AmendmentPatterns { get; set; }
    }

    private sealed class TopicDefinitionDto
    {
        public string? Id { get; set; }
        public string? Axis { get; set; }
        public List<string>? Keywords { get; set; }
    }

    private sealed class AxisDefinitionDto
    {
        public List<string>? HeadingPatterns { get; set; }
    }
}

public sealed record TopicDefinition(string Id, string? Axis, IReadOnlyList<string> Keywords);
public sealed record AxisDefinition(IReadOnlyList<string> HeadingPatterns);
