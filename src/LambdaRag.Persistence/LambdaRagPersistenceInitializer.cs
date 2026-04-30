using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LambdaRag.Persistence;

/// <summary>
/// Runs idempotent CREATE TABLE IF NOT EXISTS migrations against the configured SQLite database.
/// Call <see cref="EnsureSchemaAsync"/> once at startup (or explicitly in tests) before using any store.
/// </summary>
public sealed class LambdaRagPersistenceInitializer
{
    private readonly LambdaRagPersistenceOptions _options;

    public LambdaRagPersistenceInitializer(IOptions<LambdaRagPersistenceOptions> options)
    {
        _options = options.Value;
    }

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection($"Data Source={_options.DatabasePath}");
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(SchemaSQL);
    }

    private const string SchemaSQL = """
        CREATE TABLE IF NOT EXISTS source_documents (
            id          TEXT PRIMARY KEY,
            file_name   TEXT NOT NULL,
            kind        TEXT NOT NULL,
            byte_length INTEGER NOT NULL,
            ingested_at TEXT NOT NULL,
            bytes_path  TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS rule_sets (
            id            TEXT NOT NULL,
            version       TEXT NOT NULL,
            domain        TEXT NOT NULL,
            fingerprint   TEXT NOT NULL,
            published_at  TEXT NOT NULL,
            metadata_json TEXT NOT NULL,
            rules_json    TEXT NOT NULL,
            PRIMARY KEY (id, version)
        );

        -- Per-rule hash columns: each component (predicate, lambda, remediation)
        -- is hashed separately so a change in any one alone forces a new rule
        -- version downstream. The whole rule still lives in rule_sets.rules_json;
        -- this table provides a queryable, indexed projection for audits.
        CREATE TABLE IF NOT EXISTS rules (
            rule_set_id      TEXT NOT NULL,
            rule_set_version TEXT NOT NULL,
            rule_id          TEXT NOT NULL,
            rule_version     TEXT NOT NULL,
            severity         TEXT NOT NULL,
            predicate_hash   TEXT NOT NULL,
            lambda_hash      TEXT NOT NULL,
            remediation_hash TEXT NOT NULL,
            fingerprint      TEXT NOT NULL,
            PRIMARY KEY (rule_set_id, rule_set_version, rule_id)
        );
        CREATE INDEX IF NOT EXISTS ix_rules_pred ON rules(predicate_hash);
        CREATE INDEX IF NOT EXISTS ix_rules_lam  ON rules(lambda_hash);

        CREATE TABLE IF NOT EXISTS projections (
            cache_key     TEXT PRIMARY KEY,
            source_id     TEXT NOT NULL,
            projector_id  TEXT NOT NULL,
            projector_ver TEXT NOT NULL,
            model_id      TEXT NOT NULL,
            prompt_hash   TEXT NOT NULL,
            graph_json    TEXT NOT NULL,
            span_map_json TEXT NOT NULL,
            created_at    TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS evaluations (
            id           TEXT PRIMARY KEY,
            document_id  TEXT NOT NULL,
            rule_set_id  TEXT NOT NULL,
            rule_set_ver TEXT NOT NULL,
            rule_set_fp  TEXT NOT NULL,
            score        REAL NOT NULL,
            generated_at TEXT NOT NULL,
            report_json  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_eval_doc ON evaluations(document_id);

        -- Predicate signatures extracted by the indexing layer. Used to
        -- audit which structural shape the runtime uses to short-list
        -- candidate rules. The runtime hot-path rebuilds the signature
        -- index in memory at startup; this table is a queryable projection.
        CREATE TABLE IF NOT EXISTS rule_signatures (
            rule_set_id       TEXT NOT NULL,
            rule_set_version  TEXT NOT NULL,
            rule_id           TEXT NOT NULL,
            universal         INTEGER NOT NULL,
            equality_json     TEXT NOT NULL,
            contains_json     TEXT NOT NULL,
            field_paths_json  TEXT NOT NULL,
            PRIMARY KEY (rule_set_id, rule_set_version, rule_id)
        );
        CREATE INDEX IF NOT EXISTS ix_rule_sig_universal ON rule_signatures(universal);

        -- Authoring-time semantic chunks. The embedding column is JSON
        -- because SQLite has no native vector type; for production-scale
        -- semantic search swap to Azure AI Search via
        -- AzureSearchRuleSemanticIndex.
        CREATE TABLE IF NOT EXISTS rule_semantic_chunks (
            rule_set_id       TEXT NOT NULL,
            rule_set_version  TEXT NOT NULL,
            rule_id           TEXT NOT NULL,
            embedder_id       TEXT NOT NULL,
            source_content    TEXT NOT NULL,
            embedding_json    TEXT NOT NULL,
            PRIMARY KEY (rule_set_id, rule_set_version, rule_id, embedder_id)
        );
        """;
}
