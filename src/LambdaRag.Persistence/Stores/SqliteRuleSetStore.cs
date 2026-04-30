using System.Text.Json;
using Dapper;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Persistence.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LambdaRag.Persistence.Stores;

public sealed class SqliteRuleSetStore : IRuleSetStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteRuleSetStore> _logger;

    public SqliteRuleSetStore(
        IOptions<LambdaRagPersistenceOptions> options,
        ILogger<SqliteRuleSetStore> logger)
    {
        _connectionString = $"Data Source={options.Value.DatabasePath}";
        _logger = logger;
    }

    public async Task PublishAsync(RuleSet ruleSet, CancellationToken ct = default)
    {
        var rulesJson = JsonSerializer.Serialize(ruleSet, CanonicalJson.Compact);
        var metadataJson = JsonSerializer.Serialize(ruleSet.Metadata, CanonicalJson.Compact);
        var fingerprint = ruleSet.Fingerprint().Value;

        using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT OR IGNORE INTO rule_sets
                (id, version, domain, fingerprint, published_at, metadata_json, rules_json)
            VALUES
                (@Id, @Version, @Domain, @Fingerprint, @PublishedAt, @MetadataJson, @RulesJson)
            """,
            new
            {
                ruleSet.Id,
                ruleSet.Version,
                ruleSet.Domain,
                Fingerprint = fingerprint,
                PublishedAt = ruleSet.PublishedAt.ToString("O"),
                MetadataJson = metadataJson,
                RulesJson = rulesJson,
            });
        _logger.LogDebug("Published rule set {Id} v{Version}", ruleSet.Id, ruleSet.Version);
    }

    public async Task<RuleSet?> GetAsync(string id, string version, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<RuleSetRow>(
            """
            SELECT rules_json AS RulesJson
            FROM rule_sets
            WHERE id = @Id AND version = @Version
            """,
            new { Id = id, Version = version });

        return row is null ? null : Deserialize(row.RulesJson);
    }

    public async Task<RuleSet?> GetLatestAsync(string id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<RuleSetRow>(
            """
            SELECT rules_json AS RulesJson
            FROM rule_sets
            WHERE id = @Id
            ORDER BY published_at DESC
            LIMIT 1
            """,
            new { Id = id });

        return row is null ? null : Deserialize(row.RulesJson);
    }

    public async Task<IReadOnlyList<(string Id, string Version, string Domain, DateTimeOffset PublishedAt)>>
        ListAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        var rows = await conn.QueryAsync<RuleSetListRow>(
            """
            SELECT
                id           AS Id,
                version      AS Version,
                domain       AS Domain,
                published_at AS PublishedAt
            FROM rule_sets
            ORDER BY published_at DESC
            """);

        return rows
            .Select(r => (r.Id, r.Version, r.Domain, DateTimeOffset.Parse(r.PublishedAt)))
            .ToList();
    }

    private static RuleSet Deserialize(string json) =>
        JsonSerializer.Deserialize<RuleSet>(json, CanonicalJson.Options)!;

    private sealed class RuleSetRow
    {
        public string RulesJson { get; set; } = "";
    }

    private sealed class RuleSetListRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Domain { get; set; } = "";
        public string PublishedAt { get; set; } = "";
    }
}
