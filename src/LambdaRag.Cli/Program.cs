using System.Text.Json;
using LambdaRag.Authoring;
using LambdaRag.Authoring.AISearch;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.Authoring.Validation;
using LambdaRag.Cli;
using LambdaRag.Core.Semantic;
using LambdaRag.Core;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Evaluation;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Indexing;
using LambdaRag.Indexing.Abstractions;
using LambdaRag.Markup;
using LambdaRag.Parsing;
using LambdaRag.Projection;
using LambdaRag.Selectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Rule = LambdaRag.Core.Domain.Rule;

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
                "rules"    => await RulesCommand.RunAsync(args.Skip(1).ToArray(), TimeProvider.System),
                "ruleset"  => await RulesetAsync(args.Skip(1).ToArray()),
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
              lambda-rag review   --document <path> --ruleset <path> --out <dir> [--mode report|markup|both] [--overlay <path>] [--annotate-pass] [--rewrite]
              lambda-rag project  --document <path> --out <path>
              lambda-rag parse    --document <path> --out <path>
              lambda-rag coverage --document <path> --ruleset <path> --out <path>
              lambda-rag author   --chunk <path> --domain <name> --prefix <id-prefix> --out <path>
              lambda-rag author   --source <pdf-or-dir> --search-service <name> --storage-url <blob-url>
                                  [--container policies] [--indexer lambda-rag-rules-indexer]
                                  [--poll-seconds 5] [--timeout-minutes 15]
              lambda-rag ruleset pull --search-service <name> --domain <d> --version <v> --out <path>
                                  [--status approved] [--index lambda-rag-rules]
              lambda-rag index    --ruleset <path> [--out <path>]
              lambda-rag topic-map list
              lambda-rag topic-map show <id-or-path>
              lambda-rag topic-map coverage --ruleset <path> [--topic-map <id-or-path>]
              lambda-rag extract-rules --policy-dir <dir> --domain <name> --id <ruleset-id> --out <path>
                                       [--min-chars 200] [--prefix <id-prefix>]
              lambda-rag rules diff     <old.json> <new.json> [--out diff.json]
              lambda-rag rules show     --ruleset <path> --rule <id>
              lambda-rag rules disable  --ruleset <path> --overlay <path> --rule <id> --reason "..." [--by <name>]
              lambda-rag rules enable   --ruleset <path> --overlay <path> --rule <id>
              lambda-rag rules annotate --ruleset <path> --overlay <path> --rule <id> --note "..." [--by <name>]

            Common flags:
              --topic-map <id-or-path>   Override default contract.v1 topic map.
                                         Try `lambda-rag topic-map list` for ids.
            """);
    }

    static IServiceProvider BuildServices()
    {
        var configuration = BuildConfiguration();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(new FrozenTimeProvider(
            new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        services
            .AddLambdaRagParsing()
            .AddLambdaRagProjection()
            .AddLambdaRagSelectors()
            .AddLambdaRagEvaluation()
            .AddLambdaRagAuthoring(configuration)
            .AddLambdaRagIndexing()
            .AddLambdaRagMarkup();
        services.AddSingleton<CoverageService>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Builds the CLI's <see cref="IConfiguration"/> from
    /// <c>dotnet user-secrets</c> (preferred for local dev) plus environment
    /// variables (legacy / CI). Configure secrets via:
    ///   <c>dotnet user-secrets --project src/LambdaRag.Cli set "LambdaRag:Foundry:Endpoint" "https://..."</c>
    /// </summary>
    static IConfiguration BuildConfiguration()
        => new ConfigurationBuilder()
            .AddUserSecrets(typeof(CliEntry).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

    static Dictionary<string, string> ParseFlags(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length
                && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[args[i][2..]] = args[i + 1].Trim();
                i++;
            }
        }
        return map;
    }

    /// <summary>
    /// Boolean flag detection — returns true when <c>--name</c> appears anywhere
    /// in <paramref name="args"/>. Used for switches that take no value (e.g.
    /// <c>--annotate-pass</c>); value-bearing flags continue to use
    /// <see cref="ParseFlags"/>.
    /// </summary>
    static bool HasFlag(string[] args, string name)
    {
        var token = "--" + name;
        return args.Any(a => string.Equals(a, token, StringComparison.Ordinal));
    }

    static async Task<int> ReviewAsync(string[] args)
    {
        var f = ParseFlags(args);
        var documentPath = f.GetValueOrDefault("document") ?? throw new ArgumentException("--document required");
        var rulesetPath = f.GetValueOrDefault("ruleset") ?? throw new ArgumentException("--ruleset required");
        var outDir = f.GetValueOrDefault("out") ?? "out";
        var mode = (f.GetValueOrDefault("mode") ?? "report").ToLowerInvariant();
        if (mode is not ("report" or "markup" or "both"))
            throw new ArgumentException("--mode must be one of: report, markup, both");
        var annotatePass = HasFlag(args, "annotate-pass");
        var enableRewrite = HasFlag(args, "rewrite");
        Directory.CreateDirectory(outDir);

        await using var sp = (ServiceProvider)BuildServices();

        var parsers = sp.GetRequiredService<ParserRegistry>();
        var projector = sp.GetRequiredService<IDocumentProjector>();
        var evaluator = sp.GetRequiredService<EvaluationService>();
        var sigIndex = sp.GetRequiredService<IRuleSignatureIndex>();
        var markup = sp.GetRequiredService<OpenXmlMarkupService>();
        var ruleEmbedder = sp.GetRequiredService<IRuleEmbedder>();

        var ruleset = RuleSetIO.Load(rulesetPath);
        OverlayApplied? overlayAudit = null;
        var overlayPath = f.GetValueOrDefault("overlay");
        if (overlayPath is not null)
        {
            var overlay = OverlayIO.Load(overlayPath);
            var applied = OverlayApplier.Apply(ruleset, overlay);
            ruleset = applied.RuleSet;
            overlayAudit = applied.Audit;
            if (applied.UnknownRuleIds.Count > 0)
                AnsiConsole.MarkupLine($"[yellow]Overlay:[/]   {applied.UnknownRuleIds.Count} unknown rule id(s) ignored: {Markup.Escape(string.Join(", ", applied.UnknownRuleIds))}");
            AnsiConsole.MarkupLine($"[dim]Overlay:[/]   {Markup.Escape(overlayPath)}  fp={Markup.Escape(applied.Audit.Fingerprint.Value[..12])}…  disabled={applied.Audit.DisabledCount} notes={applied.Audit.AnnotatedCount}");
        }
        sigIndex.Build(ruleset);

        // ── Phase 1: Parse ──────────────────────────────────────────────
        var parsed = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[bold]Parsing[/] document…", async _ =>
                await parsers.ParseAsync(documentPath));

        // ── Phase 2: Project ────────────────────────────────────────────
        var topicMapSpec = f.GetValueOrDefault("topic-map");
        var effectiveProjector = topicMapSpec is null
            ? projector
            : new LambdaRag.Projection.Projectors.DeterministicContractProjector(TopicMapRegistry.Load(topicMapSpec));
        var projected = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[bold]Projecting[/] document…", async _ =>
                await effectiveProjector.ProjectAsync(parsed));

        // ── Phase 3: Embed (optional) ───────────────────────────────────
        var needsVectors = ruleset.Rules.Any(r =>
            r.GateThreshold > 0 ||
            r.Lambda.Contains("SemanticFunctions.", StringComparison.Ordinal));
        var effectiveEvaluator = evaluator;
        InMemorySemanticVectorStore? store = null;
        if (needsVectors)
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"[bold]Embedding[/] {ruleset.Rules.Count} rules + sections…", async _ =>
                {
                    var ruleSetEmbedder = new RuleSetEmbedder(ruleEmbedder);
                    store = await ruleSetEmbedder.EmbedAsync(ruleset);
                    var projEmbedder = new ProjectionEmbedder(ruleEmbedder);
                    store = await projEmbedder.EmbedSectionsAsync(projected, store);
                });

            var jitStore = new JitEmbeddingSemanticVectorStore(store!, ruleEmbedder);
            jitStore.RegisterSectionTexts(projected);

            effectiveEvaluator = new EvaluationService(
                sp.GetRequiredService<ISelectorMatcher>(),
                sp.GetRequiredService<ILogger<EvaluationService>>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ICandidateRuleFilter>(),
                jitStore);
            AnsiConsole.MarkupLine($"[dim]Vectors:[/]   embedder={Markup.Escape(ruleEmbedder.EmbedderId)} dims={ruleEmbedder.Dimensions}");
        }

        // ── Phase 4: Evaluate ───────────────────────────────────────────
        // Pillar 1 (#116) — resolve doc kind: explicit flag → filename
        // heuristic → heading-bigram classifier. Passed to the evaluator
        // which skips rules whose declared appliesToDocKinds excludes it.
        var explicitDocKind = f.GetValueOrDefault("doc-kind");
        var resolvedDocKind = DocKindResolver.Resolve(explicitDocKind, documentPath, parsed);
        if (!string.IsNullOrWhiteSpace(explicitDocKind) || resolvedDocKind != DocKindResolver.Unknown)
            AnsiConsole.MarkupLine($"[dim]Doc-kind:[/]  {Markup.Escape(resolvedDocKind)}");

        var report = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"[bold]Evaluating[/] {ruleset.Rules.Count} rules…", async _ =>
                await effectiveEvaluator.EvaluateAsync(ruleset, projected, resolvedDocKind));
        if (overlayAudit is not null)
            report = report with { OverlayApplied = overlayAudit };
        if (report.WrongProfile == true)
            AnsiConsole.MarkupLine($"[yellow]Profile:[/]   wrong_profile=true — every rule was skipped for doc-kind '{Markup.Escape(resolvedDocKind)}'.");

        var emitReport = mode is "report" or "both";
        var emitMarkup = mode is "markup" or "both";

        string? reportPath = null;
        if (emitReport)
        {
            reportPath = Path.Combine(outDir, "report.json");
            File.WriteAllText(reportPath, RuleSetIO.SerializeReport(report));
        }

        string? markupPath = null;
        if (emitMarkup)
        {
            // Markup mode requires a .docx source so OpenXmlMarkupService can
            // inject comments + tracked changes. For non-docx inputs we still
            // produce the report (when --mode both) and emit a clear note.
            if (!documentPath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Markup:    skipped — --mode {mode} requires a .docx source (got '{Path.GetExtension(documentPath)}')");
            }
            else
            {
                markupPath = Path.Combine(outDir, "reviewed.docx");
                var ruleLookup = ruleset.Rules.ToDictionary(r => r.Id, r => r, StringComparer.Ordinal);
                List<Annotation> annotations;
                if (enableRewrite)
                {
                    var rewriter = sp.GetRequiredService<IClauseRewriter>();
                    if (rewriter is NoopClauseRewriter)
                    {
                        Console.WriteLine(
                            "Rewrite:   --rewrite requested but no LLM editor agent is configured. "
                          + "No tracked-change replacements will be emitted; fail verdicts will appear as comments only.");
                        Console.WriteLine(
                            "           Set LAMBDA_RAG_FOUNDRY_EDIT_ENDPOINT, LAMBDA_RAG_FOUNDRY_EDIT_DEPLOYMENT, "
                          + "and LAMBDA_RAG_FOUNDRY_EDIT_API_KEY (or the matching LambdaRag:Foundry:Edit:* config keys) "
                          + "to enable the ComplianceEditor agent.");
                    }
                    annotations = new List<Annotation>();
                    // Resolve clause text from the parsed document's canonical
                    // text so the rewriter sees the *full* clause (including
                    // bullet/numbered structure separated by '\n'), not just
                    // the first evidence quote. Without this, multi-paragraph
                    // clauses (e.g. a bulleted Insurance section) collapse to
                    // a one-sentence rewrite that loses the list. Falls back
                    // to evidence on out-of-range / empty spans.
                    string ResolveClauseText(Verdict v)
                    {
                        var span = v.ClauseSpan ?? v.SourceSpan;
                        var canonical = parsed.CanonicalText;
                        if (span is null || span.CharLength <= 0
                            || span.CharStart < 0
                            || span.CharStart + span.CharLength > canonical.Length)
                        {
                            return v.EvidenceQuotes.Count > 0 ? v.EvidenceQuotes[0] : string.Empty;
                        }
                        return canonical.Substring(span.CharStart, span.CharLength);
                    }

                    var failVerdicts = report.Verdicts
                        .Where(v => v.Outcome is VerdictOutcome.Fail or VerdictOutcome.Error)
                        .ToList();

                    var rewrites = 0;
                    var skipReasons = new List<(string RuleId, string Reason)>();
                    await AnsiConsole.Progress()
                        .AutoClear(false)
                        .HideCompleted(false)
                        .Columns(
                            new TaskDescriptionColumn(),
                            new ProgressBarColumn { CompletedStyle = new Style(Color.Green) },
                            new SpinnerColumn(),
                            new RemainingTimeColumn())
                        .StartAsync(async ctx =>
                        {
                            var task = ctx.AddTask(
                                $"[bold]Rewriting[/] 0/{failVerdicts.Count}",
                                maxValue: failVerdicts.Count);

                            for (var i = 0; i < failVerdicts.Count; i++)
                            {
                                var v = failVerdicts[i];
                                var rule = ruleLookup.GetValueOrDefault(v.RuleId);
                                var ruleId = v.RuleId;
                                var truncatedId = ruleId.Length > 30 ? ruleId[..30] + "…" : ruleId;
                                task.Description = $"[bold]Rewriting[/] {i + 1}/{failVerdicts.Count} [green]({rewrites} redlined)[/] [dim]{truncatedId}[/]";

                                var comment = AnnotationFactory.BuildCommentForVerdict(v, rule);
                                var clauseText = ResolveClauseText(v);

                                if (v.Outcome != VerdictOutcome.Fail)
                                {
                                    skipReasons.Add((ruleId, "outcome is not Fail"));
                                    annotations.Add(comment);
                                    task.Increment(1);
                                    continue;
                                }
                                if (string.IsNullOrWhiteSpace(clauseText))
                                {
                                    skipReasons.Add((ruleId, "no clause text resolved"));
                                    annotations.Add(comment);
                                    task.Increment(1);
                                    continue;
                                }
                                if (AnnotationFactory.IsSkippableSpan(v))
                                {
                                    skipReasons.Add((ruleId, "title/preamble span (path=\"/\")"));
                                    annotations.Add(comment);
                                    task.Increment(1);
                                    continue;
                                }

                                string? rewrite = null;
                                try
                                {
                                    rewrite = await rewriter
                                        .RewriteAsync(v, clauseText, rule, default)
                                        .ConfigureAwait(false);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    skipReasons.Add((ruleId, $"LLM error: {ex.GetType().Name}"));
                                }

                                if (!string.IsNullOrWhiteSpace(rewrite))
                                {
                                    rewrites++;
                                    annotations.Add(AnnotationFactory.BuildReplaceAnnotation(v, comment, rewrite));
                                }
                                else
                                {
                                    if (!skipReasons.Any(s => s.RuleId == ruleId))
                                        skipReasons.Add((ruleId, "LLM returned empty/NO_REWRITE"));
                                    annotations.Add(comment);
                                }
                                task.Description = $"[bold]Rewriting[/] {i + 1}/{failVerdicts.Count} [green]({rewrites} redlined)[/] [dim]{truncatedId}[/]";
                                task.Increment(1);
                            }
                        });

                    Console.WriteLine($"Rewrite:   {rewrites} clause rewrite(s) emitted by {rewriter.GetType().Name}");
                    if (skipReasons.Count > 0)
                    {
                        AnsiConsole.WriteLine();
                        var table = new Table()
                            .Border(TableBorder.Rounded)
                            .AddColumn("[bold]Rule ID[/]")
                            .AddColumn("[bold]Skip Reason[/]");
                        foreach (var (rid, reason) in skipReasons)
                            table.AddRow(Markup.Escape(rid), Markup.Escape(reason));
                        AnsiConsole.Write(table);
                    }
                }
                else
                {
                    annotations = AnnotationFactory.FromReport(report, ruleLookup).ToList();
                }
                if (annotatePass)
                    annotations.AddRange(AnnotationFactory.BuildPassAnnotations(report, ruleLookup));
                var gapsSummary = AnnotationFactory.BuildGapsSummary(report, ruleLookup);
                if (gapsSummary is not null) annotations.Insert(0, gapsSummary);
                markup.Apply(documentPath, markupPath, annotations);
            }
        }

        // ── Final Summary ─────────────────────────────────────────────
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Spectre.Console.Rule("[bold]Review Summary[/]").LeftJustified());
        AnsiConsole.WriteLine();

        // Verdict breakdown table
        var verdictTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");
        verdictTable.AddRow("Document", Markup.Escape(parsed.Source.Id.ToString()));
        verdictTable.AddRow("RuleSet", Markup.Escape($"{ruleset.Id}@{ruleset.Version}"));
        verdictTable.AddRow("Score", $"[bold]{report.Score:F4}[/]");
        verdictTable.AddRow("Pass", $"[green]{report.Passed}[/]");
        verdictTable.AddRow("Fail", report.Failed > 0 ? $"[red]{report.Failed}[/]" : "0");
        verdictTable.AddRow("Gap", report.Gaps > 0 ? $"[yellow]{report.Gaps}[/]" : "0");
        verdictTable.AddRow("N/A", $"{report.NotApplicable}");
        verdictTable.AddRow("Error", report.Errored > 0 ? $"[red]{report.Errored}[/]" : "0");
        var withRemediation = report.Verdicts.Count(v => !string.IsNullOrEmpty(v.RemediationText));
        if (withRemediation > 0)
            verdictTable.AddRow("Remediations", $"[blue]{withRemediation}[/]");
        AnsiConsole.Write(verdictTable);

        // Per-verdict detail table
        AnsiConsole.WriteLine();
        var detailTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Rule ID[/]")
            .AddColumn("[bold]Outcome[/]")
            .AddColumn("[bold]Section[/]");
        foreach (var v in report.Verdicts.OrderBy(v => v.RuleId, StringComparer.Ordinal))
        {
            var outcomeMarkup = v.Outcome switch
            {
                VerdictOutcome.Pass => "[green]PASS[/]",
                VerdictOutcome.Fail => "[red]FAIL[/]",
                VerdictOutcome.Gap => "[yellow]GAP[/]",
                VerdictOutcome.NotApplicable => "[dim]N/A[/]",
                VerdictOutcome.Error => "[red bold]ERR[/]",
                _ => v.Outcome.ToString(),
            };
            var section = v.SourceSpan?.HeadingPath ?? v.ClauseSpan?.HeadingPath ?? "";
            if (section.Length > 50) section = section[..50] + "…";
            detailTable.AddRow(Markup.Escape(v.RuleId), outcomeMarkup, Markup.Escape(section));
        }
        AnsiConsole.Write(detailTable);

        // Output files table
        AnsiConsole.WriteLine();
        var fileTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Output[/]")
            .AddColumn("[bold]Path[/]");
        if (reportPath is not null)
            fileTable.AddRow("Report", Markup.Escape(Path.GetFullPath(reportPath)));
        if (markupPath is not null)
            fileTable.AddRow("Markup", Markup.Escape(Path.GetFullPath(markupPath)));
        if (fileTable.Rows.Count > 0)
            AnsiConsole.Write(fileTable);

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

        // Route to the AI Search authoring path when --source / --search-service
        // are supplied. The legacy --chunk path is preserved for offline use.
        if (f.ContainsKey("source") || f.ContainsKey("search-service"))
        {
            return await AuthorViaAiSearchAsync(f);
        }

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

    static async Task<int> AuthorViaAiSearchAsync(Dictionary<string, string> f)
    {
        var sources = (f.GetValueOrDefault("source") ?? throw new ArgumentException("--source required (file or directory)"));
        var serviceName = f.GetValueOrDefault("search-service") ?? throw new ArgumentException("--search-service required");
        var storageUrl = f.GetValueOrDefault("storage-url") ?? throw new ArgumentException("--storage-url required (e.g. https://<acct>.blob.core.windows.net)");
        var container = f.GetValueOrDefault("container") ?? "policies";
        var indexer = f.GetValueOrDefault("indexer") ?? "lambda-rag-rules-indexer";
        var pollSeconds = int.TryParse(f.GetValueOrDefault("poll-seconds") ?? "5", out var ps) ? ps : 5;
        var timeoutMinutes = int.TryParse(f.GetValueOrDefault("timeout-minutes") ?? "15", out var tm) ? tm : 15;

        var localPaths = ResolveLocalSources(sources);
        if (localPaths.Count == 0)
        {
            Console.Error.WriteLine($"No source files found at {sources}.");
            return 1;
        }

        var options = new AzureSearchAuthoringOptions
        {
            SearchServiceName = serviceName,
            StorageAccountUrl = storageUrl,
            SourceContainerName = container,
            IndexerName = indexer,
        };

        var driver = new AzureSearchAuthoringDriver(options);

        Console.WriteLine($"Uploading {localPaths.Count} file(s) to {storageUrl}/{container} ...");
        var uploaded = await driver.UploadSourcesAsync(localPaths);
        foreach (var name in uploaded)
        {
            Console.WriteLine($"  ↑ {name}");
        }

        Console.WriteLine($"Running indexer {indexer} ...");
        var result = await driver.RunIndexerAsync(
            pollInterval: TimeSpan.FromSeconds(pollSeconds),
            timeout: TimeSpan.FromMinutes(timeoutMinutes));

        Console.WriteLine($"Status:    {result.Status}");
        Console.WriteLine($"Processed: {result.ItemsProcessed}");
        Console.WriteLine($"Failed:    {result.ItemsFailed}");
        if (!result.Success)
        {
            Console.Error.WriteLine($"Indexer run did not succeed: {result.ErrorMessage}");
            return 2;
        }
        Console.WriteLine("✅ Authoring run succeeded.");
        return 0;
    }

    static List<string> ResolveLocalSources(string source)
    {
        if (Directory.Exists(source))
        {
            return Directory.EnumerateFiles(source)
                .Where(p => !Path.GetFileName(p).StartsWith('.'))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
        if (File.Exists(source)) return new List<string> { source };
        return new List<string>();
    }

    static async Task<int> RulesetAsync(string[] args)
    {
        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help")
        {
            Console.WriteLine("""
                lambda-rag ruleset pull     --search-service <name> --domain <d> --version <v> --out <path>
                                            [--status approved] [--index lambda-rag-rules]
                                            [--no-validate] [--epsilon 0.05] [--apply]
                lambda-rag ruleset validate --in <ruleset.json> [--out <report.json>]
                                            [--epsilon 0.05] [--apply] [--embedder foundry|deterministic]
                """);
            return 0;
        }

        return args[0] switch
        {
            "pull" => await RulesetPullAsync(args.Skip(1).ToArray()),
            "validate" => await RulesetValidateAsync(args.Skip(1).ToArray()),
            _ => UnknownCommand($"ruleset {args[0]}"),
        };
    }

    static async Task<int> RulesetPullAsync(string[] args)
    {
        var f = ParseFlags(args);
        var serviceName = f.GetValueOrDefault("search-service") ?? throw new ArgumentException("--search-service required");
        var domain = f.GetValueOrDefault("domain") ?? throw new ArgumentException("--domain required");
        var version = f.GetValueOrDefault("version") ?? throw new ArgumentException("--version required");
        var outPath = f.GetValueOrDefault("out") ?? throw new ArgumentException("--out required");
        var status = f.GetValueOrDefault("status") ?? "approved";
        var indexName = f.GetValueOrDefault("index") ?? "lambda-rag-rules";

        var options = new AzureSearchAuthoringOptions
        {
            SearchServiceName = serviceName,
            // StorageAccountUrl is unused for pull but required by the record.
            StorageAccountUrl = "https://unused.blob.core.windows.net",
            IndexName = indexName,
        };

        var puller = new AzureSearchSnapshotPuller(options);
        Console.WriteLine($"Pulling domain={domain} version={version} status={status} from {options.SearchEndpoint}/{indexName} ...");
        var result = await puller.PullAsync(domain, version, outPath, status: status);

        Console.WriteLine($"Rules:     {result.RuleCount}");
        Console.WriteLine($"Out:       {result.OutputPath}");
        Console.WriteLine($"SHA-256:   {result.ContentHash}");

        // Phase B (#73): self-validate the pulled ruleset unless caller opts out.
        // Validator only inspects rules that carry positive/negative examples;
        // pre-Phase-B rulesets sail through unchanged. A rejection fails the
        // pull so unsafe rules can't slip into production.
        var skipValidate = f.ContainsKey("no-validate");
        if (!skipValidate)
        {
            var epsilon = ParseEpsilon(f);
            var apply = f.ContainsKey("apply");
            var rc = await RunValidateAsync(outPath, reportOut: null, epsilon, apply, embedderPref: null);
            if (rc != 0) return rc;
        }
        return 0;
    }

    /// <summary>
    /// Phase B (#73): self-validate every rule that carries positive/negative
    /// examples and (optionally) bake calibrated thresholds back into the
    /// ruleset. Returns non-zero when any rule is rejected so CI can fail
    /// loudly instead of publishing an unvetted ruleset.
    /// </summary>
    static async Task<int> RulesetValidateAsync(string[] args)
    {
        var f = ParseFlags(args);
        var inPath = f.GetValueOrDefault("in") ?? throw new ArgumentException("--in required");
        var outPath = f.GetValueOrDefault("out");
        var epsilon = ParseEpsilon(f);
        var apply = f.ContainsKey("apply");
        var embedderPref = f.GetValueOrDefault("embedder");
        return await RunValidateAsync(inPath, outPath, epsilon, apply, embedderPref);
    }

    static double ParseEpsilon(IReadOnlyDictionary<string, string> flags)
    {
        if (!flags.TryGetValue("epsilon", out var raw)) return RuleSelfValidator.DefaultEpsilon;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var eps))
            throw new ArgumentException($"--epsilon '{raw}' is not a valid number.");
        return eps;
    }

    static async Task<int> RunValidateAsync(string rulesetPath, string? reportOut, double epsilon, bool apply, string? embedderPref)
    {
        var ruleset = RuleSetIO.Load(rulesetPath);

        IRuleEmbedder embedder;
        if (string.Equals(embedderPref, "deterministic", StringComparison.OrdinalIgnoreCase))
        {
            embedder = new DeterministicHashEmbedder();
        }
        else
        {
            var foundry = FoundryEmbedderFactory.TryCreate(BuildConfiguration());
            embedder = foundry is null ? new DeterministicHashEmbedder() : foundry;
        }
        Console.WriteLine($"Validating {rulesetPath} with embedder={embedder.EmbedderId} epsilon={epsilon:F4} ...");

        var validator = new RuleSetSelfValidator(embedder, epsilon);
        var report = await validator.ValidateAsync(ruleset);

        var writer = new AuthoringReportWriter();
        var reportPath = reportOut ?? Path.Combine("out", "authoring", $"{ruleset.Id}-{ruleset.Version}.json");
        await writer.WriteAsync(report, reportPath);

        Console.WriteLine($"Examined: {report.RuleCount} rules with examples ({ruleset.Rules.Count} total in ruleset)");
        Console.WriteLine($"Accepted: {report.AcceptedCount}");
        Console.WriteLine($"Rejected: {report.RejectedCount}");
        Console.WriteLine($"Report:   {reportPath}");

        foreach (var r in report.Results.Where(r => !r.Accepted))
            Console.WriteLine($"  REJECT {r.RuleId}: {r.RejectionReason}");

        if (!report.AllAccepted)
        {
            Console.Error.WriteLine("validate: ruleset has rejected rules — aborting.");
            return 2;
        }

        if (apply)
        {
            var calibrated = RuleSetSelfValidator.ApplyCalibratedThresholds(ruleset, report);
            RuleSetIO.Save(calibrated, rulesetPath);
            Console.WriteLine($"Applied calibrated thresholds to {report.AcceptedCount} rule(s) in {rulesetPath}.");
        }
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
                     || p.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
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
