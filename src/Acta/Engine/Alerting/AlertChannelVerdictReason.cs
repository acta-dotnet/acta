namespace Acta;

internal enum AlertChannelVerdictReason : byte
{
    Deliver = 1,
    MissingChannel = 2,
    DisabledChannel = 3,
    DeprecatedChannel = 4,
    BelowMinSeverity = 5,
    MissingTransport = 6,
}
