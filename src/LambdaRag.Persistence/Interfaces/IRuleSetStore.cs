using LambdaRag.Core.Domain;

namespace LambdaRag.Persistence.Interfaces;

public interface IRuleSetStore
{
    /// <summary>Idempotent on (id, version): publishing the same version twice is a no-op.</summary>
    Task PublishAsync(RuleSet ruleSet, CancellationToken ct = default);

    Task<RuleSet?> GetAsync(string id, string version, CancellationToken ct = default);
    Task<RuleSet?> GetLatestAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<(string Id, string Version, string Domain, DateTimeOffset PublishedAt)>>
        ListAsync(CancellationToken ct = default);
}
