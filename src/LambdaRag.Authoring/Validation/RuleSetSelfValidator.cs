using LambdaRag.Core.Domain;

namespace LambdaRag.Authoring.Validation;

/// <summary>
/// Walks a <see cref="RuleSet"/>, runs <see cref="RuleSelfValidator"/>
/// against every rule that has examples, and produces a
/// <see cref="RuleSetValidationReport"/>.
///
/// Rules without examples are skipped quietly — Phase B is opt-in. The
/// caller decides what to do with rejections (typically: fail the
/// authoring run when AllAccepted is false).
/// </summary>
public sealed class RuleSetSelfValidator
{
    private readonly RuleSelfValidator _ruleValidator;
    private readonly IRuleEmbedder _embedder;
    private readonly double _epsilon;

    public RuleSetSelfValidator(IRuleEmbedder embedder, double epsilon = RuleSelfValidator.DefaultEpsilon)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _epsilon = epsilon;
        _ruleValidator = new RuleSelfValidator(embedder, epsilon);
    }

    public async Task<RuleSetValidationReport> ValidateAsync(RuleSet ruleset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ruleset);

        var results = new List<RuleValidationResult>(ruleset.Rules.Count);
        foreach (var rule in ruleset.Rules.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (rule.Examples is null) continue;
            var r = await _ruleValidator.ValidateAsync(rule, ct).ConfigureAwait(false);
            results.Add(r);
        }

        return new RuleSetValidationReport(
            RulesetId: ruleset.Id,
            RulesetVersion: ruleset.Version,
            EmbedderId: _embedder.EmbedderId,
            Epsilon: _epsilon,
            Results: results,
            AllAccepted: results.All(r => r.Accepted));
    }

    /// <summary>
    /// Build a new <see cref="RuleSet"/> in which every accepted rule has
    /// its <see cref="Rule.GateThreshold"/> replaced with the calibrated
    /// value from <paramref name="report"/>. Rules not present in the
    /// report (no examples) are returned untouched. Rejected rules are
    /// returned untouched too — the caller is expected to fail the run
    /// before publishing.
    /// </summary>
    public static RuleSet ApplyCalibratedThresholds(RuleSet ruleset, RuleSetValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(report);

        var byId = report.Results.ToDictionary(r => r.RuleId, StringComparer.Ordinal);
        var rewritten = new List<Rule>(ruleset.Rules.Count);
        foreach (var rule in ruleset.Rules)
        {
            if (byId.TryGetValue(rule.Id, out var v) && v.Accepted)
            {
                rewritten.Add(rule with { GateThreshold = v.CalibratedThreshold });
            }
            else
            {
                rewritten.Add(rule);
            }
        }
        return ruleset with { Rules = rewritten };
    }
}
