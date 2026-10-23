using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Why a step's most recent attempt failed, stored on <c>steps.failure_code</c> beside the operator-readable
/// <c>failure_message</c>.
/// </summary>
[JsonConverter(typeof(StepFailureCodeJsonConverter))]
[CodeKind("step-failure")]
public enum StepFailureCode : byte
{
    [Code("unspecified", "Step failure not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("unhandled-exception", "The step body threw; the attempt counts against the step's retry budget.")]
    UnhandledException = 10,

    [Code(
        "interrupted",
        "An at-most-once step was re-entered on replay before its outcome was recorded (worker died mid-flight); the body is not re-run and the outcome is unknown."
    )]
    Interrupted = 20,
}
