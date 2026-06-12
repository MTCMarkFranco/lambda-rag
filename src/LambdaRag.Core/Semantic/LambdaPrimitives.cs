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
        "details to follow",
    };

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
