namespace Acta.Modules.Execution.Definitions;

/// <summary>
/// Framework policy defaults applied when a <see cref="JobDescriptor"/> leaves a per-definition
/// policy column unset.
/// </summary>
/// <remarks>
/// Every <c>definitions</c> policy column is sourced from the <c>[Job]</c> attribute via the
/// descriptor; a null override falls back to the constant here. Centralizing the defaults keeps
/// SqlServer + Postgres from drifting on the resolved row shape.
/// </remarks>
internal static class JobDefinitionRegistration
{
    // Framework defaults that match the JobAttribute XML docs and entity invariants.
    // Canonical default expression - parses exactly to Backoff.Default (initial 60s, max 28800s,
    // multiplier 2.0000, jitter 0.1000): ranged expressions default multiplier 2.0 / jitter 0.1.
    public const string DefaultBackoffExpression = "1m..8h";
    public const int DefaultExecutionTimeoutSeconds = 5 * 60;
    public const int DefaultJobRetentionSeconds = 90 * 24 * 60 * 60;
}
