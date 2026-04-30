using System.Text.Json;
using LambdaRag.Core;
using LambdaRag.Core.Domain;

namespace LambdaRag.Cli;

/// <summary>Disk format for <see cref="RuleOverlay"/>.</summary>
public static class OverlayIO
{
    public static RuleOverlay Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<RuleOverlay>(json, CanonicalJson.Options)
            ?? throw new InvalidDataException($"Overlay at {path} did not deserialize.");
    }

    public static RuleOverlay LoadOrEmpty(string path, RuleSet ruleset, TimeProvider time, string? createdBy)
    {
        if (File.Exists(path)) return Load(path);
        return new RuleOverlay(
            RuleSetId: ruleset.Id,
            RuleSetVersion: ruleset.Version,
            CreatedAt: time.GetUtcNow(),
            Disabled: [],
            Annotations: [])
        { CreatedBy = createdBy };
    }

    public static void Save(RuleOverlay overlay, string path)
    {
        var json = JsonSerializer.Serialize(overlay, CanonicalJson.Options);
        File.WriteAllText(path, json);
    }
}
