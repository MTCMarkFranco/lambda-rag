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
    long? Seed)
{
    public static readonly LockedOracleSettings Default = new(
        Temperature: 0.0f,
        TopP: 1.0f,
        Seed: 42);

    /// <summary>
    /// Unpinned — every knob null. Use only when the model does not accept
    /// the corresponding parameter (e.g. some reasoning models reject
    /// <c>temperature</c> and <c>top_p</c>). Not compatible with the Locked
    /// Oracle idempotency contract.
    /// </summary>
    public static readonly LockedOracleSettings Unpinned = new(
        Temperature: null,
        TopP: null,
        Seed: null);

    /// <summary>
    /// Stable fingerprint of the settings; folded into the extractor
    /// <c>PromptHash</c>. Using the invariant-culture format keeps the
    /// hash stable across locales.
    /// </summary>
    public string Fingerprint()
        => ContentHash.Compose(
            "locked-oracle-settings-v1",
            Temperature?.ToString("G9", System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            TopP?.ToString("G9", System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            Seed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null").Value;
}
