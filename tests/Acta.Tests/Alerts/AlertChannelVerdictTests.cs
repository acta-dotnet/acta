using Xunit;

namespace Acta.Tests.Alerts;

public sealed class AlertChannelVerdictTests
{
    [Fact]
    public void Missing_channel_fails()
    {
        var decision = AlertChannelVerdict.Decide(Alert(), channel: null, transport: null);

        Assert.Equal(AlertChannelVerdictKind.Failed, decision.Kind);
        Assert.Equal(AlertChannelVerdictReason.MissingChannel, decision.Reason);
    }

    [Fact]
    public void Disabled_channel_suppresses()
    {
        var decision = AlertChannelVerdict.Decide(Alert(), Channel(enabled: false), transport: null);

        Assert.Equal(AlertChannelVerdictKind.Suppressed, decision.Kind);
        Assert.Equal(AlertChannelVerdictReason.DisabledChannel, decision.Reason);
    }

    [Fact]
    public void Below_min_severity_suppresses()
    {
        var decision = AlertChannelVerdict.Decide(
            Alert(AlertSeverityCode.Info),
            Channel(minSeverity: AlertSeverityCode.Error),
            transport: null
        );

        Assert.Equal(AlertChannelVerdictKind.Suppressed, decision.Kind);
        Assert.Equal(AlertChannelVerdictReason.BelowMinSeverity, decision.Reason);
    }

    [Fact]
    public void Missing_transport_fails()
    {
        var decision = AlertChannelVerdict.Decide(Alert(), Channel(transportKind: "missing-kind"), transport: null);

        Assert.Equal(AlertChannelVerdictKind.Failed, decision.Kind);
        Assert.Equal(AlertChannelVerdictReason.MissingTransport, decision.Reason);
    }

    [Fact]
    public void Active_channel_with_transport_sends()
    {
        var decision = AlertChannelVerdict.Decide(Alert(), Channel(), new StubTransport());

        Assert.Equal(AlertChannelVerdictKind.Send, decision.Kind);
        Assert.Equal(AlertChannelVerdictReason.Deliver, decision.Reason);
    }

    private static DeliverableAlert Alert(AlertSeverityCode severity = AlertSeverityCode.Error) =>
        new(
            AlertId: 1,
            Severity: severity,
            Kind: AlertKindCode.FinalFailure,
            Title: "t",
            Message: "m",
            RunbookUrl: null,
            OccurrenceCount: 1,
            CreatedAtUtc: DateTime.UnixEpoch,
            RetryCount: 0,
            ChannelName: "ops",
            AlertRef: new Guid("019826f0-0000-7000-8000-000000000001"),
            JobRef: null,
            Version: 0
        );

    private static AlertChannelDeclaration Channel(
        bool enabled = true,
        AlertSeverityCode minSeverity = AlertSeverityCode.Info,
        string transportKind = AlertTransportKinds.Log
    ) => new("ops", transportKind, Endpoint: "ops", enabled, minSeverity);

    private sealed class StubTransport : IAlertTransport
    {
        public string TransportKind => AlertTransportKinds.Log;

        public Task<AlertSendStatus> SendAsync(AlertNotification notification, AlertTarget target, CancellationToken ct) =>
            Task.FromResult(AlertSendStatus.Delivered);
    }
}
