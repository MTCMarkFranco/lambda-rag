using LambdaRag.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LambdaRag.UnitTests.Persistence;

/// <summary>
/// Creates an isolated, schema-initialised SQLite database for a single test.
/// Dispose to delete the temp file.
/// </summary>
internal sealed class TestDb : IAsyncDisposable
{
    public string DbPath { get; }
    public IOptions<LambdaRagPersistenceOptions> Options { get; }

    private TestDb(string dbPath)
    {
        DbPath = dbPath;
        Options = Microsoft.Extensions.Options.Options.Create(
            new LambdaRagPersistenceOptions { DatabasePath = dbPath });
    }

    public static async Task<TestDb> CreateAsync()
    {
        var dbPath = Path.ChangeExtension(Path.GetTempFileName(), ".db");
        var db = new TestDb(dbPath);
        await new LambdaRagPersistenceInitializer(db.Options).EnsureSchemaAsync();
        return db;
    }

    public ValueTask DisposeAsync()
    {
        // Release all pooled connections so the file handle is freed before deletion.
        SqliteConnection.ClearAllPools();
        if (File.Exists(DbPath))
            File.Delete(DbPath);
        return ValueTask.CompletedTask;
    }
}
