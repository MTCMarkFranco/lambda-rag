using FluentAssertions;
using LambdaRag.Authoring.Dsl;
using LambdaRag.Authoring.Semantic;
using Xunit;

namespace LambdaRag.UnitTests.Authoring.Semantic;

public class LambdaDslTests
{
    [Fact]
    public void ContainsMeaning_emits_expected_RulesEngine_call()
    {
        var expr = Lambda.ContainsMeaning("works made for hire");
        expr.Should().Be("ContainsMeaning(input1.id, \"works made for hire\", 0.78)");
    }

    [Fact]
    public void ContainsMeaning_with_custom_threshold_uses_invariant_culture()
    {
        var expr = Lambda.ContainsMeaning("indemnification", threshold: 0.825);
        expr.Should().Be("ContainsMeaning(input1.id, \"indemnification\", 0.825)");
    }

    [Fact]
    public void ContainsMeaning_with_quotes_in_concept_is_escaped()
    {
        var expr = Lambda.ContainsMeaning("the \"works\" clause");
        expr.Should().Be("ContainsMeaning(input1.id, \"the \\\"works\\\" clause\", 0.78)");
    }

    [Fact]
    public void Section_chained_with_field_produces_parenthesised_AND()
    {
        var expr = Lambda
            .Section()
            .ContainsMeaning("contract tax bracket")
            .And(Lambda.Field("input1", "tax").LessThan(100))
            .ToExpression();

        expr.Should().Be(
            "(ContainsMeaning(input1.id, \"contract tax bracket\", 0.78)) && (input1.tax < 100)");
    }

    [Fact]
    public void Or_and_Not_combine_with_full_parenthesisation()
    {
        var expr = Lambda
            .Section()
            .ContainsMeaning("work for hire")
            .Or(Lambda.Section().ContainsMeaning("hereby assigns"))
            .Not()
            .ToExpression();

        expr.Should().Be(
            "!((ContainsMeaning(input1.id, \"work for hire\", 0.78)) || (ContainsMeaning(input1.id, \"hereby assigns\", 0.78)))");
    }

    [Fact]
    public void MatchesAny_pipe_delimits_concepts_and_escapes_them()
    {
        var expr = Lambda
            .Section()
            .MatchesAny("work for hire", "hereby assigns", "vests in customer")
            .ToExpression();

        expr.Should().Be(
            "MatchesAnyMeaning(input1.id, \"work for hire|hereby assigns|vests in customer\", 0.78)");
    }

    [Fact]
    public void TextContains_emits_lexical_predicate()
    {
        var expr = Lambda.Section().TextContains("Contoso").ToExpression();
        expr.Should().Be("input1.text.Contains(\"Contoso\")");
    }

    [Fact]
    public void ContainsMeaning_throws_on_empty_concept()
    {
        var act = () => Lambda.Section().ContainsMeaning("");
        act.Should().Throw<ArgumentException>();
    }
}

public class SemanticFunctionsTests
{
    [Fact]
    public void Cosine_of_identical_unit_vectors_is_one()
    {
        var v = new float[] { 1f, 0f, 0f };
        SemanticFunctions.Cosine(v, v).Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void Cosine_of_orthogonal_vectors_is_zero()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { 0f, 1f };
        SemanticFunctions.Cosine(a, b).Should().Be(0.0);
    }

    [Fact]
    public void Cosine_handles_mismatched_lengths_as_zero()
    {
        var a = new float[] { 1f, 0f };
        var b = new float[] { 1f, 0f, 0f };
        SemanticFunctions.Cosine(a, b).Should().Be(0.0);
    }

    [Fact]
    public void Cosine_is_idempotent_byte_for_byte_across_runs()
    {
        var a = new float[] { 0.31f, -0.42f, 0.15f, 0.88f };
        var b = new float[] { -0.11f, 0.27f, 0.95f, -0.04f };
        var first = SemanticFunctions.Cosine(a, b);
        var second = SemanticFunctions.Cosine(a, b);
        var third = SemanticFunctions.Cosine(a, b);
        first.Should().Be(second);
        second.Should().Be(third);
    }

    [Fact]
    public void ContainsMeaning_returns_true_when_cosine_meets_threshold()
    {
        // Tie-break is >=, so a cosine that exactly equals the threshold passes.
        var sectionVec = new float[] { 1f, 0f };
        var conceptVec = new float[] { 1f, 0f }; // cosine = 1
        var store = new InMemorySemanticVectorStore("test/2", 2);
        store.AddSection("sec-1", sectionVec);
        store.AddConcept("anything", conceptVec);

        using (VectorStoreAccessor.Push(store))
        {
            SemanticFunctions.ContainsMeaning("sec-1", "anything", 1.0).Should().BeTrue();
            SemanticFunctions.ContainsMeaning("sec-1", "anything", 0.99).Should().BeTrue();
        }
    }

