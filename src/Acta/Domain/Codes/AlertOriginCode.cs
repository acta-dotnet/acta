using System.Text.Json.Serialization;

namespace Acta;

/// <summary>How an alert came to be raised.</summary>
[JsonConverter(typeof(AlertOriginCodeJsonConverter))]
[CodeKind("alert-origin")]
public enum AlertOriginCode : byte
{
    [Code("unspecified", "Alert origin not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("automatic", "Raised by the engine on a failure, a timeout, a lost worker, or a deadline.")]
    Automatic = 10,

    [Code("manual", "Raised by a job's handler.")]
    Manual = 20,
}
