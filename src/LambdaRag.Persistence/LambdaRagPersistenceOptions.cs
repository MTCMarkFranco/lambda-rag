namespace LambdaRag.Persistence;

public sealed class LambdaRagPersistenceOptions
{
    public string DatabasePath { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "lambdarag.db");
}
