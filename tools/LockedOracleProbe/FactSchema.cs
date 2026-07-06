using System.Text.Json.Serialization;

namespace LambdaRag.Tools.LockedOracleProbe;

/// <summary>
/// The 5-field structured-fact schema the LLM is asked to fill. Chosen
/// to mirror an architecture-review fact-extraction task. Fields are
/// intentionally simple types so per-field agreement is measurable.
/// </summary>
internal sealed record StructuredFacts
{
    [JsonPropertyName("system_name")]
    public string? SystemName { get; init; }

    [JsonPropertyName("encryption_in_transit_enabled")]
    public bool? EncryptionInTransitEnabled { get; init; }

    [JsonPropertyName("encryption_at_rest_enabled")]
    public bool? EncryptionAtRestEnabled { get; init; }

    /// <summary>Enum: password | mfa | certificate | oauth | none | unspecified.</summary>
    [JsonPropertyName("authentication_method")]
    public string? AuthenticationMethod { get; init; }

    /// <summary>Free-text country or region name.</summary>
    [JsonPropertyName("data_residency_region")]
    public string? DataResidencyRegion { get; init; }
}

internal static class SchemaText
{
    public const string SchemaVersion = "v1";

    // JSON schema description passed to the model as part of the system
    // prompt. Kept short and unambiguous.
    public const string DescriptionForPrompt = """
        You must respond with a single JSON object matching this schema and nothing else.

        {
          "system_name":                    string,
          "encryption_in_transit_enabled":  boolean,
          "encryption_at_rest_enabled":     boolean,
          "authentication_method":          one of: "password" | "mfa" | "certificate" | "oauth" | "none" | "unspecified",
          "data_residency_region":          string (country or Azure region name)
        }

        Rules:
        - Output MUST be valid JSON. No prose, no code fences, no commentary.
        - If a field cannot be determined from the document, use null.
        - authentication_method: if MFA is enforced on top of any other method, answer "mfa".
        - data_residency_region: report where customer data is stored, not where telemetry goes.
        """;

    public const string SystemPromptVersion = "v1";

    public const string SystemPrompt =
        "You are a strict fact extractor for architecture-review documents. " +
        DescriptionForPrompt;
}
