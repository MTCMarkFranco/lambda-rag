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
    /// <summary>
    /// Generic fallback author label, used when a verdict has no
    /// matching rule definition (the rule was disabled / removed
    /// between report and markup) and category resolution can't run.
    /// Format mirrors <see cref="CommentFormatting.BuildAuthor"/> so the
    /// reviewer always sees the 🕵 prefix in the Word review pane.
    /// </summary>
    public const string Author =
        CommentFormatting.AuthorEmojiPrefix + CommentFormatting.GenericLabel + " guidance";

    /// <summary>
    /// Default mapping: every Failed / Error verdict becomes a Comment
    /// anchored to the matched section, with a category-derived author
    /// (e.g. <c>"🕵 - Legal guidance"</c>), an AC-style severity banner,
    /// the rule's optional plain-English synopsis, the natural-language
    /// statement, and (when present) the rendered remediation text.
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
            string author;
            string text;
            if (rule is null)
            {
                author = Author;
                text = $"{CommentFormatting.ErrorBanner}\n\nRule {v.RuleId} reported {v.Outcome}.";
                if (v.ErrorMessage is { Length: > 0 })
                    text += $"\n\nDetail: {v.ErrorMessage}";
            }
            else
            {
                author = CommentFormatting.BuildAuthor(rule);
                text = CommentFormatting.BuildBody(rule, v);
            }

            yield return new Annotation(
                Id: ContentHash.Compose("annot", v.Id, "comment").Value,
                Kind: AnnotationKind.Comment,
                Span: v.SourceSpan,
                Author: author,
                Text: text,
                Replacement: null);
        }
    }

    /// <summary>
    /// Opt-in mapping: every Pass verdict becomes a positive-confirmation
    /// Comment anchored to the matched section. Used by markup mode's
    /// <c>--annotate-pass</c> flag to produce full coverage proof — i.e.
    /// the reviewed document shows not only what failed, but what was
    /// checked and passed.
    ///
    /// Off by default. The volume can be high (one comment per matched
    /// section per applicable rule), so reviewers must opt in.
    ///
    /// Annotation Ids derive from <c>(verdict.Id, "pass")</c> so they are
    /// distinct from the Fail/Error ids in <see cref="FromReport"/> and
    /// the run is idempotent across two invocations of the same inputs.
    /// </summary>
    public static IEnumerable<Annotation> BuildPassAnnotations(ComplianceReport report, IReadOnlyDictionary<string, Rule> rules)
    {
        foreach (var v in report.Verdicts)
        {
            if (v.Outcome is not VerdictOutcome.Pass) continue;
            var rule = rules.GetValueOrDefault(v.RuleId);
            var statement = rule?.NaturalLanguage ?? v.RuleId;
            var author = rule is null ? Author : CommentFormatting.BuildAuthor(rule);
            var text = CommentFormatting.BuildPassBody(rule, v, statement);

            yield return new Annotation(
                Id: ContentHash.Compose("annot", v.Id, "pass").Value,
                Kind: AnnotationKind.Comment,
                Span: v.SourceSpan,
                Author: author,
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
        sb.Append("\U0001F4CB LAMBDA-RAG GAP ANALYSIS — ")
          .Append(gaps.Count)
          .AppendLine(" mandatory topic(s) not addressed:");
        foreach (var v in gaps)
        {
            var rule = rules.GetValueOrDefault(v.RuleId);
            var nl = rule?.NaturalLanguage ?? v.RuleId;
            var label = rule is null ? CommentFormatting.GenericLabel
                                     : CommentFormatting.ResolveCategoryLabel(rule);
            sb.Append("\n• [").Append(label).Append(" / ")
              .Append(v.RuleId).Append("] ").Append(nl);
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
