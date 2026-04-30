using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Markup;

/// <summary>
/// A single annotation derived from a Verdict that the markup engine knows
/// how to render. Stable Id (derived from verdict id) means re-running over
/// the same input produces byte-identical artifacts.
/// </summary>
public sealed record Annotation(
    string Id,
    AnnotationKind Kind,
    SourceSpan Span,
    string Author,
    string Text,
    string? Replacement);

public enum AnnotationKind
{
    /// <summary>A reviewer comment anchored to the span.</summary>
    Comment,
    /// <summary>A tracked-change deletion.</summary>
    Delete,
    /// <summary>A tracked-change insertion (Replacement is the new text).</summary>
    Insert,
    /// <summary>A tracked-change replacement (delete + insert at same span).</summary>
    Replace,
}

public static class AnnotationFactory
{
    public const string Author = "lambda-rag";

    /// <summary>
    /// Default mapping: every Failed verdict becomes a Comment whose body
    /// is the rule's natural-language statement plus the failure reason.
    /// Callers can post-process to upgrade to Insert/Delete/Replace when a
    /// rule supplies a remediation string in its metadata.
    /// </summary>
    public static IEnumerable<Annotation> FromReport(ComplianceReport report, IReadOnlyDictionary<string, Rule> rules)
    {
        foreach (var v in report.Verdicts)
        {
            if (v.Outcome is not VerdictOutcome.Fail and not VerdictOutcome.Error) continue;
            var rule = rules.GetValueOrDefault(v.RuleId);
            var text = rule is null
                ? $"Rule {v.RuleId} reported {v.Outcome}."
                : $"[{rule.Severity}] {rule.NaturalLanguage}";
            if (v.ErrorMessage is { Length: > 0 })
                text += $"\n\nDetail: {v.ErrorMessage}";

            yield return new Annotation(
                Id: ContentHash.Compose("annot", v.Id, "comment").Value,
                Kind: AnnotationKind.Comment,
                Span: v.SourceSpan,
                Author: Author,
                Text: text,
                Replacement: null);
        }
    }
}
