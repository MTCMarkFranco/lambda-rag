using LambdaRag.Core.Domain;
using LambdaRag.Core.Semantic;
using RE = RulesEngine.Models;

namespace LambdaRag.Evaluation.Workflow;

/// <summary>
/// Builds RulesEngine workflows from a single LambdaRag <see cref="Rule"/>.
/// We construct one workflow per rule per matched section: this keeps the
/// input shape simple ("input1" is the matched sub-graph, exactly the
/// schema declared by the rule's applies_to_schema) and makes verdict
/// attribution trivial.
///
/// Two distinct workflows are produced per rule:
///   1. <see cref="ForPredicate"/> — the applicability gate. Returns true
///      when the rule applies to this section.
///   2. <see cref="ForRule"/>      — the actual lambda. Returns true when
///      the section passes; false when it fails.
/// </summary>
public static class WorkflowFactory
{
    /// <summary>The workflow name used for every single-rule workflow we build.</summary>
    public const string WorkflowName = "lambda-rag.rule";

    /// <summary>The single rule's name within the workflow — referenced by the result.</summary>
    public const string RuleName = "rule";

    /// <summary>The workflow name used for predicate gates.</summary>
    public const string PredicateWorkflowName = "lambda-rag.predicate";

    /// <summary>The single rule's name within a predicate workflow.</summary>
    public const string PredicateRuleName = "predicate";

    public static RE.Workflow ForRule(Rule rule)
    {
        return new RE.Workflow
        {
            WorkflowName = WorkflowName,
            Rules = new[]
            {
                new RE.Rule
                {
                    RuleName = RuleName,
                    Expression = rule.Lambda,
                    RuleExpressionType = RE.RuleExpressionType.LambdaExpression,
                    SuccessEvent = "pass",
                    ErrorMessage = $"Rule '{rule.Id}' failed: {rule.NaturalLanguage}",
                },
            },
        };
    }

    /// <summary>
    /// Builds the <see cref="RE.ReSettings"/> the engine should be constructed
    /// with so that <c>SemanticFunctions.ContainsMeaning(...)</c> and
    /// <c>SemanticFunctions.MatchesAnyMeaning(...)</c> resolve inside lambda
    /// expressions. The function bodies themselves resolve the ambient
    /// <see cref="ISemanticVectorStore"/> via
    /// <see cref="VectorStoreAccessor"/>.
    /// </summary>
    public static RE.ReSettings CreateReSettings()
    {
        return new RE.ReSettings
        {
            CustomTypes = new[]
            {
                typeof(SemanticFunctions),
                typeof(LambdaPrimitives),
            },
        };
    }

    /// <summary>
    /// Builds a one-rule workflow for the rule's predicate. The predicate
    /// is the applicability gate — when the workflow result is "success",
    /// the rule applies to the section and the lambda must run.
    /// </summary>
    public static RE.Workflow ForPredicate(Rule rule)
    {
        return new RE.Workflow
        {
            WorkflowName = PredicateWorkflowName,
            Rules = new[]
            {
                new RE.Rule
                {
                    RuleName = PredicateRuleName,
                    Expression = string.IsNullOrWhiteSpace(rule.Predicate) ? "true" : rule.Predicate,
                    RuleExpressionType = RE.RuleExpressionType.LambdaExpression,
                    SuccessEvent = "applies",
                    ErrorMessage = $"Predicate for rule '{rule.Id}' did not apply.",
                },
            },
        };
    }
}
