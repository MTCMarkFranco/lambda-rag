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
    /// Default mapping: every Failed / Error verdict becomes a Comment
    /// anchored to the matched section, with the rule's natural-language
    /// statement and (when present) the rendered remediation text.
    ///
    /// Gap verdicts are deliberately excluded here — they are not tied to
    /// a specific paragraph in the reviewed document. Use
    /// <see cref="BuildGapsSummary"/> to render them as a single summary
    /// comment anchored to the top of the document.
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
            if (!string.IsNullOrWhiteSpace(v.RemediationText))
                text += $"\n\nSuggested remediation: {v.RemediationText}";
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

    /// <summary>
    /// Build a single summary Annotation that lists every Gap verdict —
    /// mandatory rules the reviewed document is silent on. Anchored to
    /// the top of the document (charStart=0). Returns <c>null</c> when
    /// the report has no gaps so callers can skip emission entirely.
    /// </summary>
    public static Annotation? BuildGapsSummary(ComplianceReport report, IReadOnlyDictionary<string, Rule> rules)
    {
        var gaps = report.Verdicts
            .Where(v => v.Outcome == VerdictOutcome.Gap)
            .OrderBy(v => v.RuleId, StringComparer.Ordinal)
            .ToList();
        if (gaps.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.Append("LAMBDA-RAG GAP ANALYSIS — ").Append(gaps.Count).AppendLine(" mandatory topic(s) not addressed:");
        foreach (var v in gaps)
        {
            var rule = rules.GetValueOrDefault(v.RuleId);
            var nl = rule?.NaturalLanguage ?? v.RuleId;
            sb.Append("\n• [").Append(v.RuleId).Append("] ").Append(nl);
        }
        var anchorSpan = new SourceSpan(report.DocumentId.Value, 0, 0, null, null);
        return new Annotation(
            Id: ContentHash.Compose("annot", "gaps-summary", report.RuleSetFingerprint.Value).Value,
            Kind: AnnotationKind.Comment,
            Span: anchorSpan,
            Author: Author,
            Text: sb.ToString(),
            Replacement: null);
    }
}
