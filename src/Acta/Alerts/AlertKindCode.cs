using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(AlertKindCodeJsonConverter))]
[CodeKind("alert-kind", Extensible = true)]
public enum AlertKindCode : byte
{
    /// <summary>
    /// The persisted id is not one this build knows: the row was written by a newer Acta that added an
    /// alert kind. Never written by Acta; only produced when reading forward.
    /// </summary>
    [Code("unspecified", "Alert kind not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

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
