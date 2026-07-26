using Acta;

namespace TestJobs;

public sealed record ContextProbe(string Note);

public sealed record ContextProbeResult(
    long JobIdFromContext,
    string JobNameFromContext,
    int? TenantIdFromContext,
    string? TenantKeyFromContext
);

/// <summary>
/// Instance handler whose <see cref="JobContext"/> arrives by constructor injection - the path
/// MediatR <c>IRequestHandler</c>s and pipeline behaviors rely on (they can't take a
/// <see cref="JobContext"/> method parameter). Echoes the injected context's identity so a spec can
/// prove <see cref="JobContext"/> is resolvable from the per-attempt DI scope.
/// </summary>
public sealed class ContextProbeHandler(JobContext context)
{
    [Job("context-probe")]
    public ContextProbeResult Run(ContextProbe input) => new(context.JobId, context.JobName, context.TenantId, context.TenantKey);
}
