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
        """;
}
