using LambdaRag.Core.Hashing;

namespace LambdaRag.Authoring;

/// <summary>
/// Locked Oracle Phase 1 (#177) — the sampling knobs pinned when calling
/// the LLM behind <see cref="FoundrySectionFactExtractor"/>. Folded into
/// the extractor's <c>PromptHash</c> so any change forces a loud cache
/// invalidation (<see cref="LambdaRag.Core.Facts.SectionFactSidecarMismatchException"/>).
///
/// <para>Default: <c>Temperature=0</c>, <c>TopP=1</c>, <c>Seed=42</c> —
/// empirically validated at 100% raw byte-identity over 1200 sequential
/// calls in Phase 0 (see issue #175 and PR #176).</para>
///
/// <para>Set any field to <c>null</c> to leave the knob unset — required
/// for reasoning models that reject sampling parameters. Doing so weakens
/// the idempotency guarantee and callers should treat resulting sidecars
/// as "best-effort" rather than "locked".</para>
/// </summary>
public sealed record LockedOracleSettings(
    float? Temperature,
    float? TopP,
    long? Seed,
    int? MaxOutputTokens = 800,
    string? DeploymentId = null,
    string? Region = null)
{
    public static readonly LockedOracleSettings Default = new(
        Temperature: 0.0f,
        TopP: 1.0f,
        Seed: 42,
        MaxOutputTokens: 800,
        DeploymentId: null,
        Region: null);

    /// <summary>
    /// Unpinned — every sampling knob null. Use only when the model does not
    /// accept the corresponding parameter (e.g. some reasoning models reject
    /// <c>temperature</c> and <c>top_p</c>). Not compatible with the Locked
    /// Oracle idempotency contract. <c>MaxOutputTokens</c>, <c>DeploymentId</c>
    /// and <c>Region</c> remain fingerprint inputs.
    /// </summary>
    public static readonly LockedOracleSettings Unpinned = new(
        Temperature: null,
        TopP: null,
        Seed: null,
        MaxOutputTokens: 800,
        DeploymentId: null,
        Region: null);

    /// <summary>
    /// Return a copy with the runtime-only observation fields populated. The
    /// factory calls this with the deployment name resolved from config and
    /// the region parsed from the endpoint URL, so that any endpoint / region
    /// swap invalidates the sidecar cache loudly.
    /// </summary>
    public LockedOracleSettings WithRuntime(string? deploymentId, string? region)
        => this with { DeploymentId = deploymentId, Region = region };

    /// <summary>
    /// Stable fingerprint of the settings; folded into the extractor
    /// <c>PromptHash</c>. Using the invariant-culture format keeps the
    /// hash stable across locales. Version tag bumped to <c>v2</c> when the
    /// three runtime fields were added (issue #181) — every pre-v2 cached
    /// sidecar is invalidated loudly via <c>SectionFactSidecarMismatchException</c>.
    /// </summary>
    public string Fingerprint()
        => ContentHash.Compose(
            "locked-oracle-settings-v2",
            Temperature?.ToString("G9", System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            TopP?.ToString("G9", System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            Seed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            MaxOutputTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            DeploymentId ?? "null",
            Region ?? "null").Value;
}
