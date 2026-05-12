namespace LambdaRag.Authoring.Editing;

/// <summary>
/// Strongly-typed configuration for <see cref="ComplianceEditor"/>.
/// Mirrors the <c>LambdaRag:Foundry:*</c> shape used by
/// <see cref="LambdaRag.Authoring.Embeddings.FoundryEmbedderFactory"/>,
/// with an <c>Edit</c> sub-section so reviewers can point the rewrite
/// agent at a different deployment (typically a chat / reasoning model)
/// than the embedding deployment.
/// </summary>
public sealed class ComplianceEditorOptions
{
    public required string Endpoint { get; init; }
    public required string Deployment { get; init; }
    public string? ApiKey { get; init; }
    public string CacheDir { get; init; } = Path.Combine("out", "rewrite-cache");
    public int MaxRewriteLength { get; init; } = 1200;
}
