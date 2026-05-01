using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LambdaRag.Core.Domain;
using Microsoft.Extensions.AI;

namespace LambdaRag.Authoring.Synopsis;

/// <summary>
/// Authoring-time service that produces a one-sentence plain-English
/// synopsis for a given <see cref="Rule"/> by reverse-engineering the
/// rule's predicate / lambda / natural-language statement through a
/// small chat model (e.g. gpt-4o-mini).
///
/// **Determinism contract**
///
/// Lambda-rag is deterministic at runtime by design — the synopsis is
/// generated **once at authoring time** and baked into the ruleset JSON
/// (<c>Rule.Metadata["synopsis"]</c>). Runtime never calls an LLM. This
/// service writes a JSON disk cache keyed by a content hash so identical
/// inputs yield identical outputs across machines and re-runs, and the
/// LLM is consulted at most once per (rule version, lambda, statement)
/// triple.
/// </summary>
public sealed class SynopsisService
{
    /// <summary>Default soft cap on the synopsis length (chars).</summary>
    public const int MaxLength = 200;

    /// <summary>Cache key marker so older caches can be busted cleanly.</summary>
    public const string CacheSchemaVersion = "v1";

    private readonly IChatClient _chat;
    private readonly string _cacheDir;
    private readonly int _maxLength;

    public SynopsisService(IChatClient chat, string cacheDir, int maxLength = MaxLength)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _cacheDir = cacheDir ?? throw new ArgumentNullException(nameof(cacheDir));
        _maxLength = maxLength is > 40 and <= 400
            ? maxLength
            : throw new ArgumentOutOfRangeException(nameof(maxLength), "Synopsis length must be 40..400.");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Returns a one-sentence synopsis for the rule. Reads the disk cache
    /// first; on miss, calls the LLM, validates the result, and writes it
    /// back to the cache.
    /// </summary>
    public async Task<string> SynopsizeAsync(Rule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var key = ComputeCacheKey(rule);
        var cachePath = Path.Combine(_cacheDir, key + ".json");

        if (File.Exists(cachePath))
        {
            var cached = TryReadCache(cachePath);
            if (!string.IsNullOrWhiteSpace(cached))
                return cached!;
        }

        var synopsis = await CallLlmAsync(rule, ct).ConfigureAwait(false);
        synopsis = Normalize(synopsis, _maxLength);

        File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheEntry(
            RuleId: rule.Id,
            Version: rule.Version,
            Synopsis: synopsis,
            CreatedAt: DateTimeOffset.UtcNow)));

        return synopsis;
    }

    /// <summary>
    /// Returns the SHA-256 cache key for a rule. Pure-code so authors can
    /// pre-compute identical keys offline.
    /// </summary>
    public static string ComputeCacheKey(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var canonical = string.Join('\u001f',
            CacheSchemaVersion,
            rule.Id,
            rule.Version,
            rule.NaturalLanguage ?? string.Empty,
            rule.Predicate ?? string.Empty,
            rule.Lambda ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Trim and validate model output into a one-sentence form.</summary>
    public static string Normalize(string raw, int maxLength = MaxLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Synopsis was empty.");

        var s = raw.Trim().Trim('"');
        var sb = new StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            var c = char.IsWhiteSpace(ch) ? ' ' : ch;
            if (c == ' ')
            {
                if (prevSpace) continue;
                prevSpace = true;
            }
            else { prevSpace = false; }
            sb.Append(c);
        }
        s = sb.ToString().Trim();

        var firstStop = s.IndexOfAny(new[] { '.', '!', '?' });
        if (firstStop >= 0 && firstStop < s.Length - 1)
            s = s[..(firstStop + 1)];

        if (s.Length > maxLength)
            s = s[..maxLength].TrimEnd() + "\u2026";

        if (!s.EndsWith('.') && !s.EndsWith('!') && !s.EndsWith('?') && !s.EndsWith('\u2026'))
            s += ".";

        return s;
    }

    private static string? TryReadCache(string path)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
            return entry?.Synopsis;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> CallLlmAsync(Rule rule, CancellationToken ct)
    {
        const string system = """
            You write one-sentence plain-English summaries of compliance
            rules. The reader is a contract reviewer who wants to know,
            at a glance, what the rule checks for.

            Rules:
            - Output exactly one sentence, no preamble, no list.
            - <= 200 characters.
            - Describe the *intent* of the rule based on the natural-
              language statement, the predicate (which sections it
              applies to), and the lambda (the executable check).
            - Do not invent constraints or numbers that are not present
              in the source. Do not say "this rule" or "the rule".
            - Start with a present-tense verb (e.g. "Verifies", "Requires",
              "Flags", "Ensures").
            """;

        var user = $"""
            ID: {rule.Id}
            STATEMENT: {rule.NaturalLanguage}
            PREDICATE: {rule.Predicate}
            LAMBDA: {rule.Lambda}
            SEVERITY: {rule.Severity}
            """;

        var resp = await _chat.GetResponseAsync(
            new[] {
                new ChatMessage(ChatRole.System, system),
                new ChatMessage(ChatRole.User, user),
            },
            new ChatOptions
            {
                Temperature = 0.0f,
                Seed = 42,
                MaxOutputTokens = 120,
            },
            cancellationToken: ct).ConfigureAwait(false);

        return resp.Text ?? string.Empty;
    }

    private sealed record CacheEntry(
        string RuleId,
        string Version,
        string Synopsis,
        DateTimeOffset CreatedAt);
}
