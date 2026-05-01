using System.Text.Json;
using LambdaRag.Core;
using LambdaRag.Core.Domain;

namespace LambdaRag.Cli;

/// <summary>
/// `lambda-rag rules ...` — governance tooling that does NOT edit the
/// extracted ruleset. Edits to rules go through the policy → extract
/// pipeline; everything here is overlay-based or read-only.
/// </summary>
public static class RulesCommand
{
    public static Task<int> RunAsync(string[] args, TimeProvider time)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return Task.FromResult(64);
        }

        return args[0] switch
        {
            "diff"     => Task.FromResult(Diff(args.Skip(1).ToArray())),
            "show"     => Task.FromResult(Show(args.Skip(1).ToArray())),
            "disable"  => Task.FromResult(Disable(args.Skip(1).ToArray(), time)),
            "enable"   => Task.FromResult(Enable(args.Skip(1).ToArray(), time)),
            "annotate" => Task.FromResult(Annotate(args.Skip(1).ToArray(), time)),
            "synopsize" => SynopsizeCommand.RunAsync(args.Skip(1).ToArray()),
            _ => Unknown(args[0]),
        };
    }

    static void PrintHelp() => Console.WriteLine("""
        Usage:
          lambda-rag rules diff      <old.json> <new.json> [--out diff.json]
          lambda-rag rules show      --ruleset <path> --rule <id>
          lambda-rag rules disable   --ruleset <path> --overlay <path> --rule <id> --reason "..." [--by <name>]
          lambda-rag rules enable    --ruleset <path> --overlay <path> --rule <id>
          lambda-rag rules annotate  --ruleset <path> --overlay <path> --rule <id> --note "..." [--by <name>]
          lambda-rag rules synopsize --ruleset <path> [--out <path>] [--cache-dir <path>] [--force]
        """);

    static Task<int> Unknown(string sub)
    {
        Console.Error.WriteLine($"unknown rules subcommand: {sub}");
        PrintHelp();
        return Task.FromResult(64);
    }

    // ────────────────────────────────────────────────────────────────────
    // diff
    // ────────────────────────────────────────────────────────────────────

    public sealed record RulesetDiff(
        string FromId, string FromVersion,
        string ToId, string ToVersion,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Removed,
        IReadOnlyList<RuleChange> Changed,
        IReadOnlyList<string> Unchanged);

    public sealed record RuleChange(
        string RuleId,
        string FromFingerprint,
        string ToFingerprint,
        IReadOnlyList<string> ChangedFields);

    static int Diff(string[] args)
    {
        if (args.Length < 2 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("usage: lambda-rag rules diff <old.json> <new.json> [--out diff.json]");
            return 64;
        }
        var oldPath = args[0];
        var newPath = args[1];
        var flags = ParseFlags(args.Skip(2).ToArray());
        var outPath = flags.GetValueOrDefault("out");

        var oldRs = RuleSetIO.Load(oldPath);
        var newRs = RuleSetIO.Load(newPath);
        var diff = ComputeDiff(oldRs, newRs);

        Console.WriteLine($"From:    {diff.FromId}@{diff.FromVersion}  ({oldRs.Rules.Count} rules)");
        Console.WriteLine($"To:      {diff.ToId}@{diff.ToVersion}  ({newRs.Rules.Count} rules)");
        Console.WriteLine($"Added:     {diff.Added.Count}");
        foreach (var id in diff.Added) Console.WriteLine($"  + {id}");
        Console.WriteLine($"Removed:   {diff.Removed.Count}");
        foreach (var id in diff.Removed) Console.WriteLine($"  - {id}");
        Console.WriteLine($"Changed:   {diff.Changed.Count}");
        foreach (var c in diff.Changed)
            Console.WriteLine($"  ~ {c.RuleId}  ({string.Join(", ", c.ChangedFields)})");
        Console.WriteLine($"Unchanged: {diff.Unchanged.Count}");

        if (outPath is not null)
        {
            File.WriteAllText(outPath, JsonSerializer.Serialize(diff, CanonicalJson.Options));
            Console.WriteLine($"Wrote:     {outPath}");
        }

        // Exit code: 0 if no changes, 2 if there are governance-affecting deltas.
        var hasDelta = diff.Added.Count > 0 || diff.Removed.Count > 0 || diff.Changed.Count > 0;
        return hasDelta ? 2 : 0;
    }

    public static RulesetDiff ComputeDiff(RuleSet oldRs, RuleSet newRs)
    {
        var oldById = oldRs.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var newById = newRs.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var allIds = oldById.Keys.Union(newById.Keys, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();

        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<RuleChange>();
        var unchanged = new List<string>();

        foreach (var id in allIds)
        {
            var inOld = oldById.TryGetValue(id, out var o);
            var inNew = newById.TryGetValue(id, out var n);
            if (inOld && !inNew) { removed.Add(id); continue; }
            if (!inOld && inNew) { added.Add(id); continue; }
            var oldFp = o!.Fingerprint().Value;
            var newFp = n!.Fingerprint().Value;
            if (string.Equals(oldFp, newFp, StringComparison.Ordinal)) { unchanged.Add(id); continue; }
            changed.Add(new RuleChange(id, oldFp, newFp, FieldDiff(o, n)));
        }

        return new RulesetDiff(
            FromId: oldRs.Id, FromVersion: oldRs.Version,
            ToId: newRs.Id, ToVersion: newRs.Version,
            Added: added, Removed: removed, Changed: changed, Unchanged: unchanged);
    }

    static IReadOnlyList<string> FieldDiff(Rule a, Rule b)
    {
        var fields = new List<string>();
        if (!string.Equals(a.Predicate, b.Predicate, StringComparison.Ordinal)) fields.Add("predicate");
        if (!string.Equals(a.Lambda, b.Lambda, StringComparison.Ordinal)) fields.Add("lambda");
        if (!string.Equals(a.Remediation ?? "", b.Remediation ?? "", StringComparison.Ordinal)) fields.Add("remediation");
        if (a.Severity != b.Severity) fields.Add("severity");
        if (a.Applicability != b.Applicability) fields.Add("applicability");
        if (!string.Equals(a.AppliesToSchema.ToJsonString(), b.AppliesToSchema.ToJsonString(), StringComparison.Ordinal)) fields.Add("schema");
        if (!string.Equals(a.NaturalLanguage, b.NaturalLanguage, StringComparison.Ordinal)) fields.Add("naturalLanguage");
        if (!string.Equals(a.Version, b.Version, StringComparison.Ordinal)) fields.Add("version");
        return fields;
    }

    // ────────────────────────────────────────────────────────────────────
    // show
    // ────────────────────────────────────────────────────────────────────

    static int Show(string[] args)
    {
        var f = ParseFlags(args);
        var rulesetPath = f.GetValueOrDefault("ruleset") ?? throw new ArgumentException("--ruleset required");
        var ruleId = f.GetValueOrDefault("rule") ?? throw new ArgumentException("--rule required");

        var ruleset = RuleSetIO.Load(rulesetPath);
        var rule = ruleset.Rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null)
        {
            Console.Error.WriteLine($"Rule '{ruleId}' not found in {ruleset.Id}@{ruleset.Version}.");
            return 1;
        }

        Console.WriteLine($"Rule:           {rule.Id}@{rule.Version}");
        Console.WriteLine($"Severity:       {rule.Severity}");
        Console.WriteLine($"Applicability:  {rule.Applicability}");
        Console.WriteLine($"Fingerprint:    {rule.Fingerprint().Value}");
        Console.WriteLine();
        Console.WriteLine($"Statement:      {rule.NaturalLanguage}");
        Console.WriteLine($"Predicate:      {rule.Predicate}");
        Console.WriteLine($"Lambda:         {rule.Lambda}");
        if (!string.IsNullOrEmpty(rule.Remediation))
            Console.WriteLine($"Remediation:    {rule.Remediation}");
        Console.WriteLine($"Source:         {rule.SourceSpan.DocumentId}  [{rule.SourceSpan.CharStart}..{rule.SourceSpan.CharStart + rule.SourceSpan.CharLength}]");
        if (!string.IsNullOrEmpty(rule.EvidenceQuote))
            Console.WriteLine($"Evidence:       \"{rule.EvidenceQuote}\"");
        return 0;
    }

    // ────────────────────────────────────────────────────────────────────
    // disable / enable / annotate
    // ────────────────────────────────────────────────────────────────────

    static int Disable(string[] args, TimeProvider time)
    {
        var f = ParseFlags(args);
        var rulesetPath = f.GetValueOrDefault("ruleset") ?? throw new ArgumentException("--ruleset required");
        var overlayPath = f.GetValueOrDefault("overlay") ?? throw new ArgumentException("--overlay required");
        var ruleId = f.GetValueOrDefault("rule") ?? throw new ArgumentException("--rule required");
        var reason = f.GetValueOrDefault("reason") ?? throw new ArgumentException("--reason required (governance: disabling a rule must be documented)");
        var by = f.GetValueOrDefault("by");

        var ruleset = RuleSetIO.Load(rulesetPath);
        if (ruleset.Rules.All(r => r.Id != ruleId))
            throw new InvalidOperationException($"Rule '{ruleId}' does not exist in {ruleset.Id}@{ruleset.Version}.");

        var overlay = OverlayIO.LoadOrEmpty(overlayPath, ruleset, time, by);
        EnsureOverlayBindsTo(overlay, ruleset);

        var disabled = overlay.Disabled.Where(d => d.RuleId != ruleId).ToList();
        disabled.Add(new DisabledRule(ruleId, reason, time.GetUtcNow()) { DisabledBy = by });
        var updated = overlay with { Disabled = disabled };

        OverlayIO.Save(updated, overlayPath);
        Console.WriteLine($"Disabled {ruleId} in {overlayPath}");
        Console.WriteLine($"  reason: {reason}");
        Console.WriteLine($"  by:     {by ?? "(unspecified)"}");
        Console.WriteLine($"Overlay fingerprint: {updated.Fingerprint().Value}");
        return 0;
    }

    static int Enable(string[] args, TimeProvider time)
    {
        var f = ParseFlags(args);
        var rulesetPath = f.GetValueOrDefault("ruleset") ?? throw new ArgumentException("--ruleset required");
        var overlayPath = f.GetValueOrDefault("overlay") ?? throw new ArgumentException("--overlay required");
        var ruleId = f.GetValueOrDefault("rule") ?? throw new ArgumentException("--rule required");

        var ruleset = RuleSetIO.Load(rulesetPath);
        if (!File.Exists(overlayPath))
        {
            Console.WriteLine($"No overlay at {overlayPath}; nothing to enable.");
            return 0;
        }
        var overlay = OverlayIO.Load(overlayPath);
        EnsureOverlayBindsTo(overlay, ruleset);

        var disabled = overlay.Disabled.Where(d => d.RuleId != ruleId).ToList();
        if (disabled.Count == overlay.Disabled.Count)
        {
            Console.WriteLine($"{ruleId} was not disabled in {overlayPath}; nothing to do.");
            return 0;
        }
        var updated = overlay with { Disabled = disabled };
        OverlayIO.Save(updated, overlayPath);
        Console.WriteLine($"Re-enabled {ruleId} in {overlayPath}");
        Console.WriteLine($"Overlay fingerprint: {updated.Fingerprint().Value}");
        return 0;
    }

    static int Annotate(string[] args, TimeProvider time)
    {
        var f = ParseFlags(args);
        var rulesetPath = f.GetValueOrDefault("ruleset") ?? throw new ArgumentException("--ruleset required");
        var overlayPath = f.GetValueOrDefault("overlay") ?? throw new ArgumentException("--overlay required");
        var ruleId = f.GetValueOrDefault("rule") ?? throw new ArgumentException("--rule required");
        var note = f.GetValueOrDefault("note") ?? throw new ArgumentException("--note required");
        var by = f.GetValueOrDefault("by");

        var ruleset = RuleSetIO.Load(rulesetPath);
        if (ruleset.Rules.All(r => r.Id != ruleId))
            throw new InvalidOperationException($"Rule '{ruleId}' does not exist in {ruleset.Id}@{ruleset.Version}.");

        var overlay = OverlayIO.LoadOrEmpty(overlayPath, ruleset, time, by);
        EnsureOverlayBindsTo(overlay, ruleset);

        var notes = new List<RuleAnnotation>(overlay.Annotations)
        {
            new(ruleId, note, time.GetUtcNow()) { AuthoredBy = by },
        };
        var updated = overlay with { Annotations = notes };

        OverlayIO.Save(updated, overlayPath);
        Console.WriteLine($"Annotated {ruleId} in {overlayPath}");
        Console.WriteLine($"  note: {note}");
        Console.WriteLine($"  by:   {by ?? "(unspecified)"}");
        return 0;
    }

    static void EnsureOverlayBindsTo(RuleOverlay overlay, RuleSet ruleset)
    {
        if (!string.Equals(overlay.RuleSetId, ruleset.Id, StringComparison.Ordinal) ||
            !string.Equals(overlay.RuleSetVersion, ruleset.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Overlay binds to {overlay.RuleSetId}@{overlay.RuleSetVersion} but ruleset is {ruleset.Id}@{ruleset.Version}. " +
                "Refusing to mutate. Regenerate the overlay against the current ruleset.");
        }
    }

    static Dictionary<string, string> ParseFlags(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                map[args[i][2..]] = args[i + 1];
                i++;
            }
        }
        return map;
    }
}
