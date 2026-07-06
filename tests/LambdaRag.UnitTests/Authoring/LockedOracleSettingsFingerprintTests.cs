using FluentAssertions;
using LambdaRag.Authoring;
using Xunit;

namespace LambdaRag.UnitTests.Authoring;

// Issue #181 (FID Lottery audit follow-up) — the extended fingerprint must
// round-trip identically for identical inputs and diverge on ANY single-field
// change. This is the Locked Oracle Phase-1 invariance guarantee.
public class LockedOracleSettingsFingerprintTests
{
    [Fact]
    public void Identical_settings_produce_identical_fingerprints()
    {
        var a = new LockedOracleSettings(0.0f, 1.0f, 42L, 800, "gpt-5.4-mini", "canada");
        var b = new LockedOracleSettings(0.0f, 1.0f, 42L, 800, "gpt-5.4-mini", "canada");
        a.Fingerprint().Should().Be(b.Fingerprint());
    }

    [Fact]
    public void Fingerprint_uses_content_hash_prefix()
        => LockedOracleSettings.Default.Fingerprint().Should().StartWith("lr1:");

    [Theory]
    [InlineData(0.1f, 1.0f, 42L, 800, "gpt-5.4-mini", "canada")]      // temperature diff
    [InlineData(0.0f, 0.95f, 42L, 800, "gpt-5.4-mini", "canada")]     // topP diff
    [InlineData(0.0f, 1.0f, 43L, 800, "gpt-5.4-mini", "canada")]      // seed diff
    [InlineData(0.0f, 1.0f, 42L, 1000, "gpt-5.4-mini", "canada")]     // maxOutputTokens diff
    [InlineData(0.0f, 1.0f, 42L, 800, "gpt-5.4", "canada")]           // deployment diff
    [InlineData(0.0f, 1.0f, 42L, 800, "gpt-5.4-mini", "eastus")]      // region diff
    public void Any_single_field_change_produces_different_fingerprint(
        float temp, float topP, long seed, int maxTok, string dep, string region)
    {
        var baseline = new LockedOracleSettings(0.0f, 1.0f, 42L, 800, "gpt-5.4-mini", "canada");
        var altered = new LockedOracleSettings(temp, topP, seed, maxTok, dep, region);
        altered.Fingerprint().Should().NotBe(baseline.Fingerprint());
    }

    [Fact]
    public void Nulls_participate_honestly()
    {
        var withNulls = new LockedOracleSettings(0.0f, 1.0f, 42L, 800, null, null);
        var withDep = new LockedOracleSettings(0.0f, 1.0f, 42L, 800, "gpt-5.4-mini", null);
        var withRegion = new LockedOracleSettings(0.0f, 1.0f, 42L, 800, null, "canada");
        withNulls.Fingerprint().Should().NotBe(withDep.Fingerprint());
        withNulls.Fingerprint().Should().NotBe(withRegion.Fingerprint());
        withDep.Fingerprint().Should().NotBe(withRegion.Fingerprint());
    }

    [Fact]
    public void WithRuntime_only_overrides_deployment_and_region()
    {
        var seed = new LockedOracleSettings(0.0f, 1.0f, 42L, 800);
        var runtime = seed.WithRuntime("gpt-5.4-mini", "canada");
        runtime.Temperature.Should().Be(seed.Temperature);
        runtime.TopP.Should().Be(seed.TopP);
        runtime.Seed.Should().Be(seed.Seed);
        runtime.MaxOutputTokens.Should().Be(seed.MaxOutputTokens);
        runtime.DeploymentId.Should().Be("gpt-5.4-mini");
        runtime.Region.Should().Be("canada");
    }
}
