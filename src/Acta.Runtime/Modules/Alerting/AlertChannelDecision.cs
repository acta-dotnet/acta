namespace Acta.Modules.Alerting;

internal enum AlertChannelDecisionKind : byte
{
    Send = 1,
    Failed = 2,
    Suppressed = 3,
}

internal enum AlertChannelDecisionReason : byte
{
    Deliver = 1,
    MissingChannel = 2,
    DisabledChannel = 3,
    DeprecatedChannel = 4,
    BelowMinSeverity = 5,
    MissingTransport = 6,
}

internal readonly record struct AlertChannelDecision(AlertChannelDecisionKind Kind, AlertChannelDecisionReason Reason)
{
    public static AlertChannelDecision Decide(DeliverableAlert alert, AlertChannelDeclaration? channel, IAlertTransport? transport)
    {
        if (channel is null)
        {
            return new AlertChannelDecision(AlertChannelDecisionKind.Failed, AlertChannelDecisionReason.MissingChannel);
        }

        if (channel.Status == AlertChannelStatusCode.Disabled)
        {
            return new AlertChannelDecision(AlertChannelDecisionKind.Suppressed, AlertChannelDecisionReason.DisabledChannel);
        }

        if (channel.Status == AlertChannelStatusCode.Deprecated)
        {
            return new AlertChannelDecision(AlertChannelDecisionKind.Suppressed, AlertChannelDecisionReason.DeprecatedChannel);
        }

        if (alert.Severity < channel.MinSeverity)
        {
            return new AlertChannelDecision(AlertChannelDecisionKind.Suppressed, AlertChannelDecisionReason.BelowMinSeverity);
        }

        if (transport is null)
        {
            return new AlertChannelDecision(AlertChannelDecisionKind.Failed, AlertChannelDecisionReason.MissingTransport);
        }

        return new AlertChannelDecision(AlertChannelDecisionKind.Send, AlertChannelDecisionReason.Deliver);
    }
}
