namespace Acta;

/// <summary>
/// The two fixed signal names of the operator-command inbox on a <c>sys.outbox</c> slot. Fixed names
/// bound the inbox: at most two command rows per source ever exist, because the source-to-slot mapping
/// is 1:1 (<see cref="OutboxRelayRegistry"/> keys registrations by namespace) and the applying tick
/// consumes each row in the same pass, so a healthy relay's inbox rests at zero.
/// </summary>
internal static class OutboxSignalNames
{
    public const string Requeue = "outbox.requeue";

    public const string Discard = "outbox.discard";
}
