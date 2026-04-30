using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Persistence.Interfaces;

public interface IProjectionCache
{
    Task<ProjectedDocument?> GetAsync(ContentHash cacheKey, CancellationToken ct = default);

    Task PutAsync(
        ContentHash cacheKey,
        ProjectedDocument doc,
        string modelId,
        ContentHash promptHash,
        CancellationToken ct = default);
}
