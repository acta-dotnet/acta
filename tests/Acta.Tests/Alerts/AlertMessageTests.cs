using Xunit;

namespace Acta.Tests.Alerts;

/// <summary>An automatic alert's message ends each sentence once, whether or not the failure message ended its own.</summary>
public sealed class AlertMessageTests
{
    [Theory]
    [InlineData(
        AlertKindCode.FinalFailure,
        "Label for order 2003 was not printed.",
        "Terminal failure: Label for order 2003 was not printed."
    )]
    [InlineData(AlertKindCode.FirstFailure, "Carrier refused order 2003.", "Attempt failed: Carrier refused order 2003. Retrying.")]
    [InlineData(
        AlertKindCode.ThresholdReached,
        "Carrier refused order 2003.",
        "Repeated failures since this incident opened: Carrier refused order 2003."
    )]
    [InlineData(AlertKindCode.FirstFailure, "Is the carrier down?", "Attempt failed: Is the carrier down? Retrying.")]
    [InlineData(AlertKindCode.FinalFailure, "boom", "Terminal failure: boom.")]
    public void A_failure_message_is_one_sentence_of_the_alert(AlertKindCode kind, string failureMessage, string expected)
    {
        var failed = new AlertableEvent(
            EventId: 1,
            CreatedAtUtc: DateTime.UnixEpoch,
            JobId: 7,
            DefinitionId: 3,
            JobName: "print-label",
            AlertProfile: AlertProfileCode.OnFailure,
            AlertChannelName: null,
            EventCode: EventCode.JobFailed,
            EventMessage: failureMessage,
            DetailsFormatId: 0,
            Details: null,
            Superseded: false
        );

        Assert.Equal(expected, AlertsJob.Render(failed, kind).Message);
    }
}
