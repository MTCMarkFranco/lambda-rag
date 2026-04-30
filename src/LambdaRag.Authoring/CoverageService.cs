using System.Text.Json.Nodes;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Evaluation.Workflow;
using Microsoft.Extensions.Logging;
using RE = RulesEngine;

namespace LambdaRag.Authoring;

/// <summary>
/// Audit-time tool that reports, for each rule, which sections of a
/// projected document the rule's predicate matched, plus a vector
/// similarity score from <see cref="IRuleEmbedder"/> as a *sanity signal
/// only* — never a runtime decision.
///
/// The runtime evaluator (LambdaRag.Evaluation.EvaluationService) does
/// not consult vectors. They live here purely for human review:
///   • "Did the predicate match the sections I expected?"
///   • "Are the matched sections semantically related to the source chunk
///     this rule was extracted from?"
/// </summary>
public sealed class CoverageService
{
    private readonly ISelectorMatcher _matcher;
    private readonly IRuleEmbedder _embedder;
    private readonly ILogger<CoverageService> _logger;

    public CoverageService(
        ISelectorMatcher matcher,
        IRuleEmbedder embedder,
        ILogger<CoverageService> logger)
    {
        _matcher = matcher;
        _embedder = embedder;
        _logger = logger;
    }

    public async Task<CoverageReport> AnalyzeAsync(
        RuleSet ruleSet,
        ProjectedDocument document,
        CancellationToken ct = default)
    {
        var perRule = new List<RuleCoverage>();

        foreach (var rule in ruleSet.Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var candidates = _matcher.Match(rule.Selector, document);

            var sections = new List<SectionCoverage>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var (applies, error) = await EvaluatePredicateAsync(rule, candidate).ConfigureAwait(false);
                var sectionId = ExtractSectionId(candidate);
                var sectionText = ExtractSectionText(candidate);
                double? similarity = null;
                if (rule.SourceEmbedding is not null && !string.IsNullOrEmpty(sectionText))
                {
                    var sectionVec = await _embedder.EmbedAsync(sectionText, ct).ConfigureAwait(false);
                    similarity = DeterministicHashEmbedder.Cosine(rule.SourceEmbedding, sectionVec);
                }
                sections.Add(new SectionCoverage(
                    SectionId: sectionId,
                    Path: candidate.Path,
                    Span: candidate.Span,
                    PredicateApplies: applies,
                    PredicateError: error,
                    SimilarityToSource: similarity));
            }

            perRule.Add(new RuleCoverage(
                RuleId: rule.Id,
                RuleVersion: rule.Version,
                PredicateText: rule.Predicate,
                CandidateCount: candidates.Count,
                AppliedCount: sections.Count(s => s.PredicateApplies),
                EmbedderId: rule.SourceEmbedding is null ? null : _embedder.EmbedderId,
                Sections: sections
                    .OrderBy(s => s.Path, StringComparer.Ordinal)
                    .ToList()));
        }

        return new CoverageReport(
            DocumentId: document.SourceId.Value,
            RuleSetId: ruleSet.Id,
            RuleSetVersion: ruleSet.Version,
            ProjectorId: document.ProjectorId,
            Rules: perRule);
    }

    private async Task<(bool Applies, string? Error)> EvaluatePredicateAsync(
        Rule rule, MatchedSection section)
    {
        try
        {
            var input = JsonToExpando.Convert(section.Node);
            var workflow = WorkflowFactory.ForPredicate(rule);
            var engine = new RE.RulesEngine([workflow]);
            var results = await engine
                .ExecuteAllRulesAsync(WorkflowFactory.PredicateWorkflowName, input!)
                .ConfigureAwait(false);
            var result = results.Single();
            return (result.IsSuccess, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Coverage predicate for rule {RuleId} errored at {Path}",
                rule.Id, section.Path);
            return (false, ex.Message);
        }
    }

    private static string? ExtractSectionId(MatchedSection s) =>
        s.Node is JsonObject o && o["id"] is JsonNode n ? n.GetValue<string>() : null;

    private static string? ExtractSectionText(MatchedSection s) =>
        s.Node is JsonObject o && o["text"] is JsonNode n ? n.GetValue<string>() : null;
}

public sealed record CoverageReport(
    string DocumentId,
    string RuleSetId,
    string RuleSetVersion,
    string ProjectorId,
    IReadOnlyList<RuleCoverage> Rules);

public sealed record RuleCoverage(
    string RuleId,
    string RuleVersion,
    string PredicateText,
    int CandidateCount,
    int AppliedCount,
    string? EmbedderId,
    IReadOnlyList<SectionCoverage> Sections);

public sealed record SectionCoverage(
    string? SectionId,
    string Path,
    SourceSpan Span,
    bool PredicateApplies,
    string? PredicateError,
    double? SimilarityToSource);
