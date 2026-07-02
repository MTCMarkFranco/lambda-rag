using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Facts;
using Xunit;

namespace LambdaRag.UnitTests.Facts;

public class FactBagTests
{
    private static FactSchema MakeSchema() => new(
        "test", "1",
        new[]
        {
            new FactConcept("encryption_declared", FactType.Boolean, ""),
            new FactConcept("key_rotation_days", FactType.Integer, ""),
            new FactConcept("tls_min_version", FactType.Enum, "") { EnumValues = new[] { "1.2", "1.3" } },
            new FactConcept("region", FactType.Text, ""),
        });

    private static Dictionary<string, object?> Facts(params (string k, object? v)[] pairs)
        => pairs.ToDictionary(p => p.k, p => p.v);

    [Fact]
    public void Get_Returns_Null_On_Missing_Concept()
    {
        var bag = new FactBag();
        bag.Get("anything").Should().BeNull();
    }

    [Fact]
    public void Merge_Single_Section_Copies_Values()
    {
        var bag = new FactBag();
        bag.Merge("s1", Facts(("encryption_declared", true), ("key_rotation_days", 90L)), MakeSchema());
        bag.Get("encryption_declared").Should().Be(true);
        bag.Get("key_rotation_days").Should().Be(90L);
        bag.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void Merge_Boolean_Union_Is_Or()
    {
        var bag = new FactBag();
        bag.Merge("s1", Facts(("encryption_declared", false)), MakeSchema());
        bag.Merge("s2", Facts(("encryption_declared", true)), MakeSchema());
        bag.Get("encryption_declared").Should().Be(true);
        bag.Conflicts.Should().ContainSingle(c => c.ConceptName == "encryption_declared" && c.Resolver == "boolean_or");
    }

    [Fact]
    public void Merge_Boolean_Same_Value_No_Conflict()
    {
        var bag = new FactBag();
        bag.Merge("s1", Facts(("encryption_declared", true)), MakeSchema());
        bag.Merge("s2", Facts(("encryption_declared", true)), MakeSchema());
        bag.Get("encryption_declared").Should().Be(true);
        bag.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void Merge_Integer_Min_Wins()
    {
        var bag = new FactBag();
        bag.Merge("s1", Facts(("key_rotation_days", 180L)), MakeSchema());
        bag.Merge("s2", Facts(("key_rotation_days", 90L)), MakeSchema());
        bag.Merge("s3", Facts(("key_rotation_days", 365L)), MakeSchema());
        bag.Get("key_rotation_days").Should().Be(90L);
        bag.Conflicts.Should().HaveCount(2);
        bag.Conflicts.Should().OnlyContain(c => c.Resolver == "min");
    }

    [Fact]
    public void Merge_Enum_First_Non_Null_Wins()
    {
        var bag = new FactBag();
        bag.Merge("s1", Facts(("tls_min_version", "1.2")), MakeSchema());
        bag.Merge("s2", Facts(("tls_min_version", "1.3")), MakeSchema());
        bag.Get("tls_min_version").Should().Be("1.2");
        bag.Conflicts.Should().ContainSingle(c => c.Resolver == "first_non_null");
    }

    [Fact]
    public void Merge_Enum_Skips_Nulls()
    {
        var bag = new FactBag();
        bag.Merge("s1", Facts(("tls_min_version", null)), MakeSchema());
        bag.Merge("s2", Facts(("tls_min_version", "1.3")), MakeSchema());
        bag.Get("tls_min_version").Should().Be("1.3");
        bag.Conflicts.Should().BeEmpty();
    }

    [Fact]
    public void Merge_Drops_Values_Outside_Schema()
    {
        var bag = new FactBag();
        bag.Merge("s1", Facts(("not_in_schema", "hello"), ("encryption_declared", true)), MakeSchema());
        bag.Values.Should().ContainKey("encryption_declared");
        bag.Values.Should().NotContainKey("not_in_schema");
    }

    [Fact]
    public void Merge_Missing_Concept_In_Section_Is_Ignored()
    {
        var bag = new FactBag();
        bag.Merge("s1", Facts(("encryption_declared", true)), MakeSchema());
        bag.Values.Should().ContainKey("encryption_declared");
        bag.Get("key_rotation_days").Should().BeNull();
    }

    [Fact]
    public void Merge_Bool_As_String_Parses()
    {
        var bag = new FactBag();
        bag.Merge("s1", Facts(("encryption_declared", "true")), MakeSchema());
        bag.Get("encryption_declared").Should().NotBeNull();
    }

    [Fact]
    public void Merge_Integer_As_String_Parses()
    {
        var bag = new FactBag();
        bag.Merge("s1", Facts(("key_rotation_days", 90L)), MakeSchema());
        bag.Merge("s2", Facts(("key_rotation_days", "30")), MakeSchema());
        bag.Get("key_rotation_days").Should().Be(30L);
    }

    [Fact]
    public void Conflict_Records_Section_Provenance()
    {
        var bag = new FactBag();
        bag.Merge("sA", Facts(("tls_min_version", "1.2")), MakeSchema());
        bag.Merge("sB", Facts(("tls_min_version", "1.3")), MakeSchema());
        bag.Conflicts.Should().ContainSingle();
        bag.Conflicts[0].IncomingSectionId.Should().Be("sB");
        bag.Conflicts[0].Existing.Should().Be("1.2");
        bag.Conflicts[0].Incoming.Should().Be("1.3");
        bag.Conflicts[0].Resolved.Should().Be("1.2");
    }
}
