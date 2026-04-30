using System.Text.Json;
using LambdaRag.Authoring;
using LambdaRag.Cli;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Evaluation;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Indexing;
using LambdaRag.Indexing.Abstractions;
using LambdaRag.Parsing;
using LambdaRag.Projection;
using LambdaRag.Selectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

return await CliEntry.RunAsync(args);

static class CliEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "review"   => await ReviewAsync(args.Skip(1).ToArray()),
                "project"  => await ProjectAsync(args.Skip(1).ToArray()),
                "parse"    => await ParseAsync(args.Skip(1).ToArray()),
                "coverage" => await CoverageAsync(args.Skip(1).ToArray()),
                "author"   => await AuthorAsync(args.Skip(1).ToArray()),
                "index"    => await IndexAsync(args.Skip(1).ToArray()),
                "topic-map" => await TopicMapAsync(args.Skip(1).ToArray()),
                "extract-rules" => await ExtractRulesAsync(args.Skip(1).ToArray()),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex}");
            return 1;
        }
    }

    static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"unknown command: {cmd}");
        PrintHelp();
        return 64;
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
            lambda-rag — deterministic rules-over-documents

            Usage:
              lambda-rag review   --document <path> --ruleset <path> --out <dir>
              lambda-rag project  --document <path> --out <path>
              lambda-rag parse    --document <path> --out <path>
              lambda-rag coverage --document <path> --ruleset <path> --out <path>
              lambda-rag author   --chunk <path> --domain <name> --prefix <id-prefix> --out <path>
              lambda-rag index    --ruleset <path> [--out <path>]
              lambda-rag topic-map list
              lambda-rag topic-map show <id-or-path>
              lambda-rag topic-map coverage --ruleset <path> [--topic-map <id-or-path>]
              lambda-rag extract-rules --policy-dir <dir> --domain <name> --id <ruleset-id> --out <path>
                                       [--min-chars 200] [--prefix <id-prefix>]

            Common flags:
              --topic-map <id-or-path>   Override default contract.v1 topic map.
                                         Try `lambda-rag topic-map list` for ids.
            """);
    }

    static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole());
        services.AddSingleton<TimeProvider>(new FrozenTimeProvider(
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        services
            .AddLambdaRagParsing()
            .AddLambdaRagProjection()
            .AddLambdaRagSelectors()
            .AddLambdaRagEvaluation()
            .AddLambdaRagAuthoring()
            .AddLambdaRagIndexing();
        services.AddSingleton<CoverageService>();
        return services.BuildServiceProvider();
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

    static async Task<int> ReviewAsync(string[] args)
    {
        var f = ParseFlags(args);
        var documentPath = f.GetValueOrDefault("document") ?? throw new ArgumentException("--document required");
        var rulesetPath = f.GetValueOrDefault("ruleset") ?? throw new ArgumentException("--ruleset required");
        var outDir = f.GetValueOrDefault("out") ?? "out";
        Directory.CreateDirectory(outDir);

        await using var sp = (ServiceProvider)BuildServices();

        var parsers = sp.GetRequiredService<ParserRegistry>();
        var projector = sp.GetRequiredService<IDocumentProjector>();
        var evaluator = sp.GetRequiredService<EvaluationService>();
        var sigIndex = sp.GetRequiredService<IRuleSignatureIndex>();

        var ruleset = RuleSetIO.Load(rulesetPath);
        sigIndex.Build(ruleset);
        var parsed = await parsers.ParseAsync(documentPath);
        var topicMapSpec = f.GetValueOrDefault("topic-map");
        var effectiveProjector = topicMapSpec is null
            ? projector
            : new LambdaRag.Projection.Projectors.DeterministicContractProjector(TopicMapRegistry.Load(topicMapSpec));
        var projected = await effectiveProjector.ProjectAsync(parsed);
        var report = await evaluator.EvaluateAsync(ruleset, projected);

        var reportPath = Path.Combine(outDir, "report.json");
        File.WriteAllText(reportPath, RuleSetIO.SerializeReport(report));

        Console.WriteLine($"Document:  {parsed.Source.Id}");
        Console.WriteLine($"RuleSet:   {ruleset.Id}@{ruleset.Version}");
        Console.WriteLine($"Score:     {report.Score:F4}");
        Console.WriteLine($"Verdicts:  pass={report.Passed} fail={report.Failed} n/a={report.NotApplicable} err={report.Errored}");
        var withRemediation = report.Verdicts.Count(v => !string.IsNullOrEmpty(v.RemediationText));
        if (withRemediation > 0)
            Console.WriteLine($"Rewrites:  {withRemediation} verdict(s) include a suggested remediation");
        Console.WriteLine($"Wrote:     {reportPath}");
        return 0;
    }

    static async Task<int> ProjectAsync(string[] args)
    {
        var f = ParseFlags(args);
        var documentPath = f.GetValueOrDefault("document") ?? throw new ArgumentException("--document required");
        var outPath = f.GetValueOrDefault("out") ?? "projection.json";

        await using var sp = (ServiceProvider)BuildServices();
        var parsers = sp.GetRequiredService<ParserRegistry>();
        var projector = sp.GetRequiredService<IDocumentProjector>();

        var parsed = await parsers.ParseAsync(documentPath);
        var topicMapSpec = f.GetValueOrDefault("topic-map");
        var effectiveProjector = topicMapSpec is null
            ? projector
            : new LambdaRag.Projection.Projectors.DeterministicContractProjector(TopicMapRegistry.Load(topicMapSpec));
        var projected = await effectiveProjector.ProjectAsync(parsed);
        File.WriteAllText(outPath, projected.Graph.ToJsonString(LambdaRag.Core.CanonicalJson.Options));
        Console.WriteLine($"Wrote:     {outPath}");
        return 0;
    }

    static async Task<int> ParseAsync(string[] args)
    {
        var f = ParseFlags(args);
        var documentPath = f.GetValueOrDefault("document") ?? throw new ArgumentException("--document required");
        var outPath = f.GetValueOrDefault("out") ?? "parsed.json";

        await using var sp = (ServiceProvider)BuildServices();
        var parsers = sp.GetRequiredService<ParserRegistry>();
        var parsed = await parsers.ParseAsync(documentPath);

        var dump = new
        {
            source = new { parsed.Source.Id, parsed.Source.FileName, parsed.Source.Kind },
            blocks = parsed.Blocks.Select(b => new
            {
                id = b.Id,
                kind = b.Kind.ToString(),
                heading_path = b.HeadingPath,
                text = b.Text,
                span = b.Span,
            }),
        };
        File.WriteAllText(outPath, System.Text.Json.JsonSerializer.Serialize(dump, LambdaRag.Core.CanonicalJson.Options));
        Console.WriteLine($"Wrote:     {outPath}");
        return 0;
    }

    static async Task<int> CoverageAsync(string[] args)
    {
        var f = ParseFlags(args);
        var documentPath = f.GetValueOrDefault("document") ?? throw new ArgumentException("--document required");
        var rulesetPath = f.GetValueOrDefault("ruleset") ?? throw new ArgumentException("--ruleset required");
        var outPath = f.GetValueOrDefault("out") ?? "coverage.json";

        await using var sp = (ServiceProvider)BuildServices();
        var parsers = sp.GetRequiredService<ParserRegistry>();
        var projector = sp.GetRequiredService<IDocumentProjector>();
        var coverage = sp.GetRequiredService<CoverageService>();

        var ruleset = RuleSetIO.Load(rulesetPath);
        var parsed = await parsers.ParseAsync(documentPath);
        var projected = await projector.ProjectAsync(parsed);
        var report = await coverage.AnalyzeAsync(ruleset, projected);

        File.WriteAllText(outPath, JsonSerializer.Serialize(report, LambdaRag.Core.CanonicalJson.Options));

        Console.WriteLine($"Document:  {parsed.Source.Id}");
        Console.WriteLine($"RuleSet:   {ruleset.Id}@{ruleset.Version}");
        foreach (var rc in report.Rules)
        {
            Console.WriteLine($"  {rc.RuleId}: candidates={rc.CandidateCount} applied={rc.AppliedCount}");
        }
        Console.WriteLine($"Wrote:     {outPath}");
        return 0;
    }

    static async Task<int> AuthorAsync(string[] args)
    {
        var f = ParseFlags(args);
        var chunkPath = f.GetValueOrDefault("chunk") ?? throw new ArgumentException("--chunk required");
        var domain = f.GetValueOrDefault("domain") ?? "contract";
        var prefix = f.GetValueOrDefault("prefix") ?? string.Empty;
        var outPath = f.GetValueOrDefault("out") ?? "authored.json";

        await using var sp = (ServiceProvider)BuildServices();
        var agent = sp.GetRequiredService<IRuleAuthoringAgent>();

        var content = await File.ReadAllTextAsync(chunkPath);
        var span = new SourceSpan(
            DocumentId: Path.GetFileName(chunkPath),
            CharStart: 0,
            CharLength: content.Length,
            PageNumber: 1,
            HeadingPath: null);

        var suggestions = await agent.AuthorAsync(new RuleAuthoringRequest(
            SourceContent: content,
            Domain: domain,
            RuleIdPrefix: prefix,
            SourceSpan: span));

        File.WriteAllText(outPath, JsonSerializer.Serialize(
            new { suggestions = suggestions.Select(s => new
            {
                rule = s.Rule,
                confidence = s.Confidence,
                rationale = s.Rationale,
            }) },
            LambdaRag.Core.CanonicalJson.Options));

        Console.WriteLine($"Authored:  {suggestions.Count} suggestion(s)");
        foreach (var s in suggestions)
        {
            Console.WriteLine($"  {s.Rule.Id}  conf={s.Confidence:F2}  predicate=\"{s.Rule.Predicate}\"");
        }
        Console.WriteLine($"Wrote:     {outPath}");
        return 0;
    }

    static Task<int> IndexAsync(string[] args)
    {
        var f = ParseFlags(args);
        var rulesetPath = f.GetValueOrDefault("ruleset") ?? throw new ArgumentException("--ruleset required");
        var outPath = f.GetValueOrDefault("out");

        using var sp = (ServiceProvider)BuildServices();
        var sigIndex = sp.GetRequiredService<IRuleSignatureIndex>();

        var ruleset = RuleSetIO.Load(rulesetPath);
        sigIndex.Build(ruleset);

        Console.WriteLine($"Index:        {sigIndex.IndexId}");
        Console.WriteLine($"Rules:        {sigIndex.RuleCount}");
        Console.WriteLine($"Universal:    {sigIndex.UniversalCount}");
        Console.WriteLine($"Narrowed:     {sigIndex.RuleCount - sigIndex.UniversalCount}");

        if (!string.IsNullOrEmpty(outPath))
        {
            var dump = new
            {
                index_id = sigIndex.IndexId,
                rule_count = sigIndex.RuleCount,
                universal_count = sigIndex.UniversalCount,
                signatures = ruleset.Rules
                    .OrderBy(r => r.Id, StringComparer.Ordinal)
                    .Select(r => sigIndex.GetSignature(r.Id))
                    .ToList(),
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(dump, LambdaRag.Core.CanonicalJson.Options));
            Console.WriteLine($"Wrote:        {outPath}");
        }
        return Task.FromResult(0);
    }

    static async Task<int> ExtractRulesAsync(string[] args)
    {
        var f = ParseFlags(args);
        var policyDir = f.GetValueOrDefault("policy-dir") ?? throw new ArgumentException("--policy-dir required");
        var domain = f.GetValueOrDefault("domain") ?? "contract";
        var id = f.GetValueOrDefault("id") ?? "rs_extracted";
        var outPath = f.GetValueOrDefault("out") ?? "extracted-ruleset.json";
        var prefix = f.GetValueOrDefault("prefix") ?? "X";
        var minChars = int.TryParse(f.GetValueOrDefault("min-chars"), out var mc) ? mc : 200;

        await using var sp = (ServiceProvider)BuildServices();
        var parsers = sp.GetRequiredService<ParserRegistry>();
        var agent = sp.GetRequiredService<IRuleAuthoringAgent>();

        var policyFiles = Directory.EnumerateFiles(policyDir)
            .Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"Policy dir:   {policyDir}");
        Console.WriteLine($"Files:        {policyFiles.Count}");

        var allRules = new List<Rule>();
        var skipped = 0; var examined = 0; var emitted = 0;

        foreach (var path in policyFiles)
        {
            var file = Path.GetFileName(path);
            ParsedDocument parsed;
            try { parsed = await parsers.ParseAsync(path); }
            catch (Exception ex)
            {
                Console.WriteLine($"  ! parse failed for {file}: {ex.Message}");
                continue;
            }

            var docPrefix = $"{prefix}-{Math.Abs(file.GetHashCode()) % 10000:D4}";
            var idx = 0;
            foreach (var block in parsed.Blocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text) || block.Text.Length < minChars) { skipped++; continue; }
                examined++;

                var span = new SourceSpan(
                    DocumentId: parsed.Source.Id.Value,
                    CharStart: block.Span.CharStart,
                    CharLength: block.Span.CharLength,
                    PageNumber: block.Span.PageNumber,
                    HeadingPath: block.HeadingPath);

                IReadOnlyList<RuleAuthoringSuggestion> suggestions;
                try
                {
                    suggestions = await agent.AuthorAsync(new RuleAuthoringRequest(
                        SourceContent: block.Text,
                        Domain: domain,
                        RuleIdPrefix: $"{docPrefix}-{idx:D3}",
                        SourceSpan: span));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ! author failed for {file}#{idx}: {ex.Message}");
                    idx++; continue;
                }

                foreach (var s in suggestions)
                {
                    allRules.Add(s.Rule);
                    emitted++;
                }
                idx++;
            }
            Console.WriteLine($"  - {file}: blocks={parsed.Blocks.Count} authored=so-far={emitted}");
        }

        // Deduplicate by lambda+predicate fingerprint, keep deterministic order
        var deduped = allRules
            .GroupBy(r => $"{r.PredicateHash().Value}::{r.LambdaHash().Value}")
            .Select(g => g.OrderBy(r => r.Id, StringComparer.Ordinal).First())
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        var ruleset = new RuleSet(
            Id: id,
            Version: "1.0.0",
            Domain: domain,
            PublishedAt: DateTimeOffset.UnixEpoch,
            Rules: deduped,
            Metadata: new Dictionary<string, string>
            {
                ["source_dir"] = policyDir,
                ["files_processed"] = policyFiles.Count.ToString(),
                ["blocks_examined"] = examined.ToString(),
                ["blocks_skipped_short"] = skipped.ToString(),
                ["raw_suggestions"] = emitted.ToString(),
                ["dedup_count"] = (emitted - deduped.Count).ToString(),
            });

        RuleSetIO.Save(ruleset, outPath);
        Console.WriteLine($"Examined:     {examined} blocks across {policyFiles.Count} files");
        Console.WriteLine($"Skipped:      {skipped} short blocks (<{minChars} chars)");
        Console.WriteLine($"Authored:     {emitted} raw suggestions");
        Console.WriteLine($"Final ruleset: {deduped.Count} unique rules");
        Console.WriteLine($"Wrote:        {outPath}");
        return 0;
    }

    static Task<int> TopicMapAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: lambda-rag topic-map <list|show|coverage> [args]");
            return Task.FromResult(64);
        }

        switch (args[0])
        {
            case "list":
            {
                Console.WriteLine("Embedded topic maps:");
                foreach (var id in TopicMapRegistry.ListEmbedded())
                    Console.WriteLine($"  {id}");
                return Task.FromResult(0);
            }
            case "show":
            {
                if (args.Length < 2) { Console.Error.WriteLine("usage: lambda-rag topic-map show <id-or-path>"); return Task.FromResult(64); }
                var map = TopicMapRegistry.Load(args[1]);
                Console.WriteLine($"Domain:    {map.Domain}");
                Console.WriteLine($"Version:   {map.Version}");
                Console.WriteLine($"Topics:    {map.Topics.Count}");
                foreach (var t in map.Topics.OrderBy(t => t.Id, StringComparer.Ordinal))
                    Console.WriteLine($"  {(t.Axis is null ? "•" : "↳")} {t.Id}{(t.Axis is null ? "" : $" [axis={t.Axis}]")}  kw={t.Keywords.Count}");
                return Task.FromResult(0);
            }
            case "coverage":
            {
                var f = ParseFlags(args.Skip(1).ToArray());
                var rulesetPath = f.GetValueOrDefault("ruleset") ?? throw new ArgumentException("--ruleset required");
                var topicMapSpec = f.GetValueOrDefault("topic-map") ?? "contract.v1";
                var ruleset = RuleSetIO.Load(rulesetPath);
                var map = TopicMapRegistry.Load(topicMapSpec);
                var cov = RulesetTopicVocabulary.Coverage(ruleset.Rules, map);

                Console.WriteLine($"Topic map:           {map.Domain} v{map.Version}");
                Console.WriteLine($"Ruleset:             {ruleset.Id}@{ruleset.Version} ({ruleset.Rules.Count} rules)");
                Console.WriteLine($"Topics referenced:   {cov.Referenced.Count}");
                Console.WriteLine($"Topics declared:     {cov.Declared.Count}");
                Console.WriteLine($"Missing from map:    {cov.MissingFromMap.Count}");
                foreach (var t in cov.MissingFromMap) Console.WriteLine($"  ! {t}");
                Console.WriteLine($"Unused in rules:     {cov.UnusedInRules.Count}");
                if (f.ContainsKey("verbose"))
                    foreach (var t in cov.UnusedInRules) Console.WriteLine($"  - {t}");

                var outPath = f.GetValueOrDefault("out");
                if (outPath is not null)
                {
                    File.WriteAllText(outPath, JsonSerializer.Serialize(cov, LambdaRag.Core.CanonicalJson.Options));
                    Console.WriteLine($"Wrote:               {outPath}");
                }
                return Task.FromResult(cov.IsFullyCovered ? 0 : 2);
            }
            default:
                Console.Error.WriteLine($"unknown topic-map action: {args[0]}");
                return Task.FromResult(64);
        }
    }
}
