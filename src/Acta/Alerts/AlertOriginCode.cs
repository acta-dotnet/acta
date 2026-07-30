using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(AlertOriginCodeJsonConverter))]
[CodeKind("alert-origin")]
public enum AlertOriginCode : byte
{
    [Code("automatic", "System-emitted from a state-mutating SP (failure / timeout / orphan / deadline).")]
    Automatic = 10,

    [Code("manual", "ctx.AlertAsync from inside a user handler.")]
    Manual = 20,
}
