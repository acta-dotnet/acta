namespace Acta.Runtime.Modules.Execution.Definitions;

/// <summary>
/// Framework policy defaults applied when a <see cref="JobDescriptor"/> leaves a per-definition
/// policy column unset.
/// </summary>
/// <remarks>
/// Every <c>definitions</c> policy column is sourced from the <c>[Job]</c> attribute via the
/// descriptor; a null override falls back to the constant here. Centralizing the defaults keeps
/// SqlServer + Postgres from drifting on the resolved row shape. All three constants must stay
/// aligned with the values the JobAttribute XML docs and entity invariants state.
/// </remarks>
internal static class JobDefinitionRegistration
{
    /// <summary>
    /// Canonical default expression - parses exactly to Backoff.Default (initial 60s, max 86400s,
    /// multiplier 2.0000, jitter 0.1000). Spelled out rather than "1m..1d": a ranged expression
    /// silently implies x2 growth and 10% jitter, so the short form hid the actual policy from
    /// every reader of a definition row, the docs, and the dashboard.
    /// </summary>
    public const string DefaultBackoffExpression = "1m..1d x2 ~10%";
    public const int DefaultExecutionTimeoutSeconds = 5 * 60;
    public const int DefaultJobRetentionSeconds = 90 * 24 * 60 * 60;
}
