namespace Acta.Features.Execution;

/// <summary>
/// Default <see cref="IJobContextAccessor"/>: a scoped holder the worker runtime sets at the top of
/// each attempt scope. Scoped rather than AsyncLocal-backed: the runtime resolves the handler from
/// the same attempt scope, so the set value is visible without execution-context flow, and
/// concurrent executors stay isolated by their separate scopes.
/// </summary>
internal sealed class JobContextAccessor : IJobContextAccessor
{
    public JobContext? JobContext { get; set; }
}
