using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(AlertKindCodeJsonConverter))]
[CodeKind("alert-kind")]
public enum AlertKindCode : byte
{
    // Sparse catalog ids leave insertion room; numeric ordering is not behavioral.
    [Code("first-failure", "First failure in a window; emitted by Automatic origin on non-terminal failure.")]
    FirstFailure = 10,

    [Code("threshold-reached", "Threshold-bound emission (e.g., LostClaimAlertThreshold reached).")]
    ThresholdReached = 20,

    [Code("final-failure", "Terminal failure; emitted by Automatic origin on Status to Failed.")]
    FinalFailure = 30,

    [Code("manual", "Hand-raised; ctx.AlertAsync from inside a handler.")]
    Manual = 40,

    [Code("recovery", "Recovery transition; emitted when a previously-failed Job succeeds.")]
    Recovery = 50,
}
