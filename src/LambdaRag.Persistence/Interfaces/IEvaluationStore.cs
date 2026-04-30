using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Persistence.Interfaces;

public interface IEvaluationStore
{
    /// <summary>
    /// Persists a compliance report. The storage id is computed as
    /// ContentHash.OfString(compact-json(report)) so identical runs are deduplicated.
    /// </summary>
    Task SaveAsync(ComplianceReport report, CancellationToken ct = default);

    Task<ComplianceReport?> GetAsync(ContentHash id, CancellationToken ct = default);

    Task<IReadOnlyList<ComplianceReport>> GetByDocumentAsync(
        ContentHash documentId, CancellationToken ct = default);
}
