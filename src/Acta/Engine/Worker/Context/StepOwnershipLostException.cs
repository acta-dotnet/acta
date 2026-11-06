namespace Acta;

/// <summary>
/// Thrown when <c>complete_job_step</c> returns <c>StaleVersion</c>: the step's version CAS matched no row, so this
/// attempt can no longer prove it owns the step. A framework-internal fault, not a handler control signal: a
/// concurrent execution re-claimed the job and advanced the step. The runner treats it as a retryable attempt abort
/// rather than a verdict: a row another execution owns no-ops at the <c>complete_executions</c> CAS, and a row this
/// attempt still owns re-arms under the failure budget instead of landing Failed with no recorded reason.
/// </summary>
internal sealed class StepOwnershipLostException(string stepName)
    : Exception($"Step '{stepName}' completion lost its version CAS; the job is owned by another execution.")
{
    public string StepName { get; } = stepName;
}
