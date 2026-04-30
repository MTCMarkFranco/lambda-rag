using System.Text.Json;
using Dapper;
using LambdaRag.Core.Domain;
using LambdaRag.Indexing.Abstractions;
using LambdaRag.Indexing.Signatures;
using LambdaRag.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LambdaRag.Indexing.Persistence;

/// <summary>
/// Persists extracted rule signatures and semantic chunks to SQLite so
/// the index can be reloaded without re-extracting on every startup.
/// </summary>
public sealed class SqliteIndexStore
{
    private readonly string _connectionString;

    public SqliteIndexStore(IOptions<LambdaRagPersistenceOptions> options)
    {
        _connectionString = $"Data Source={options.Value.DatabasePath}";
    }

    public async Task PublishSignaturesAsync(
        RuleSet ruleSet,
        IRuleSignatureIndex index,
        CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "DELETE FROM rule_signatures WHERE rule_set_id = @SetId AND rule_set_version = @SetVer",
            new { SetId = ruleSet.Id, SetVer = ruleSet.Version }, transaction: tx);

        foreach (var rule in ruleSet.Rules)
        {
            var sig = index.GetSignature(rule.Id) ?? RuleSignature.UniversalFor(rule.Id);
            await conn.ExecuteAsync(
                """
                INSERT INTO rule_signatures
                    (rule_set_id, rule_set_version, rule_id, universal,
                     equality_json, contains_json, field_paths_json)
                VALUES
                    (@SetId, @SetVer, @RuleId, @Universal,
                     @EqJson, @CtJson, @FpJson)
                """,
                new
                {
                    SetId = ruleSet.Id,
                    SetVer = ruleSet.Version,
                    RuleId = rule.Id,
                    Universal = sig.Universal ? 1 : 0,
                    EqJson = JsonSerializer.Serialize(sig.Equalities),
                    CtJson = JsonSerializer.Serialize(sig.Containments),
                    FpJson = JsonSerializer.Serialize(sig.FieldPaths),
                }, transaction: tx);
        }
        await tx.CommitAsync(ct);
    }

    public async Task PublishSemanticChunksAsync(
        RuleSet ruleSet,
        string embedderId,
        CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var tx = await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(
            "DELETE FROM rule_semantic_chunks WHERE rule_set_id = @SetId AND rule_set_version = @SetVer",
            new { SetId = ruleSet.Id, SetVer = ruleSet.Version }, transaction: tx);

        foreach (var rule in ruleSet.Rules)
        {
            if (rule.SourceContent is null) continue;
            await conn.ExecuteAsync(
                """
                INSERT INTO rule_semantic_chunks
                    (rule_set_id, rule_set_version, rule_id, embedder_id,
                     source_content, embedding_json)
                VALUES
                    (@SetId, @SetVer, @RuleId, @EmbId, @Content, @Embedding)
                """,
                new
                {
                    SetId = ruleSet.Id,
                    SetVer = ruleSet.Version,
                    RuleId = rule.Id,
                    EmbId = embedderId,
                    Content = rule.SourceContent,
                    Embedding = rule.SourceEmbedding is null ? "[]" : JsonSerializer.Serialize(rule.SourceEmbedding),
                }, transaction: tx);
        }
        await tx.CommitAsync(ct);
    }
}