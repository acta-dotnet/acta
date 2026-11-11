namespace Acta;

/// <summary>
/// Thrown when <c>complete_job_step</c> returns <c>StaleVersion</c>: the step's row changed, or went, between this
/// attempt's start of the step and its completion, so the completion wrote nothing. The fence refuses another attempt
/// before the version is read, so the change is this attempt's own: the step's name was started twice at once, or the
/// handler reset the job's state inside the step's body. Not a control signal: the runner fails the attempt as
/// <c>attempt-aborted</c> under the failure budget, with the step named, instead of reporting a step nothing recorded.
/// </summary>
internal sealed class StepOwnershipLostException(string stepName)
    : Exception(
        $"Step '{stepName}' changed under its own attempt before it completed: its name was started twice at once, or the job's state was reset inside its body."
    )
{
    public string StepName { get; } = stepName;
}
