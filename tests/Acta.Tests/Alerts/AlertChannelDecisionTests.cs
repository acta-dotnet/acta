using Acta.Modules.Alerting;
using Acta.Modules.Alerting.Api;
using Xunit;

namespace Acta.Tests.Alerts;

public sealed class AlertChannelDecisionTests
{
    [Fact]
    public void Missing_channel_fails()
    {
        var decision = AlertChannelDecision.Decide(Alert(), channel: null, transport: null);

        Assert.Equal(AlertChannelDecisionKind.Failed, decision.Kind);
        Assert.Equal(AlertChannelDecisionReason.MissingChannel, decision.Reason);
    }

    [Theory]
    [InlineData(AlertChannelStatusCode.Disabled, (byte)AlertChannelDecisionReason.DisabledChannel)]
    [InlineData(AlertChannelStatusCode.Deprecated, (byte)AlertChannelDecisionReason.DeprecatedChannel)]
    public void Inactive_channel_suppresses(AlertChannelStatusCode status, byte reason)
    {
        var decision = AlertChannelDecision.Decide(Alert(), Channel(status: status), transport: null);

        Assert.Equal(AlertChannelDecisionKind.Suppressed, decision.Kind);
        Assert.Equal((AlertChannelDecisionReason)reason, decision.Reason);
    }

    [Fact]
    public void Below_min_severity_suppresses()
    {
        var decision = AlertChannelDecision.Decide(
            Alert(AlertSeverityCode.Info),
            Channel(minSeverity: AlertSeverityCode.Error),
            transport: null
        );

        Assert.Equal(AlertChannelDecisionKind.Suppressed, decision.Kind);
        Assert.Equal(AlertChannelDecisionReason.BelowMinSeverity, decision.Reason);
    }

    [Fact]
    public void Missing_transport_fails()
    {
        var decision = AlertChannelDecision.Decide(Alert(), Channel(transportKind: "missing-kind"), transport: null);

        Assert.Equal(AlertChannelDecisionKind.Failed, decision.Kind);
        Assert.Equal(AlertChannelDecisionReason.MissingTransport, decision.Reason);
    }

    [Fact]
    public void Active_channel_with_transport_sends()
    {
        var decision = AlertChannelDecision.Decide(Alert(), Channel(), new StubTransport());

        Assert.Equal(AlertChannelDecisionKind.Send, decision.Kind);
        Assert.Equal(AlertChannelDecisionReason.Deliver, decision.Reason);
    }

    private static DeliverableAlert Alert(AlertSeverityCode severity = AlertSeverityCode.Error) =>
        new(
            AlertId: 1,
            JobId: null,
            Severity: severity,
            Kind: AlertKindCode.FinalFailure,
            Title: "t",
            Message: "m",
            RunbookUrl: null,
            OccurrenceCount: 1,
            CreatedAtUtc: DateTime.UnixEpoch,
            RetryCount: 0,
            ChannelName: "ops"
        );

    private static AlertChannelDeclaration Channel(
        AlertChannelStatusCode status = AlertChannelStatusCode.Active,
        AlertSeverityCode minSeverity = AlertSeverityCode.Info,
        string transportKind = AlertTransportKinds.Log
    ) => new("ops", transportKind, Endpoint: "ops", status, minSeverity);

    private sealed class StubTransport : IAlertTransport
    {
        public string TransportKind => AlertTransportKinds.Log;

        public Task<AlertDeliveryOutcome> SendAsync(AlertNotification notification, AlertTarget target, CancellationToken ct) =>
            Task.FromResult(AlertDeliveryOutcome.Delivered);
    }
}
