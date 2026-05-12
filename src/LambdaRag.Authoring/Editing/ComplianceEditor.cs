using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LambdaRag.Core.Domain;
using LambdaRag.Markup;
using Microsoft.Agents.AI;

namespace LambdaRag.Authoring.Editing;

/// <summary>
/// Compliance-focused clause re-author backed by a Microsoft Foundry v2
/// endpoint **Prompt agent on the Responses API** (Microsoft Agent
/// Framework v2). The agent is constructed via:
///
/// <code>
/// var responseClient = azureOpenAI.GetOpenAIResponseClient(deployment);
/// var agent = responseClient.CreateAIAgent(instructions, name: "ComplianceEditor");
/// </code>
///
/// — not a Persistent Agent — so each rewrite is a stateless one-shot
/// turn. Determinism is enforced via a SHA-256 disk cache and strict
/// normalization on the model response.
///
/// The agent's only job is **clause re-authoring**: take the original
/// clause + the rule guidance + (optional) remediation hint, return the
/// new clause text that would make the document comply. No commentary,
/// no markdown, no JSON.
/// </summary>
public sealed class ComplianceEditor : IClauseRewriter
{
    public const string CacheSchemaVersion = "v1";

    public const string AgentName = "ComplianceEditor";

    public const string SystemPrompt = """
        You are ComplianceEditor, a focused compliance redlining agent.
        Your only job is to re-author a single contract clause so it
        complies with a stated rule.

        You will receive:
        - RULE: the compliance rule the clause currently violates.
        - REMEDIATION: optional drafting hint from the rule author.
        - CLAUSE: the original clause text from the document.

        Output ONLY the rewritten clause text. No preamble, no markdown,
        no quotes, no JSON, no "Here is...". Preserve the original tone
        and structure of the clause; change only what is needed to make
        the clause comply. Keep length proportional to the original; do
        not exceed 1000 characters. If you cannot confidently rewrite
        the clause (e.g. the input is too short to be a clause, or the
        rule guidance is ambiguous), output the single token NO_REWRITE.
        """;

    private readonly AIAgent _agent;
    private readonly ComplianceEditorOptions _options;

    public ComplianceEditor(AIAgent agent, ComplianceEditorOptions options)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Directory.CreateDirectory(_options.CacheDir);
    }

    public async Task<string?> RewriteAsync(
        Verdict verdict,
        string clauseText,
        Rule? rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (string.IsNullOrWhiteSpace(clauseText)) return null;

        var key = ComputeCacheKey(rule, verdict, clauseText);
        var cachePath = Path.Combine(_options.CacheDir, key + ".json");

        if (File.Exists(cachePath))
        {
            var cached = TryReadCache(cachePath);
            if (cached is not null) return cached.Length == 0 ? null : cached;
        }

        var userMessage = BuildUserMessage(rule, verdict, clauseText);
        string? rewrite;
        try
        {
            var response = await _agent
                .RunAsync(userMessage, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            rewrite = response?.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        rewrite = Normalize(rewrite, _options.MaxRewriteLength);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheEntry(
            RuleId: rule?.Id,
            VerdictId: verdict.Id,
            Rewrite: rewrite ?? string.Empty,
            CreatedAt: DateTimeOffset.UtcNow)));

        return string.IsNullOrEmpty(rewrite) ? null : rewrite;
    }

    public static string ComputeCacheKey(Rule? rule, Verdict verdict, string clauseText)
    {
        var canonical = string.Join('\u001f',
            CacheSchemaVersion,
            rule?.Id ?? string.Empty,
            rule?.Version ?? string.Empty,
            verdict.RuleId,
            verdict.RemediationText ?? string.Empty,
            clauseText);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string? Normalize(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Equals("NO_REWRITE", StringComparison.Ordinal)) return null;

        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\u201C') && (s[^1] == '"' || s[^1] == '\u201D'))
            s = s[1..^1].Trim();

        var sb = new StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            if (ch == '\n')
            {
                sb.Append('\n');
                prevSpace = false;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (prevSpace) continue;
                prevSpace = true;
                sb.Append(' ');
            }
            else
            {
                prevSpace = false;
                sb.Append(ch);
            }
        }
        s = sb.ToString().Trim();

        if (s.Length == 0) return null;
        if (s.Length > maxLength) s = s[..maxLength].TrimEnd() + "\u2026";
        return s;
    }

    private static string BuildUserMessage(Rule? rule, Verdict verdict, string clauseText)
    {
        var sb = new StringBuilder();
        sb.Append("RULE: ");
        sb.Append(rule?.NaturalLanguage ?? verdict.RuleId);
        sb.Append('\n');

        if (!string.IsNullOrWhiteSpace(verdict.RemediationText))
        {
            sb.Append("REMEDIATION: ").Append(verdict.RemediationText).Append('\n');
        }

        sb.Append("CLAUSE: ").Append(clauseText);
        return sb.ToString();
    }

    private static string? TryReadCache(string path)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
            return entry?.Rewrite;
        }
        catch
        {
            return null;
        }
    }

    private sealed record CacheEntry(
        string? RuleId,
        string VerdictId,
        string Rewrite,
        DateTimeOffset CreatedAt);
}
