using System.Text.Json;
using Dapper;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Persistence.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LambdaRag.Persistence.Stores;

public sealed class SqliteEvaluationStore : IEvaluationStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteEvaluationStore> _logger;

    public SqliteEvaluationStore(
        IOptions<LambdaRagPersistenceOptions> options,
        ILogger<SqliteEvaluationStore> logger)
    {
        _connectionString = $"Data Source={options.Value.DatabasePath}";
        _logger = logger;
    }

    public async Task SaveAsync(ComplianceReport report, CancellationToken ct = default)
    {
        var reportJson = JsonSerializer.Serialize(report, CanonicalJson.Compact);
        var id = ContentHash.OfString(reportJson).Value;

        using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(
            """
            INSERT OR IGNORE INTO evaluations
                (id, document_id, rule_set_id, rule_set_ver, rule_set_fp, score, generated_at, report_json)
            VALUES
                (@Id, @DocumentId, @RuleSetId, @RuleSetVer, @RuleSetFp, @Score, @GeneratedAt, @ReportJson)
            """,
            new
            {
                Id = id,
                DocumentId = report.DocumentId.Value,
                RuleSetId = report.RuleSetId,
                RuleSetVer = report.RuleSetVersion,
                RuleSetFp = report.RuleSetFingerprint.Value,
                report.Score,
                GeneratedAt = report.GeneratedAt.ToString("O"),
                ReportJson = reportJson,
            });
        _logger.LogDebug("Saved evaluation {Id}", id);
    }

    public async Task<ComplianceReport?> GetAsync(ContentHash id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        var row = await conn.QuerySingleOrDefaultAsync<EvaluationRow>(
            "SELECT report_json AS ReportJson FROM evaluations WHERE id = @Id",
            new { Id = id.Value });

        return row is null ? null : Deserialize(row.ReportJson);
    }

    public async Task<IReadOnlyList<ComplianceReport>> GetByDocumentAsync(
        ContentHash documentId, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        var rows = await conn.QueryAsync<EvaluationRow>(
            """
            SELECT report_json AS ReportJson
            FROM evaluations
            WHERE document_id = @DocumentId
            ORDER BY generated_at DESC
            """,
            new { DocumentId = documentId.Value });

        return rows.Select(r => Deserialize(r.ReportJson)).ToList();
    }

    private static ComplianceReport Deserialize(string json) =>
        JsonSerializer.Deserialize<ComplianceReport>(json, CanonicalJson.Options)!;

    private sealed class EvaluationRow
    {
        public string ReportJson { get; set; } = "";
    }
}
