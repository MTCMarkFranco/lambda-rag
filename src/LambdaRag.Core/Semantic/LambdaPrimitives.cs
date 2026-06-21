using System.Text.RegularExpressions;

namespace LambdaRag.Core.Semantic;

/// <summary>
/// Pillar 3 + 5 (#118, #120) — pure-code lambda primitives registered with
/// RulesEngine so rule lambdas can call them by name. Every method here is:
///
///   • <b>Static and side-effect-free</b> — RulesEngine custom-type methods
///     must be static. Vector / phrasebook lookups go through ambient
///     accessors (<see cref="VectorStoreAccessor"/>,
///     <see cref="PhrasebookAccessor"/>) set per-evaluation by the engine.
///   • <b>Deterministic</b> — same arguments + same ambient state =
///     byte-identical bool result. No I/O, no clock.
///   • <b>Part of the rule artifact contract</b>. Renaming a method or
///     changing argument order is a major-version break.
///
/// These primitives replace the keyword-soup lambdas that the accuracy
/// experiment showed are too brittle for prose (e.g. <c>Contains("year")</c>
/// matching "yearly basis" → false positive).
/// </summary>
public static class LambdaPrimitives
{
    /// <summary>
    /// Marker prefix on every <see cref="InvalidOperationException"/> raised
    /// from inside this class. The evaluator's runtime-exception detector
    /// recognises this prefix and surfaces the failure as
    /// <c>VerdictOutcome.Error</c> instead of <c>Fail</c>.
    /// </summary>
    public const string ErrorMarker = "lambda-rag.primitives:";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Registered as <c>LambdaPrimitives.RegexMatch(text, pattern)</c>.
    /// Returns true iff <paramref name="pattern"/> matches anywhere in
    /// <paramref name="text"/>. Case-insensitive, singleline, 200ms timeout.
    /// Pattern is part of the lambda string so it is folded into the rule
    /// fingerprint — same regex always evaluates the same way.
    /// </summary>
    public static bool RegexMatch(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern)) return false;
        try
        {
            return Regex.IsMatch(
                text, pattern,
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
                RegexTimeout);
        }
        catch (RegexParseException ex)
        {
            throw new InvalidOperationException(
                $"{ErrorMarker} malformed regex pattern '{pattern}': {ex.Message}");
        }
    }

    /// <summary>
    /// Registered as <c>LambdaPrimitives.PhraseMatch(text, phrasebookId)</c>.
    /// True iff any phrase in the named phrasebook (looked up via
    /// <see cref="PhrasebookAccessor.RequireCurrent"/>) appears anywhere in
    /// <paramref name="text"/>. Case-insensitive substring match. Throws
    /// when the phrasebook is not registered — missing phrasebooks fail
    /// loud, never silent false.
    /// </summary>
    public static bool PhraseMatch(string text, string phrasebookId)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (string.IsNullOrEmpty(phrasebookId))
            throw new InvalidOperationException($"{ErrorMarker} phrasebook id is required.");
        var store = PhrasebookAccessor.RequireCurrent();
        if (!store.TryGetPhrases(phrasebookId, out var phrases))
            throw new InvalidOperationException(
                $"{ErrorMarker} no phrasebook registered with id '{phrasebookId}'. " +
                "Declare it in the ruleset's 'phrasebooks' map.");
        foreach (var phrase in phrases)
        {
            if (string.IsNullOrEmpty(phrase)) continue;
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Pillar 5 (#120) — registered as
    /// <c>LambdaPrimitives.IsTemplateBoilerplate(text)</c>. Returns true
    /// when the section is dominated by placeholder/template wording so a
    /// section-presence rule can FAIL instead of PASS on "section exists
    /// but is still TBD".
    ///
    /// Trigger when either:
    ///   • any verbatim placeholder phrase from the signed list appears, OR
    ///   • the placeholder phrases collectively cover &gt;= 30% of the
    ///     section's characters (long stretches of "to be completed" prose).
    ///
    /// Phrase list is part of this class so changing it bumps the binary
    /// version of the primitive — the ruleset cannot silently weaken the
    /// detector after publication.
    /// </summary>
    public static bool IsTemplateBoilerplate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Verbatim hit — high-confidence single-phrase trigger.
        foreach (var phrase in BoilerplatePhrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Density heuristic: cumulative boilerplate-character coverage
        // relative to overall text length. Catches sections that consist
        // entirely of placeholder snippets even when no single phrase
        // crossed the verbatim list (e.g. "(insert) (insert) (insert)").
        // 30% is the threshold from the plan's Pillar 5 spec.
        var total = text.Length;
        if (total < 20) return false; // nothing meaningful to score
        long covered = 0;
        foreach (var phrase in BoilerplatePhrases)
        {
            var idx = 0;
            while ((idx = text.IndexOf(phrase, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                covered += phrase.Length;
                idx += phrase.Length;
            }
        }
        return covered * 10 >= total * 3; // covered / total >= 0.3
    }

    /// <summary>
    /// Signed placeholder phrase list. Each entry is treated as a
    /// case-insensitive substring. Order is irrelevant; uniqueness is not
    /// enforced because the density heuristic is upper-bounded by the
    /// section length anyway.
    /// </summary>
    public static readonly IReadOnlyList<string> BoilerplatePhrases = new[]
    {
        "to be completed",
        "to be defined",
        "to be determined",
        "to be confirmed",
        "will be completed",
        "will be defined",
        "will be determined",
        "will be added",
        "tbd",
        "tba",
        "[insert",
        "<insert",
        "(insert)",
        "insert text here",
        "fill in",
        "fill-in",
        "lorem ipsum",
        "<placeholder>",
        "[placeholder]",
        "{placeholder}",
        "placeholder text",
        "n/a — to be added",
        "n/a - to be added",
        "coming soon",
        "[to do]",
        "todo:",
        "to-do:",
        "stub section",
        "click here to add",
        "this section will describe",
        "this section describes",
        "this section will be completed",
        "details to follow",
    };

    // ───────────── Pillar 9 — spike-ported pre-filter primitives ─────────

    // Minimum word count for a clause to count as a "real" prose sentence
    // (an obligation/discussion sentence). Short tag lines like
    // "Architecture Risks (ARB-1)" do not qualify.
    private const int ProseSentenceMinWords = 8;

    // Sentence boundary: . ! ? followed by whitespace or end-of-string,
    // NOT preceded by a digit (so "6.4.1" does not count as 3 sentences).
    private static readonly Regex ProseSentenceSplitRe = new(
        @"(?<!\d)[.!?](?:\s+|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    // (ARB-1), (ARB-2), (ARB-1 & ARB-2), (ARB-2 if required), with various
    // unicode dashes — matches the CTC PSA template tag pattern.
    private static readonly Regex ArbTagRe = new(
        @"\(ARB[\u2010\u2011\u2012\u2013\u2014\u2015\-]?[12](\s*&\s*ARB[\u2010-\u2015\-]?2)?(\s+if\s+required)?\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex TemplatePhraseRe = new(
        @"(to\s+be\s+completed\s+by|required\s+for\s+ARB[\u2010-\u2015\-]?[12]|click\s+to\s+read\s+message)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex GlossaryHeadingRe = new(
        @"\b(glossary|acronym|appendix|appendices|reference\s+links?|references)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    // Acronym-definition row: "TLS Transport Layer Security" — uppercase
    // acronym (2-6 chars), then 2-6 capitalised words, no digits/units.
    private static readonly Regex AcronymDefinitionRowRe = new(
        @"^[A-Z]{2,6}\s+[A-Z][A-Za-z]+(?:\s+[A-Z][A-Za-z]+){1,5}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex DigitOrPercentRe = new(
        @"[\d%]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>
    /// Counts sentences in <paramref name="text"/> whose word count clears
    /// the obligation-prose floor (<see cref="ProseSentenceMinWords"/>).
    /// Section/version numbers (e.g. "6.4.1") are not treated as sentence
    /// boundaries. Exposed so ruleset authors can write threshold-aware
    /// predicates and so test code can assert filter semantics directly.
    /// Pure, deterministic, no I/O.
    /// </summary>
    public static int CountProseSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var total = 0;
        foreach (var s in ProseSentenceSplitRe.Split(text))
        {
            // Word count via simple whitespace split — matches the spike's
            // `len(s.split())` semantics. Empty entries fall out naturally.
            var words = 0;
            foreach (var part in s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                _ = part;
                words++;
                if (words >= ProseSentenceMinWords) break;
            }
            if (words >= ProseSentenceMinWords) total++;
        }
        return total;
    }

    /// <summary>
    /// Pillar 9 — ported from policy-compiler-spike v0.1.1
    /// <c>_is_arb_scaffolding</c>. Returns <c>true</c> when the chunk is
    /// dominated by PSA section-template tags and placeholders (e.g.
    /// "Sendsuite Replacement X (ARB-1)" repeated several times, or
    /// "Required for ARB-2" stubs) and contains at most one real prose
    /// sentence.
    /// <para>
    /// Trigger: <c>(ARB-tag occurrences + template-phrase occurrences) ≥ 3</c>
    /// AND <see cref="CountProseSentences"/> ≤ 1.
    /// </para>
    /// <para>
    /// Use in a rule's lambda as
    /// <c>!LambdaPrimitives.IsArbScaffolding(input1.text)</c> to suppress
    /// passes driven by section-listing chunks that mention obligation
    /// keywords only as template scaffolding.
    /// </para>
    /// </summary>
    public static bool IsArbScaffolding(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var tagHits = ArbTagRe.Matches(text).Count;
        var templateHits = TemplatePhraseRe.Matches(text).Count;
        if (tagHits + templateHits < 3) return false;
        return CountProseSentences(text) <= 1;
    }

    /// <summary>
    /// Pillar 9 — ported from policy-compiler-spike v0.1.1
    /// <c>_is_glossary_or_appendix_listing</c> (post-ABCCo refinement).
    /// Returns <c>true</c> when the chunk is a glossary, acronym table,
    /// appendix listing, or reference-link block — chunks that lexically
    /// mention obligation concepts only as items in a reference list.
    /// <para>
    /// Triggers in two cases:
    /// </para>
    /// <list type="number">
    ///   <item>An explicit heading or line contains <c>glossary</c>,
    ///   <c>acronym</c>, <c>appendix</c>, <c>appendices</c>,
    ///   <c>reference links</c>, or <c>references</c> as a standalone
    ///   word, AND there are ≤ 2 real prose sentences.</item>
    ///   <item>The chunk has ≥ 4 acronym-DEFINITION rows
    ///   (uppercase 2–6-char acronym followed by 2–6 capitalised words,
    ///   no digits, no units) AND ≤ 2 real prose sentences. The digit/
    ///   unit guard prevents data tables like SLA percentages from being
    ///   mis-classified (observed in the ABCCo cross-doc validation).
    ///   </item>
    /// </list>
    /// </summary>
    public static bool IsGlossaryOrAppendixListing(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var s = raw.Trim();
            if (s.Length > 0) lines.Add(s);
        }
        if (lines.Count == 0) return false;

        var headingHit = false;
        foreach (var ln in lines)
        {
            if (GlossaryHeadingRe.IsMatch(ln)) { headingHit = true; break; }
        }
        if (!headingHit)
        {
            var definitionRows = 0;
            foreach (var ln in lines)
            {
                if (AcronymDefinitionRowRe.IsMatch(ln)
                    && !DigitOrPercentRe.IsMatch(ln))
                {
                    definitionRows++;
                }
            }
            if (definitionRows < 4) return false;
        }
        return CountProseSentences(text) <= 2;
    }

    // ─────────────────────────── Pillar 6 (#124) ───────────────────────────

    /// <summary>
    /// Pillar 6 — registered as
    /// <c>LambdaPrimitives.SemanticBindings(anchorName)</c>. Returns the
    /// list of tokens bound to <paramref name="anchorName"/> for the
    /// section currently being evaluated. Empty list when no token cleared
    /// the anchor's cosine threshold — the lambda can express
    /// <c>LambdaPrimitives.SemanticBindings("rpo").Count &gt; 0</c> as the
    /// canonical "any binding" check, which also fully evaluates as a
    /// boolean inside Microsoft RulesEngine.
    ///
    /// Bindings are sourced from the ambient
    /// <see cref="SemanticBindingAccessor"/> pushed by the evaluator
    /// before invoking the lambda. Calling outside an evaluation scope
    /// returns an empty list (rather than throwing) so the primitive is
    /// safe to compose with predicates and short-circuit operators.
    /// </summary>
    public static IReadOnlyList<TokenMatch> SemanticBindings(string anchorName)
    {
        if (string.IsNullOrEmpty(anchorName)) return Array.Empty<TokenMatch>();
        var scope = SemanticBindingAccessor.Current;
        if (scope is null) return Array.Empty<TokenMatch>();
        return scope.GetBindings(anchorName);
    }

    /// <summary>
    /// Pillar 7 (#129) — registered as
    /// <c>LambdaPrimitives.HasTopic(input1, "decision_records")</c>. Returns
    /// <c>true</c> iff the projected section's multi-label
    /// <c>topics</c> array contains <paramref name="topic"/> (ordinal
    /// string compare). Lets a rule predicate fire on sections where the
    /// target dimension is a secondary topic, not only the strict
    /// <c>primary_topic</c> — closing the recall gap left by Pillar 6 on
    /// documents whose PDF parser merged multiple dimensions into one
    /// coarse heading group.
    ///
    /// <para>
    /// Returns <c>false</c> (never throws) for: null <paramref name="input"/>,
    /// null/empty <paramref name="topic"/>, input without a <c>topics</c>
    /// member, non-enumerable <c>topics</c>, empty list, or a list whose
    /// elements are not strings (non-string entries are skipped, not
    /// thrown on).
    /// </para>
    ///
    /// <para>
    /// Axis-qualified topics like <c>platform:azure</c> match exactly —
    /// no prefix or substring fallback. Pure, deterministic, no I/O.
    /// </para>
    /// </summary>
    public static bool HasTopic(object? input, string topic)
    {
        if (input is null || string.IsNullOrEmpty(topic)) return false;

        IEnumerable<object?>? topics = null;

        // Path 1: ExpandoObject / plain Dictionary<string,object?> — kept
        // because unit tests, alt evaluators, and ad-hoc callers feed
        // these shapes directly.
        if (input is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue("topics", out var raw))
                topics = AsObjectEnumerable(raw);
        }
        // Path 2: JsonObject — direct-JsonNode call path (unit tests).
        else if (input is System.Text.Json.Nodes.JsonObject jo
                 && jo["topics"] is System.Text.Json.Nodes.JsonArray ja)
        {
            topics = ja.Select(n => (object?)(n is System.Text.Json.Nodes.JsonValue jv
                && jv.TryGetValue<string>(out var s) ? s : null));
        }
        // Path 3 (the production path) — Microsoft RulesEngine compiles
        // lambdas as Expression<Func<DynamicClass, bool>> via
        // DynamicClassFactory: the ExpandoObject we built in JsonToExpando
        // is reshaped into a generated class whose members are *typed
        // properties*, not dictionary entries. Reflection is therefore the
        // only thing that can read `topics` off whatever object the engine
        // hands us. Also covers any plain POCO author who exposes a
        // `topics` collection.
        else
        {
            var t = input.GetType();
            var prop = t.GetProperty("topics",
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.IgnoreCase);
            object? value = prop?.GetValue(input);
            if (value is null)
            {
                var field = t.GetField("topics",
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase);
                value = field?.GetValue(input);
            }
            topics = AsObjectEnumerable(value);
        }

        if (topics is null) return false;

        foreach (var element in topics)
        {
            if (element is string s2 && string.Equals(s2, topic, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static IEnumerable<object?>? AsObjectEnumerable(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case string:
                // Reject string (an IEnumerable<char>) — topics must be a
                // structured array, never a single string masquerading as
                // one.
                return null;
            case IEnumerable<object?> typed:
                return typed;
            case System.Collections.IEnumerable plain:
                // List<string>, object[], arrays generated by
                // DynamicClassFactory — wrap into the canonical
                // IEnumerable<object?> shape.
                return plain.Cast<object?>();
            default:
                return null;
        }
    }

    /// <summary>
    /// Pillar 7 (#129) — score-gated overload. Returns <c>true</c> iff
    /// <paramref name="topic"/> is in <c>input.topics</c> AND its
    /// <c>topic_scores</c> entry is at least <paramref name="minScore"/>.
    /// This is the right call for ARB-PSA-style rules where a body-only
    /// keyword match (score 0.4) is too weak to be considered "the
    /// section is about this topic" but a heading match (0.9) is.
    /// </summary>
    public static bool HasTopic(object? input, string topic, double minScore)
    {
        if (!HasTopic(input, topic)) return false;
        if (double.IsNaN(minScore) || minScore <= 0.0) return true;

        var raw = ReadMember(input, "topic_scores");

        if (raw is null) return false;

        // topic_scores at evaluation time: ExpandoObject (IDictionary
        // path) for direct callers, generated DynamicClass for RulesEngine
        // — both end up walked here.
        double? score = null;

        if (raw is IDictionary<string, object?> scoreDict)
        {
            if (scoreDict.TryGetValue(topic, out var s)) score = ToDouble(s);
        }
        else if (raw is System.Text.Json.Nodes.JsonObject jo
                 && jo[topic] is System.Text.Json.Nodes.JsonNode n)
        {
            score = ToDouble(n);
        }
        else if (raw is System.Collections.IDictionary nonGeneric
                 && nonGeneric.Contains(topic))
        {
            // POCO/legacy path: Dictionary<string, double> et al. The
            // generic IDictionary<string, object?> cast above won't match
            // because generic dictionary types are not covariant in their
            // value parameter.
            score = ToDouble(nonGeneric[topic]);
        }
        else
        {
            // Reflection fallback: property/field per topic name on the
            // generated dynamic class.
            var t = raw.GetType();
            var prop = t.GetProperty(topic,
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.IgnoreCase);
            object? value = prop?.GetValue(raw);
            if (value is null)
            {
                var field = t.GetField(topic,
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase);
                value = field?.GetValue(raw);
            }
            score = ToDouble(value);
        }

        return score.HasValue && score.Value >= minScore;
    }

    /// <summary>
    /// Pillar 9 — hybrid 4-arg overload ported from
    /// policy-compiler-spike v0.1.1. Returns <c>true</c> when EITHER:
    /// <list type="bullet">
    ///   <item>The metadata-projected <c>topics[]</c> array contains
    ///   <paramref name="topic"/> with <c>topic_scores</c> ≥ <paramref name="minScore"/>
    ///   (the legacy lexical path — same as the 3-arg overload), OR</item>
    ///   <item>The ambient <see cref="SemanticBindingAccessor"/> scope has
    ///   any non-empty bindings for <paramref name="semanticAnchorName"/>
    ///   (the semantic fallback — picks up chunks the projector
    ///   under-tagged but whose body text has at least one token binding
    ///   the rule's anchor).</item>
    /// </list>
    /// <para>
    /// The semantic path uses bindings already pre-filtered by the
    /// resolver's effective threshold (anchor.Threshold − offset). Pass
    /// the anchor's own name as <paramref name="semanticAnchorName"/> to
    /// get the natural "lexical-or-semantic" predicate gate.
    /// </para>
    /// <para>
    /// Returns <c>false</c> (never throws) when the ambient scope is
    /// missing — e.g. when called from a rule whose ruleset declared no
    /// semantic anchors, or from outside an evaluation. Pure,
    /// deterministic, no I/O of its own.
    /// </para>
    /// </summary>
    public static bool HasTopic(
        object? input,
        string topic,
        double minScore,
        string semanticAnchorName)
    {
        // Lexical path first — preserves byte-identity for chunks the
        // projector already tagged correctly.
        if (HasTopic(input, topic, minScore)) return true;

        // Semantic fallback. Empty anchor name disables the fallback so
        // ruleset authors can opt out per call site.
        if (string.IsNullOrEmpty(semanticAnchorName)) return false;
        var bindings = SemanticBindings(semanticAnchorName);
        return bindings.Count > 0;
    }

    /// <summary>
    /// Pillar 7.B (#130) — returns <c>true</c> when the section was
    /// emitted by the projector's anchor-driven synthetic-section
    /// post-pass (<c>is_synthetic_anchor == true</c>). The post-pass
    /// only emits a synthetic when the section body's embedding
    /// cleared a configured cosine threshold against one of the rule's
    /// own anchors, so this primitive is a *section-level* anchor-
    /// match witness — the runtime-equivalent of "an anchor for this
    /// rule's topic matched somewhere in this body".
    ///
    /// Rules that gate on per-token <c>SemanticBindings(name).Count</c>
    /// can OR this primitive in as an alternative pass path, so a
    /// synthetic section still passes even though its body text won't
    /// produce a token-level cosine ≥ the per-anchor threshold (0.78).
    /// </summary>
    public static bool HasSyntheticAnchor(object? input)
    {
        var raw = ReadMember(input, "is_synthetic_anchor");
        if (raw is null) return false;
        if (raw is bool b) return b;
        if (raw is System.Text.Json.Nodes.JsonValue jv
            && jv.TryGetValue<bool>(out var jb))
            return jb;
        if (raw is string s && bool.TryParse(s, out var parsed))
            return parsed;
        return false;
    }

    /// <summary>
    /// Pillar 7.B (#130) — overload that ALSO checks the synthetic
    /// anchor's name. Returns <c>true</c> when the section is synthetic
    /// AND <c>synthetic_anchor</c> equals <paramref name="anchorName"/>
    /// (case-sensitive). Lets a rule lambda demand the synthetic was
    /// emitted for a *specific* anchor, not just any anchor — useful
    /// when one rule defines several anchors and only some of them
    /// should count as evidence in a particular branch.
    /// </summary>
    public static bool HasSyntheticAnchor(object? input, string anchorName)
    {
        if (!HasSyntheticAnchor(input)) return false;
        if (string.IsNullOrEmpty(anchorName)) return false;
        var raw = ReadMember(input, "synthetic_anchor");
        if (raw is null) return false;
        string? name = raw switch
        {
            string s => s,
            System.Text.Json.Nodes.JsonValue jv when jv.TryGetValue<string>(out var js) => js,
            _ => raw.ToString(),
        };
        return string.Equals(name, anchorName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pillar 8 POC (#133) — resolves the <i>best</i> token-match binding for
    /// <paramref name="anchorName"/> in the active Pillar 6
    /// <see cref="SemanticBindingAccessor"/> scope. "Best" is highest
    /// <see cref="TokenMatch.Cosine"/>; ties broken deterministically by
    /// lowest <see cref="TokenMatch.CharStart"/>, then ordinal lower
    /// <see cref="TokenMatch.Text"/>. Returns <c>null</c> when the scope
    /// is absent, the anchor has no bindings, or the anchor name is
    /// null/empty. Never throws.
    ///
    /// <para>
    /// This is the locator step of the Pillar 8 extraction pattern:
    /// cosine-find <em>where</em> in the chunk the policy concept appears,
    /// so downstream extraction primitives can read the value adjacent to
    /// that span and apply the structural constraint.
    /// </para>
    /// </summary>
    public static TokenMatch? ResolveAnchorSpan(string anchorName)
    {
        if (string.IsNullOrEmpty(anchorName)) return null;
        var scope = SemanticBindingAccessor.Current;
        if (scope is null) return null;
        var bindings = scope.GetBindings(anchorName);
        if (bindings is null || bindings.Count == 0) return null;

        TokenMatch? best = null;
        foreach (var b in bindings)
        {
            if (best is null) { best = b; continue; }
            if (b.Cosine > best.Cosine) { best = b; continue; }
            if (b.Cosine < best.Cosine) continue;
            if (b.CharStart < best.CharStart) { best = b; continue; }
            if (b.CharStart > best.CharStart) continue;
            if (string.CompareOrdinal(b.Text, best.Text) < 0) best = b;
        }
        return best;
    }

    /// <summary>
    /// Pillar 8 POC (#133) — same as <see cref="ResolveAnchorSpan(string)"/>,
    /// but when the cosine-based bindings are empty, falls back to a
    /// case-insensitive literal whole-word search of
    /// <paramref name="anchorName"/> in <paramref name="text"/>. The
    /// leftmost literal occurrence is returned as a synthetic
    /// <see cref="TokenMatch"/> with <c>Cosine = 1.0</c>.
    ///
    /// <para>
    /// Rationale: acronyms like <c>RPO</c> / <c>RTO</c> are 3-letter
    /// tokens that often do not clear the rule-level cosine threshold
    /// against multi-word anchor texts ("recovery point objective rpo
    /// data loss"). When the literal acronym is present in the chunk,
    /// that's the strongest possible "where in the chunk" signal — no
    /// embedding can do better. The fallback preserves the Pillar 8
    /// architecture (locator → extractor → constraint) when the
    /// vocabulary in the doc happens to match the anchor name exactly.
    /// </para>
    ///
    /// <para>
    /// Deterministic by construction: the regex is a static word-boundary
    /// match on a Regex.Escape'd anchor name, leftmost wins, no
    /// re-ordering. Same text + same anchorName → byte-identical span.
    /// </para>
    /// </summary>
    public static TokenMatch? ResolveAnchorSpan(string anchorName, string text)
    {
        var cosine = ResolveAnchorSpan(anchorName);
        if (cosine is not null) return cosine;
        if (string.IsNullOrEmpty(anchorName) || string.IsNullOrEmpty(text)) return null;
        var pattern = @"\b" + System.Text.RegularExpressions.Regex.Escape(anchorName) + @"\b";
        System.Text.RegularExpressions.Match m;
        try
        {
            m = System.Text.RegularExpressions.Regex.Match(
                text, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null;
        }
        if (!m.Success) return null;
        return new TokenMatch(m.Value, 1.0, m.Index, m.Length);
    }

    private static readonly System.Text.RegularExpressions.Regex DurationRx = new(
        @"\b(\d+(?:[.,]\d+)?)\s*(hours|hour|hrs|hr|h|minutes|minute|mins|min|seconds|second|secs|sec|days|day|weeks|week|wk)(?:\b|(?=[A-Z]))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.CultureInvariant
        | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly char[] SentenceDelims = { '.', '!', '?', '\n', '\r' };

    /// <summary>
    /// Sentence-start scanner: walks backwards from <paramref name="beforePos"/>
    /// and returns the offset just AFTER the previous sentence terminator,
    /// or 0 if none found. A period / bang / question mark counts as a
    /// terminator only when followed by whitespace or end-of-text, so
    /// numeric literals like <c>4.5</c> do NOT split a sentence.
    /// </summary>
    private static int FindSentenceStart(string text, int beforePos)
    {
        var i = Math.Min(beforePos, text.Length - 1);
        while (i >= 0)
        {
            var c = text[i];
            if (c == '\n' || c == '\r') return i + 1;
            if (c == '.' || c == '!' || c == '?')
            {
                if (i + 1 >= text.Length) return i + 1;
                if (char.IsWhiteSpace(text[i + 1])) return i + 1;
            }
            i--;
        }
        return 0;
    }

    /// <summary>
    /// Sentence-end scanner: walks forward from <paramref name="afterPos"/>
    /// and returns the offset just AFTER the next sentence terminator, or
    /// <c>text.Length</c> if none found. Same digit-aware semantics as
    /// <see cref="FindSentenceStart(string, int)"/>.
    /// </summary>
    private static int FindSentenceEnd(string text, int afterPos)
    {
        for (var i = afterPos; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n' || c == '\r') return i + 1;
            if (c == '.' || c == '!' || c == '?')
            {
                if (i + 1 >= text.Length) return i + 1;
                if (char.IsWhiteSpace(text[i + 1])) return i + 1;
            }
        }
        return text.Length;
    }

    /// <summary>
    /// Pillar 8 POC (#133) — extracts a duration value from <paramref name="text"/>
    /// associated with the anchor span resolved by
    /// <see cref="ResolveAnchorSpan(string)"/> for <paramref name="anchorName"/>.
    /// Scope is the <em>sentence containing the anchor</em>, intersected with
    /// the ±<paramref name="windowChars"/> window. Within that scope, the
    /// match whose span is nearest the anchor wins (gap-distance, with
    /// leftmost / ordinal-text tiebreakers). Returns <c>null</c> when:
    /// <list type="bullet">
    /// <item><description><paramref name="text"/> is null or empty</description></item>
    /// <item><description><paramref name="anchorName"/> is null or empty</description></item>
    /// <item><description>the anchor has no resolvable binding in scope</description></item>
    /// <item><description>no duration pattern is present in the anchor's sentence</description></item>
    /// <item><description>the regex times out (200ms, locale-invariant)</description></item>
    /// </list>
    ///
    /// <para>
    /// Sentence terminators are <c>. ! ?</c> followed by whitespace (or
    /// end-of-text), plus <c>\n \r</c>. A bare <c>.</c> inside a numeric
    /// literal (<c>4.5</c>) does NOT split the sentence — the digit-aware
    /// scanner preserves "4.5 hours" as one extractable duration.
    /// </para>
    ///
    /// <para>
    /// Sentence scoping ensures <c>RPO: 4 hours. RTO: 2 hours.</c> yields
    /// the correct value for each anchor independently — a pure
    /// nearest-to-anchor metric ties when two durations are equidistant
    /// from the anchor, so we constrain to the anchor's own sentence first.
    /// </para>
    ///
    /// <para>Unit table (case-insensitive, locale-invariant):</para>
    /// <list type="bullet">
    /// <item><description><c>h | hr | hrs | hour | hours</c> → hours</description></item>
    /// <item><description><c>min | mins | minute | minutes</c> → minutes</description></item>
    /// <item><description><c>sec | secs | second | seconds</c> → seconds</description></item>
    /// <item><description><c>day | days</c> → days</description></item>
    /// <item><description><c>wk | week | weeks</c> → 7 × days</description></item>
    /// </list>
    /// Bare single-letter units (<c>m</c>, <c>s</c>, <c>d</c>, <c>w</c>) are
    /// intentionally excluded — too ambiguous in technical prose
    /// ("Section 4d", "$5m budget").
    ///
    /// <para>
    /// Decimal values accept both <c>4.5</c> and <c>4,5</c> (European
    /// decimal). Negative numbers and hyphenated forms (<c>4-hour</c>) do
    /// not match.
    /// </para>
    /// </summary>
    /// <summary>
    /// Pillar 8 POC (#133) — internal helper that builds the full set of
    /// anchor candidates for <paramref name="anchorName"/>: cosine bindings
    /// if any, otherwise literal whole-word regex matches in
    /// <paramref name="text"/>. Used by <see cref="ExtractDurationNear"/>
    /// to evaluate every candidate against every duration match and pick
    /// the (anchor, duration) pair with the smallest gap — the real
    /// anchor-value pairing rather than the leftmost mention.
    /// </summary>
    private static IReadOnlyList<TokenMatch> ResolveAnchorSpans(string anchorName, string text)
    {
        var scope = SemanticBindingAccessor.Current;
        var bindings = scope?.GetBindings(anchorName);
        if (bindings is { Count: > 0 }) return bindings;

        if (string.IsNullOrEmpty(text)) return Array.Empty<TokenMatch>();
        var pattern = @"\b" + System.Text.RegularExpressions.Regex.Escape(anchorName) + @"\b";
        System.Text.RegularExpressions.MatchCollection mc;
        try
        {
            mc = System.Text.RegularExpressions.Regex.Matches(
                text, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(200));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return Array.Empty<TokenMatch>();
        }
        if (mc.Count == 0) return Array.Empty<TokenMatch>();
        var list = new List<TokenMatch>(mc.Count);
        foreach (System.Text.RegularExpressions.Match m in mc)
            list.Add(new TokenMatch(m.Value, 1.0, m.Index, m.Length));
        return list;
    }

    public static TimeSpan? ExtractDurationNear(string text, string anchorName, int windowChars = 120)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(anchorName)) return null;
        if (windowChars < 0) windowChars = 0;

        var candidates = ResolveAnchorSpans(anchorName, text);
        if (candidates.Count == 0) return null;

        TimeSpan? overallBest = null;
        var bestAnchorGap = int.MaxValue;
        var bestAnchorCosine = double.NegativeInfinity;
        var bestAnchorStart = int.MaxValue;
        var bestMatchStart = int.MaxValue;
        string bestMatchText = string.Empty;

        foreach (var span in candidates)
        {
            var anchorStart = span.CharStart;
            var anchorEnd = span.CharStart + span.CharLength;
            if (anchorStart < 0 || anchorEnd > text.Length) continue;

            var winLo = Math.Max(0, anchorStart - windowChars);
            var winHi = Math.Min(text.Length, anchorEnd + windowChars);
            var sentStart = anchorStart > 0 ? FindSentenceStart(text, anchorStart - 1) : 0;
            var sentEnd = anchorEnd < text.Length ? FindSentenceEnd(text, anchorEnd) : text.Length;
            var lo = Math.Max(sentStart, winLo);
            var hi = Math.Min(sentEnd, winHi);
            if (lo >= hi) continue;
            var slice = text.Substring(lo, hi - lo);

            System.Text.RegularExpressions.MatchCollection matches;
            try { matches = DurationRx.Matches(slice); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { continue; }
            if (matches.Count == 0) continue;

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                var matchStart = lo + m.Index;
                var matchEnd = matchStart + m.Length;
                int gap =
                    matchStart >= anchorEnd ? matchStart - anchorEnd :
                    matchEnd <= anchorStart ? anchorStart - matchEnd :
                    0;

                // Deterministic preference order across ALL (anchor, duration)
                // pairs in this chunk:
                //   1. smaller anchor-to-duration gap (the real pairing)
                //   2. higher anchor cosine (more confident anchor first)
                //   3. lower anchor CharStart (leftmost anchor wins ties)
                //   4. lower duration CharStart (leftmost duration wins ties)
                //   5. ordinal-lower duration text
                bool wins =
                    gap < bestAnchorGap
                    || (gap == bestAnchorGap && span.Cosine > bestAnchorCosine)
                    || (gap == bestAnchorGap && span.Cosine == bestAnchorCosine
                        && anchorStart < bestAnchorStart)
                    || (gap == bestAnchorGap && span.Cosine == bestAnchorCosine
                        && anchorStart == bestAnchorStart && matchStart < bestMatchStart)
                    || (gap == bestAnchorGap && span.Cosine == bestAnchorCosine
                        && anchorStart == bestAnchorStart && matchStart == bestMatchStart
                        && string.CompareOrdinal(m.Value, bestMatchText) < 0);
                if (!wins) continue;

                var parsed = ParseDurationMatch(m);
                if (parsed is null) continue;

                overallBest = parsed;
                bestAnchorGap = gap;
                bestAnchorCosine = span.Cosine;
                bestAnchorStart = anchorStart;
                bestMatchStart = matchStart;
                bestMatchText = m.Value;
            }
        }
        return overallBest;
    }

    private static TimeSpan? ParseDurationMatch(System.Text.RegularExpressions.Match m)
    {
        var numText = m.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(
                numText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var n))
            return null;

        var unit = m.Groups[2].Value.ToLowerInvariant();
        return unit switch
        {
            "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(n),
            "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(n),
            "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(n),
            "day" or "days" => TimeSpan.FromDays(n),
            "wk" or "week" or "weeks" => TimeSpan.FromDays(n * 7),
            _ => null,
        };
    }

    /// <summary>
    /// Pillar 8 POC (#133) — boolean sugar over
    /// <see cref="ExtractDurationNear(string, string, int)"/>. Returns
    /// <c>true</c> when a duration is extractable near the resolved anchor
    /// span; <c>false</c> otherwise. Lets a rule lambda read naturally:
    /// <c>HasExtractedDurationNear(input1.text, "rpo")</c> — the policy's
    /// real intent is "did the section commit to a duration for RPO?",
    /// not "did the section mention the word RPO?".
    /// </summary>
    public static bool HasExtractedDurationNear(string text, string anchorName, int windowChars = 120)
        => ExtractDurationNear(text, anchorName, windowChars) is not null;

    /// <summary>
    /// Pillar 8 POC (#133) — 2-arg overload for the dynamic lambda parser,
    /// which does not bind to methods with default-valued parameters. Uses
    /// the default 120-char window.
    /// </summary>
    public static bool HasExtractedDurationNear(string text, string anchorName)
        => ExtractDurationNear(text, anchorName, 120) is not null;

    private static object? ReadMember(object? input, string memberName)
    {
        if (input is null) return null;
        if (input is IDictionary<string, object?> dict)
            return dict.TryGetValue(memberName, out var v) ? v : null;
        if (input is System.Text.Json.Nodes.JsonObject jo)
            return jo[memberName];

        var t = input.GetType();
        var prop = t.GetProperty(memberName,
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.IgnoreCase);
        if (prop is not null) return prop.GetValue(input);

        var field = t.GetField(memberName,
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.IgnoreCase);
        return field?.GetValue(input);
    }

    private static double? ToDouble(object? v)
    {
        switch (v)
        {
            case null: return null;
            case double d: return d;
            case float f: return f;
            case long l: return l;
            case int i: return i;
            case decimal m: return (double)m;
            case System.Text.Json.Nodes.JsonValue jv:
                if (jv.TryGetValue<double>(out var jd)) return jd;
                if (jv.TryGetValue<long>(out var jl)) return jl;
                return null;
            case string s:
                return double.TryParse(s,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed) ? parsed : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Pillar 6 — find the nearest decimal number to any binding within
    /// <paramref name="windowChars"/> characters and return it. Returns
    /// <see cref="double.NaN"/> when no number is found (so the lambda
    /// can guard with <c>!double.IsNaN(...)</c> — RulesEngine resolves
    /// <see cref="double.NaN"/> reliably).
    /// </summary>
    public static double ExtractNumberNear(string text, IReadOnlyList<TokenMatch> bindings, long windowChars = 40)
    {
        if (string.IsNullOrEmpty(text) || bindings is null || bindings.Count == 0) return double.NaN;
        var w = (int)Math.Max(1, Math.Min(windowChars, 4096));
        double? best = null;
        foreach (var b in bindings)
        {
            var lo = Math.Max(0, b.CharStart - w);
            var hi = Math.Min(text.Length, b.CharStart + b.CharLength + w);
            var window = text[lo..hi];
            foreach (System.Text.RegularExpressions.Match m in NumberRx.Matches(window))
            {
                if (double.TryParse(
                        m.Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var d))
                {
                    if (best is null) best = d;
                    break;
                }
            }
            if (best is not null) break;
        }
        return best ?? double.NaN;
    }

    /// <summary>
    /// Pillar 6 — return the text window around the first binding for
    /// evidence quoting. Empty string when no bindings exist.
    /// </summary>
    public static string NearestText(string text, IReadOnlyList<TokenMatch> bindings, long windowChars = 40)
    {
        if (string.IsNullOrEmpty(text) || bindings is null || bindings.Count == 0) return string.Empty;
        var w = (int)Math.Max(1, Math.Min(windowChars, 4096));
        var b = bindings[0];
        var lo = Math.Max(0, b.CharStart - w);
        var hi = Math.Min(text.Length, b.CharStart + b.CharLength + w);
        return text[lo..hi];
    }

    private static readonly System.Text.RegularExpressions.Regex NumberRx = new(
        @"-?\d+(?:\.\d+)?",
        System.Text.RegularExpressions.RegexOptions.Compiled);
}

/// <summary>
/// Pillar 6 — a single token that bound to an anchor at runtime. Exposed
/// to lambdas via <see cref="LambdaPrimitives.SemanticBindings"/>.
/// Locale-invariant by construction (all text is lowercased ASCII).
/// </summary>
public sealed record TokenMatch(string Text, double Cosine, int CharStart, int CharLength);

/// <summary>
/// Pillar 6 — per-evaluation scope holding the bindings the active rule
/// resolved against the current section. The evaluator pushes one of
/// these before invoking the lambda and clears it after, mirroring the
/// <see cref="VectorStoreAccessor"/> / <see cref="PhrasebookAccessor"/>
/// pattern. Scoped via <see cref="AsyncLocal{T}"/> so concurrent
/// evaluations on different rules are isolated.
/// </summary>
public interface ISemanticBindingScope
{
    IReadOnlyList<TokenMatch> GetBindings(string anchorName);
}

/// <summary>
/// Default dictionary-backed binding scope, used by the evaluator and by tests.
/// </summary>
public sealed class DictionarySemanticBindingScope : ISemanticBindingScope
{
    private readonly Dictionary<string, IReadOnlyList<TokenMatch>> _map;

    public DictionarySemanticBindingScope(IReadOnlyDictionary<string, IReadOnlyList<TokenMatch>>? bindings = null)
    {
        _map = new Dictionary<string, IReadOnlyList<TokenMatch>>(StringComparer.Ordinal);
        if (bindings is null) return;
        foreach (var (k, v) in bindings) _map[k] = v;
    }

    public IReadOnlyList<TokenMatch> GetBindings(string anchorName)
        => _map.TryGetValue(anchorName, out var v) ? v : Array.Empty<TokenMatch>();
}

/// <summary>
/// Ambient holder for the active <see cref="ISemanticBindingScope"/>. The
/// evaluator pushes the current rule's bindings before invoking
/// RulesEngine and clears it after. AsyncLocal so concurrent evaluations
/// remain isolated.
/// </summary>
public static class SemanticBindingAccessor
{
    private static readonly AsyncLocal<ISemanticBindingScope?> _current = new();

    public static ISemanticBindingScope? Current => _current.Value;

    public static IDisposable Push(ISemanticBindingScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = _current.Value;
        _current.Value = scope;
        return new PopOnDispose(previous);
    }

    private sealed class PopOnDispose(ISemanticBindingScope? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}

/// <summary>
/// Read-only registry of named phrase lists scoped to a single ruleset.
/// Set per-evaluation by <see cref="PhrasebookAccessor.Push"/>.
/// </summary>
public interface IPhrasebookStore
{
    bool TryGetPhrases(string id, out IReadOnlyList<string> phrases);
}

/// <summary>
/// Default phrasebook store: thin wrapper around an
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/>. Suitable for tests and
/// for the runtime evaluator alike — the ruleset hydrates it once before
/// each review.
/// </summary>
public sealed class DictionaryPhrasebookStore : IPhrasebookStore
{
    private readonly Dictionary<string, IReadOnlyList<string>> _map;

    public DictionaryPhrasebookStore(IReadOnlyDictionary<string, IReadOnlyList<string>>? phrasebooks)
    {
        _map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (phrasebooks is null) return;
        foreach (var (k, v) in phrasebooks)
            _map[k] = v;
    }

    public bool TryGetPhrases(string id, out IReadOnlyList<string> phrases)
        => _map.TryGetValue(id, out phrases!);
}

/// <summary>
/// Ambient holder for the active <see cref="IPhrasebookStore"/>. The
/// evaluator pushes the ruleset's phrasebook map before invoking
/// RulesEngine and clears it after, so concurrent evaluations on
/// different rulesets are isolated per <see cref="AsyncLocal{T}"/>.
/// </summary>
public static class PhrasebookAccessor
{
    private static readonly AsyncLocal<IPhrasebookStore?> _current = new();

    public static IPhrasebookStore? Current => _current.Value;

    internal static IPhrasebookStore RequireCurrent() =>
        _current.Value ?? throw new InvalidOperationException(
            $"{LambdaPrimitives.ErrorMarker} PhraseMatch invoked outside an evaluation scope.");

    public static IDisposable Push(IPhrasebookStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var previous = _current.Value;
        _current.Value = store;
        return new PopOnDispose(previous);
    }

    private sealed class PopOnDispose(IPhrasebookStore? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
