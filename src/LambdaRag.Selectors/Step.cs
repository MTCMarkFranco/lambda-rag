namespace LambdaRag.Selectors;

internal abstract class Step { }

internal sealed class RootStep : Step
{
    public static readonly RootStep Instance = new();
    private RootStep() { }
}

internal sealed class FieldStep(string name) : Step
{
    public string Name { get; } = name;
}

internal sealed class IndexStep(int index) : Step
{
    public int Index { get; } = index;
}

internal sealed class AllStep : Step
{
    public static readonly AllStep Instance = new();
    private AllStep() { }
}

internal sealed class FilterStep(FilterPredicate predicate) : Step
{
    public FilterPredicate Predicate { get; } = predicate;
}
