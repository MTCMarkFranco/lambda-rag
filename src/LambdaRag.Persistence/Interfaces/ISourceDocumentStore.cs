using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Persistence.Interfaces;

public interface ISourceDocumentStore
{
    Task UpsertAsync(SourceDocument doc, CancellationToken ct = default);
    Task<SourceDocument?> GetAsync(ContentHash id, CancellationToken ct = default);
}
