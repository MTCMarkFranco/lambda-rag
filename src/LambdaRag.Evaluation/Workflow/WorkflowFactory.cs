using LambdaRag.Core.Domain;
using RE = RulesEngine.Models;

namespace LambdaRag.Evaluation.Workflow;

/// <summary>
/// Builds a RulesEngine Workflow from a single LambdaRag Rule. We construct
/// one workflow per rule per matched section: this keeps the input shape
/// simple ("input1" is the matched sub-graph, exactly the schema declared
/// by the rule's applies_to_schema) and makes verdict attribution trivial.
/// </summary>
internal static class WorkflowFactory
{
    /// <summary>The workflow name used for every single-rule workflow we build.</summary>
    public const string WorkflowName = "lambda-rag.rule";

    /// <summary>The single rule's name within the workflow — referenced by the result.</summary>
    public const string RuleName = "rule";

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
}
