namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// Thrown when <c>complete_step</c> returns <c>StaleVersion</c>: the step slot's version CAS
/// matched no row, so this attempt can no longer prove it owns the slot.
/// </summary>
/// <remarks>
/// A framework-internal fault, not a handler control signal. The usual cause is a concurrent
/// execution re-claiming the job and advancing the slot, but the signal can also be spurious: a
/// transient-retry re-run of a CAS batch whose first attempt committed reads zero changes and looks
/// identical. The runner therefore treats it as a retryable attempt abort rather than a verdict:
/// a row another execution owns no-ops at the <c>complete_execution</c> CAS, while a row this
/// attempt still owns re-arms under the failure budget and retries, replaying recorded steps.
/// </remarks>
internal sealed class StepOwnershipLostException(string stepName)
    : Exception($"Step '{stepName}' completion lost its version CAS; the job is owned by another execution.")
{
    public string StepName { get; } = stepName;
}
