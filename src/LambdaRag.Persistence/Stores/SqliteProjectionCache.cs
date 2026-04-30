using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Persistence.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LambdaRag.Persistence.Stores;

public sealed class SqliteProjectionCache : IProjectionCache
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteProjectionCache> _logger;

    public SqliteProjectionCache(
        IOptions<LambdaRagPersistenceOptions> options,
        ILogger<SqliteProjectionCache> logger)
    {
        _connectionString = $"Data Source={options.Value.DatabasePath}";
        _logger = logger;
    }

    public async Task<ProjectedDocument?> GetAsync(ContentHash cacheKey, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<ProjectionRow>(
            """
            SELECT
                source_id     AS SourceId,
                projector_id  AS ProjectorId,
                projector_ver AS ProjectorVer,
                graph_json    AS GraphJson,
                span_map_json AS SpanMapJson
            FROM projections
            WHERE cache_key = @CacheKey
            """,
            new { CacheKey = cacheKey.Value });

        if (row is null) return null;

        var graph = (JsonNode.Parse(row.GraphJson) as JsonObject)!;
        var spanMap = JsonSerializer.Deserialize<Dictionary<string, SourceSpan>>(
            row.SpanMapJson, CanonicalJson.Options)!;

        return new ProjectedDocument(
            new ContentHash(row.SourceId),
            row.ProjectorId,
            row.ProjectorVer,
            graph,
            spanMap);
    }

    public async Task PutAsync(
        ContentHash cacheKey,
        ProjectedDocument doc,
        string modelId,
        ContentHash promptHash,
        CancellationToken ct = default)
    {
        var graphJson = doc.Graph.ToJsonString(CanonicalJson.Compact);
        var spanMapJson = JsonSerializer.Serialize(doc.SpanMap, CanonicalJson.Compact);

        using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT OR REPLACE INTO projections
                (cache_key, source_id, projector_id, projector_ver, model_id, prompt_hash,
                 graph_json, span_map_json, created_at)
            VALUES
                (@CacheKey, @SourceId, @ProjectorId, @ProjectorVer, @ModelId, @PromptHash,
                 @GraphJson, @SpanMapJson, @CreatedAt)
            """,
            new
            {
                CacheKey = cacheKey.Value,
                SourceId = doc.SourceId.Value,
                doc.ProjectorId,
                ProjectorVer = doc.ProjectorVersion,
                ModelId = modelId,
                PromptHash = promptHash.Value,
                GraphJson = graphJson,
                SpanMapJson = spanMapJson,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
        _logger.LogDebug("Cached projection {CacheKey}", cacheKey.Value);
    }

    private sealed class ProjectionRow
    {
        public string SourceId { get; set; } = "";
        public string ProjectorId { get; set; } = "";
        public string ProjectorVer { get; set; } = "";
        public string GraphJson { get; set; } = "";
        public string SpanMapJson { get; set; } = "";
    }
}
