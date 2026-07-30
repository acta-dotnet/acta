namespace Acta.Modules.Execution;

/// <summary>
/// Thrown when <c>complete_step</c> returns <c>StaleVersion</c>: the step slot's version CAS
/// matched no row, so a concurrent execution of the same job advanced the slot and this attempt no
/// longer owns it.
/// </summary>
/// <remarks>
/// A framework-internal fault, not a handler control signal: the step slot version only advances
/// when another execution re-claimed the job (which also bumps <c>runtimes.version</c>), so the losing
/// attempt's <c>complete_execution</c> CAS no-ops against the row the winner now owns. The runner stops
/// this attempt cooperatively, the same as a heartbeat-cancelled stolen lease.
/// </remarks>
internal sealed class StepOwnershipLostException(string stepName)
    : Exception($"Step '{stepName}' completion lost its version CAS; the job is owned by another execution.")
{
    public string StepName { get; } = stepName;
}
