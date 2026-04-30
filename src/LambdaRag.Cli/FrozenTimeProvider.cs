namespace LambdaRag.Cli;

/// <summary>
/// A <see cref="TimeProvider"/> that returns the same UTC instant on every
/// call. Used by the CLI so re-running a review yields byte-identical
/// outputs (timestamps in <see cref="LambdaRag.Core.Domain.Verdict"/> and
/// <see cref="LambdaRag.Core.Domain.ComplianceReport"/> are stable).
/// </summary>
public sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => instant;
}
