using System.Text.Json;
using LambdaRag.Authoring;
using LambdaRag.Cli;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Evaluation;
using LambdaRag.Evaluation.Engine;
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
            .AddLambdaRagAuthoring();
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

        var ruleset = RuleSetIO.Load(rulesetPath);
        var parsed = await parsers.ParseAsync(documentPath);
        var projected = await projector.ProjectAsync(parsed);
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
        var projected = await projector.ProjectAsync(parsed);
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
}
