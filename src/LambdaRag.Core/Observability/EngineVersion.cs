using System.Diagnostics;
using System.Reflection;

namespace LambdaRag.Core.Observability;

/// <summary>
/// Resolves engine version + git SHA for the run manifest / telemetry ledger.
/// Never throws — returns <c>"unknown"</c> when neither the assembly attribute
/// nor a local git working tree can produce a value.
/// </summary>
public static class EngineVersion
{
    private static readonly Lazy<(string Version, string GitSha)> _resolved = new(Resolve);

    public static string AssemblyVersion => _resolved.Value.Version;
    public static string GitSha => _resolved.Value.GitSha;

    private static (string Version, string GitSha) Resolve()
    {
        var asm = typeof(EngineVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "unknown";
        var git = ExtractGitFromInfo(info) ?? TryGitCli() ?? "unknown";
        return (info, git);
    }

    // Assembly informational version often looks like "1.2.3+abcdef" — the
    // trailing hash after '+' is the SourceLink-populated git SHA.
    private static string? ExtractGitFromInfo(string info)
    {
        var plus = info.IndexOf('+');
        if (plus < 0 || plus == info.Length - 1) return null;
        return info[(plus + 1)..];
    }

    private static string? TryGitCli()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(1500);
            return string.IsNullOrWhiteSpace(stdout) ? null : stdout;
        }
        catch { return null; }
    }
}
