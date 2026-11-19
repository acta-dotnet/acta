using Xunit;

namespace Acta.Tests.Context;

public class SignalWaitOutcomeTests
{
    [Fact]
    public void A_raise_that_carried_a_value_answers_it_from_every_member()
    {
        var outcome = SignalWaitOutcome<string>.Signalled("approval", hasValue: true, "yes");

        Assert.True(outcome.Received);
        Assert.True(outcome.HasValue);
        Assert.Equal("yes", outcome.Value);
        Assert.True(outcome.TryGetValue(out var value));
        Assert.Equal("yes", value);
        Assert.Equal("yes", outcome.ValueOrThrow());
        outcome.ThrowIfTimedOut();
    }

    [Fact]
    public void A_raised_default_is_told_from_a_raise_without_a_value()
    {
        // A raised zero is a value; a raise without one is not, though both read as 0.
        var raisedZero = SignalWaitOutcome<int>.Signalled("count", hasValue: true, 0);
        var presenceOnly = SignalWaitOutcome<int>.Signalled("count", hasValue: false, 0);

        Assert.True(raisedZero.HasValue);
        Assert.True(raisedZero.TryGetValue(out var zero));
        Assert.Equal(0, zero);
        Assert.False(presenceOnly.HasValue);
        Assert.False(presenceOnly.TryGetValue(out _));
        Assert.Equal(0, presenceOnly.Value);
    }

    [Fact]
    public void ValueOrThrow_answers_default_for_a_raise_without_a_value()
    {
        // An operator's raise without a value is the default on the HTTP API, the dashboard, and the CLI, so reading it
        // never throws.
        var outcome = SignalWaitOutcome<string>.Signalled("approval", hasValue: false, null);

        Assert.True(outcome.Received);
        Assert.False(outcome.HasValue);
        Assert.False(outcome.TryGetValue(out _));
        Assert.Null(outcome.ValueOrThrow());
        outcome.ThrowIfTimedOut();
    }

    [Fact]
    public void A_raise_that_decodes_to_null_has_no_value()
    {
        // A custom format can decode a raised payload to null, which holds no value.
        var outcome = SignalWaitOutcome<string>.Signalled("approval", hasValue: true, null);

        Assert.False(outcome.HasValue);
        Assert.False(outcome.TryGetValue(out _));
    }

    [Fact]
    public void A_timed_out_wait_has_no_value_and_both_throwing_members_throw_the_timeout()
    {
        var outcome = SignalWaitOutcome<int>.Expired("approval");

        Assert.True(outcome.TimedOut);
        Assert.False(outcome.HasValue);
        Assert.False(outcome.TryGetValue(out _));
        Assert.Equal("approval", Assert.Throws<SignalWaitTimeoutException>(() => outcome.ValueOrThrow()).SignalName);
        Assert.Equal("approval", Assert.Throws<SignalWaitTimeoutException>(outcome.ThrowIfTimedOut).SignalName);
    }
}
