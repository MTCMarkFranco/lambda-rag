using FluentAssertions;
using LambdaRag.Authoring;
using Xunit;

namespace LambdaRag.UnitTests.Authoring;

public class DeterministicHashEmbedderTests
{
    [Fact]
    public async Task SameInput_ProducesSameVector()
    {
        var e = new DeterministicHashEmbedder();
        var a = await e.EmbedAsync("Provider shall maintain ISO 27001 controls.");
        var b = await e.EmbedAsync("Provider shall maintain ISO 27001 controls.");
        a.Should().BeEquivalentTo(b);
    }

    [Fact]
    public async Task DifferentInput_ProducesDifferentVector()
    {
        var e = new DeterministicHashEmbedder();
        var a = await e.EmbedAsync("payment terms net 30");
        var b = await e.EmbedAsync("governing law of Delaware");
        a.Should().NotBeEquivalentTo(b);
    }

    [Fact]
    public async Task Vector_IsL2Normalised()
    {
        var e = new DeterministicHashEmbedder();
        var v = await e.EmbedAsync("anything goes here");
        var sumSq = v.Sum(x => (double)x * x);
        Math.Sqrt(sumSq).Should().BeApproximately(1.0, 1e-5);
    }

    [Fact]
    public async Task Cosine_OfVectorWithItself_IsOne()
    {
        var e = new DeterministicHashEmbedder();
        var v = await e.EmbedAsync("Cosine should equal 1 for self-similarity.");
        DeterministicHashEmbedder.Cosine(v, v).Should().BeApproximately(1.0, 1e-5);
    }

    [Fact]
    public async Task Cosine_OfDifferentVectors_IsBelowOne()
    {
        var e = new DeterministicHashEmbedder();
        var a = await e.EmbedAsync("payment terms");
        var b = await e.EmbedAsync("governing law");
        DeterministicHashEmbedder.Cosine(a, b).Should().BeLessThan(1.0);
    }

    [Fact]
    public void Cosine_ReturnsZero_OnNullOrMismatchedShapes()
    {
        DeterministicHashEmbedder.Cosine(null, new float[] { 1, 2 }).Should().Be(0);
        DeterministicHashEmbedder.Cosine(new float[] { 1 }, new float[] { 1, 2 }).Should().Be(0);
    }

    [Fact]
    public void Dimensions_AndId_AreStable()
    {
        var e = new DeterministicHashEmbedder();
        e.Dimensions.Should().Be(32);
        e.EmbedderId.Should().Be("deterministic-sha256/32");
    }
}
