namespace LambdaRag.Selectors;

internal sealed class JsonPathExpression(IReadOnlyList<Step> steps)
{
    public IReadOnlyList<Step> Steps { get; } = steps;
}
