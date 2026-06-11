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
