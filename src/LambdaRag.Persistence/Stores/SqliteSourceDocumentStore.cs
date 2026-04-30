using Dapper;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Persistence.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LambdaRag.Persistence.Stores;

public sealed class SqliteSourceDocumentStore : ISourceDocumentStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSourceDocumentStore> _logger;

    public SqliteSourceDocumentStore(
        IOptions<LambdaRagPersistenceOptions> options,
        ILogger<SqliteSourceDocumentStore> logger)
    {
        _connectionString = $"Data Source={options.Value.DatabasePath}";
        _logger = logger;
    }

    public async Task UpsertAsync(SourceDocument doc, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT INTO source_documents (id, file_name, kind, byte_length, ingested_at, bytes_path)
            VALUES (@Id, @FileName, @Kind, @ByteLength, @IngestedAt, NULL)
            ON CONFLICT(id) DO UPDATE SET
                file_name   = excluded.file_name,
                kind        = excluded.kind,
                byte_length = excluded.byte_length,
                ingested_at = excluded.ingested_at
            """,
            new
            {
                Id = doc.Id.Value,
                doc.FileName,
                Kind = doc.Kind.ToString(),
                doc.ByteLength,
                IngestedAt = doc.IngestedAt.ToString("O"),
            });
        _logger.LogDebug("Upserted source document {Id}", doc.Id.Value);
    }

    public async Task<SourceDocument?> GetAsync(ContentHash id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<SourceDocumentRow>(
            """
            SELECT
                id          AS Id,
                file_name   AS FileName,
                kind        AS Kind,
                byte_length AS ByteLength,
                ingested_at AS IngestedAt
            FROM source_documents
            WHERE id = @Id
            """,
            new { Id = id.Value });

        if (row is null) return null;

        return new SourceDocument(
            new ContentHash(row.Id),
            row.FileName,
            Enum.Parse<SourceDocumentKind>(row.Kind),
            row.ByteLength,
            DateTimeOffset.Parse(row.IngestedAt));
    }

    private sealed class SourceDocumentRow
    {
        public string Id { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Kind { get; set; } = "";
        public long ByteLength { get; set; }
        public string IngestedAt { get; set; } = "";
    }
}
