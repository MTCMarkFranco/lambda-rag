using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Facts;
using LambdaRag.Core.Hashing;
using Xunit;

namespace LambdaRag.UnitTests.Facts;

/// <summary>
/// Pillar 12 (#153) — Phase 3. Sidecar disk round-trip + fingerprint-drift
/// throw semantics. No Foundry calls: we save/load through <see cref="SectionFactSidecarIO"/>
/// directly.
/// </summary>
public class SectionFactSidecarIOTests
{
    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lambda-rag-facts-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "sidecar.facts.json");
    }

    private static SectionFactSidecar Sample()
    {
        var docHash = ContentHash.OfString("doc").Value;
        var schemaHash = ContentHash.OfString("schema").Value;
        var fp = SectionFactSidecar.ComputeFingerprint(docHash, schemaHash, "m1", "p1", "o1").Value;
        var sections = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
        {
            ["s_1"] = new Dictionary<string, object?>
            {
                ["encryption_declared"] = true,
                ["key_rotation_days"] = 90L,
                ["tls_min_version"] = "1.2",
                ["storage_region"] = "eu-west-1",
            },
            ["s_2"] = new Dictionary<string, object?>
            {
                ["encryption_declared"] = false,
            },
        };
        return new SectionFactSidecar(
            SidecarVersion: "1.0",
            DocumentId: docHash,
            FactSchemaId: "s1",
            FactSchemaHash: schemaHash,
            ModelId: "m1",
            PromptHash: "p1",
            GeneratedAt: "2026-07-01T00:00:00Z",
            Sections: sections)
        {
            Fingerprint = fp,
            Warnings = new[] { "s_2: supporting_quote not found" },
        };
    }

    [Fact]
    public void RoundTrip_preserves_values_and_types()
    {
        var path = TempFile();
        var original = Sample();
        SectionFactSidecarIO.Save(original, path);
        var loaded = SectionFactSidecarIO.TryLoad(path);

        loaded.Should().NotBeNull();
        loaded!.DocumentId.Should().Be(original.DocumentId);
        loaded.Fingerprint.Should().Be(original.Fingerprint);
        loaded.Sections["s_1"]["encryption_declared"].Should().Be(true);
        loaded.Sections["s_1"]["key_rotation_days"].Should().Be(90L);
        loaded.Sections["s_1"]["tls_min_version"].Should().Be("1.2");
        loaded.Sections["s_2"]["encryption_declared"].Should().Be(false);
        loaded.Warnings.Should().NotBeNull();
        loaded.Warnings!.Should().ContainSingle();
    }

    [Fact]
    public void RoundTrip_is_byte_identical_on_second_save()
    {
        var path1 = TempFile();
        var path2 = Path.Combine(Path.GetDirectoryName(path1)!, "second.json");
        var original = Sample();
        SectionFactSidecarIO.Save(original, path1);
        var loaded = SectionFactSidecarIO.TryLoad(path1)!;
        SectionFactSidecarIO.Save(loaded, path2);
        File.ReadAllBytes(path1).Should().BeEquivalentTo(File.ReadAllBytes(path2));
    }

    [Fact]
    public void LoadOrThrow_throws_on_fingerprint_mismatch()
    {
        var path = TempFile();
        var original = Sample();
        SectionFactSidecarIO.Save(original, path);
        var wrongFp = ContentHash.OfString("nope");
        Action act = () => SectionFactSidecarIO.LoadOrThrow(path, wrongFp);
        act.Should().Throw<SectionFactSidecarMismatchException>()
            .WithMessage("*--refresh-facts*");
    }

    [Fact]
    public void LoadOrThrow_returns_sidecar_on_match()
    {
        var path = TempFile();
        var original = Sample();
        SectionFactSidecarIO.Save(original, path);
        var expected = new ContentHash(original.Fingerprint!);
        var loaded = SectionFactSidecarIO.LoadOrThrow(path, expected);
        loaded.DocumentId.Should().Be(original.DocumentId);
    }

    [Fact]
    public void CachePath_uses_expected_layout()
    {
        var docHash = ContentHash.OfString("doc-a");
        var schemaHash = ContentHash.OfString("schema-b");
        var dir = Path.Combine(Path.GetTempPath(), "lambda-rag-facts-tests", Guid.NewGuid().ToString("N"));
        var path = SectionFactSidecarIO.CachePath(dir, docHash, schemaHash);
        path.Should().StartWith(dir);
        path.Should().EndWith(".facts.json");
        Path.GetFileName(path).Should().NotContain("lr1:");
    }

    [Fact]
    public void ResolveCacheDir_uses_override_when_given()
    {
        var explicitDir = @"C:\some\path";
        SectionFactSidecarIO.ResolveCacheDir(explicitDir).Should().Be(explicitDir);
    }

    [Fact]
    public void ResolveCacheDir_defaults_under_userprofile_when_null()
    {
        var resolved = SectionFactSidecarIO.ResolveCacheDir(null);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        resolved.Should().StartWith(home);
        resolved.Should().EndWith(Path.Combine(".lambda-rag", "facts"));
    }
}
