using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Evaluation;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Parsing;
using LambdaRag.Projection;
using LambdaRag.Selectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddLambdaRagParsing()
    .AddLambdaRagProjection()
    .AddLambdaRagSelectors()
    .AddLambdaRagEvaluation();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { name = "lambda-rag", version = "0.1.0" }));

app.MapPost("/review", async (
    ReviewRequest req,
    ParserRegistry parsers,
    IDocumentProjector projector,
    EvaluationService evaluator,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    if (!File.Exists(req.DocumentPath)) return Results.BadRequest("documentPath not found");
    if (!File.Exists(req.RuleSetPath)) return Results.BadRequest("ruleSetPath not found");

    var parsed = await parsers.ParseAsync(req.DocumentPath, ct);
    var projected = await projector.ProjectAsync(parsed, ct);
    var ruleset = LambdaRag.Cli.RuleSetIO.Load(req.RuleSetPath);
    var report = await evaluator.EvaluateAsync(ruleset, projected, ct);
    return Results.Ok(report);
});

app.Run();

public record ReviewRequest(string DocumentPath, string RuleSetPath);

