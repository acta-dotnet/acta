using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What condition an alert reports.</summary>
/// <remarks>Sparse ids leave room to insert; numeric order carries no behavior.</remarks>
[JsonConverter(typeof(AlertKindCodeJsonConverter))]
[CodeKind("alert-kind")]
public enum AlertKindCode : byte
{
    /// <summary>
    /// The persisted id is not one this build knows: the row was written by a newer Acta that added an
    /// alert kind. Never written by Acta; only produced when reading forward.
    /// </summary>
    [Code("unspecified", "Alert kind not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("first-failure", "The failure that opened the incident: an attempt failed and the job lives on.")]
    FirstFailure = 10,

    [Code("threshold-reached", "A counted condition reached its configured threshold, such as a job that keeps losing its claim.")]
    ThresholdReached = 20,

    [Code("final-failure", "The job failed for good.")]
    FinalFailure = 30,

    [Code("manual", "Raised by a job's handler.")]
    Manual = 40,
}