    [Fact]
    public void ContainsMeaning_returns_false_when_cosine_below_threshold()
    {
        var store = new InMemorySemanticVectorStore("test/2", 2);
        store.AddSection("sec-1", new float[] { 1f, 0f });
        store.AddConcept("orthogonal", new float[] { 0f, 1f }); // cosine = 0

        using (VectorStoreAccessor.Push(store))
        {
            SemanticFunctions.ContainsMeaning("sec-1", "orthogonal", 0.5).Should().BeFalse();
        }
    }

    [Fact]
    public void ContainsMeaning_throws_when_section_vector_missing()
    {
        var store = new InMemorySemanticVectorStore("test/2", 2);
        store.AddConcept("c", new float[] { 1f, 0f });

        using (VectorStoreAccessor.Push(store))
        {
            var act = () => SemanticFunctions.ContainsMeaning("missing", "c", 0.5);
            act.Should().Throw<InvalidOperationException>().WithMessage("*missing*");
        }
    }

    [Fact]
    public void ContainsMeaning_throws_when_concept_vector_missing()
    {
        var store = new InMemorySemanticVectorStore("test/2", 2);
        store.AddSection("sec-1", new float[] { 1f, 0f });

        using (VectorStoreAccessor.Push(store))
        {
            var act = () => SemanticFunctions.ContainsMeaning("sec-1", "unknown", 0.5);
            act.Should().Throw<InvalidOperationException>().WithMessage("*unknown*");
        }
    }

    [Fact]
    public void SemanticFunctions_throw_when_no_store_pushed()
    {
        // No VectorStoreAccessor.Push — must fail loud.
        var act = () => SemanticFunctions.ContainsMeaning("a", "b", 0.5);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*outside an evaluation scope*");
    }

    [Fact]
    public void NotConfiguredSemanticVectorStore_throws_on_section_lookup()
    {
        var store = new NotConfiguredSemanticVectorStore();
        var act = () => store.TryGetSection("x", out _);
        act.Should().Throw<InvalidOperationException>().WithMessage("*not configured*");
    }

    [Fact]
    public void MatchesAnyMeaning_returns_true_on_first_concept_above_threshold()
    {
        var store = new InMemorySemanticVectorStore("test/2", 2);
        store.AddSection("sec-1", new float[] { 1f, 0f });
        store.AddConcept("orthogonal", new float[] { 0f, 1f });   // cosine = 0
        store.AddConcept("aligned",    new float[] { 1f, 0f });   // cosine = 1

        using (VectorStoreAccessor.Push(store))
        {
            SemanticFunctions.MatchesAnyMeaning("sec-1", "orthogonal|aligned", 0.5).Should().BeTrue();
        }
    }

    [Fact]
    public void MatchesAnyMeaning_returns_false_when_no_concept_above_threshold()
    {
        var store = new InMemorySemanticVectorStore("test/2", 2);
        store.AddSection("sec-1", new float[] { 1f, 0f });
        store.AddConcept("a", new float[] { 0f, 1f }); // 0
        store.AddConcept("b", new float[] { 0f, 1f }); // 0

        using (VectorStoreAccessor.Push(store))
        {
            SemanticFunctions.MatchesAnyMeaning("sec-1", "a|b", 0.5).Should().BeFalse();
        }
    }

    [Fact]
    public async Task VectorStoreAccessor_isolates_stores_per_async_flow()
    {
        var s1 = new InMemorySemanticVectorStore("s1", 2);
        var s2 = new InMemorySemanticVectorStore("s2", 2);

        var t1 = Task.Run(() =>
        {
            using (VectorStoreAccessor.Push(s1))
            {
                Thread.Sleep(20);
                return VectorStoreAccessor.Current!.ModelId;
            }
        });
        var t2 = Task.Run(() =>
        {
            using (VectorStoreAccessor.Push(s2))
            {
                Thread.Sleep(20);
                return VectorStoreAccessor.Current!.ModelId;
            }
        });

        var results = await Task.WhenAll(t1, t2);
        results.Should().BeEquivalentTo(new[] { "s1", "s2" });
        VectorStoreAccessor.Current.Should().BeNull();
    }
}
