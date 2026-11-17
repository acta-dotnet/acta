namespace Acta;

internal enum AlertChannelVerdictReason : byte
{
    Deliver = 1,
    MissingChannel = 2,
    DisabledChannel = 3,
    BelowMinSeverity = 4,
    MissingTransport = 5,
}
