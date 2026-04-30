using System.Text.Json;
using System.Text.Json.Nodes;
using LambdaRag.Core;
using LambdaRag.Core.Domain;

namespace LambdaRag.Cli;

/// <summary>
/// Loads / saves <see cref="RuleSet"/> from disk using our canonical-JSON
/// converters (selector tagged-union etc).
/// </summary>
public static class RuleSetIO
{
    public static RuleSet Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RuleSet>(json, CanonicalJson.Options)
            ?? throw new InvalidDataException($"RuleSet at {path} did not deserialize.");
    }

    public static void Save(RuleSet ruleset, string path)
    {
        var json = JsonSerializer.Serialize(ruleset, CanonicalJson.Options);
        File.WriteAllText(path, json);
    }

    public static string SerializeReport(ComplianceReport report)
        => JsonSerializer.Serialize(report, CanonicalJson.Options);
}
