namespace Acta;

internal readonly record struct AlertChannelVerdict(AlertChannelVerdictKind Kind, AlertChannelVerdictReason Reason)
{
    public static AlertChannelVerdict Decide(DeliverableAlert alert, AlertChannelDeclaration? channel, IAlertTransport? transport)
    {
        if (channel is null)
        {
            return new AlertChannelVerdict(AlertChannelVerdictKind.Failed, AlertChannelVerdictReason.MissingChannel);
        }

        if (!channel.Enabled)
        {
            return new AlertChannelVerdict(AlertChannelVerdictKind.Suppressed, AlertChannelVerdictReason.DisabledChannel);
        }

        if (alert.Severity < channel.MinSeverity)
        {
            return new AlertChannelVerdict(AlertChannelVerdictKind.Suppressed, AlertChannelVerdictReason.BelowMinSeverity);
        }

        return transport is null
            ? new AlertChannelVerdict(AlertChannelVerdictKind.Failed, AlertChannelVerdictReason.MissingTransport)
            : new AlertChannelVerdict(AlertChannelVerdictKind.Send, AlertChannelVerdictReason.Deliver);
    }
}
