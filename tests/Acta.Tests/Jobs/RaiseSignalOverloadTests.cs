using Acta.Tests.AspNetCore.Fakes;
using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// The short overloads of <c>IJobs.RaiseSignalAsync</c>: a token in the fourth slot raises as the named <c>ct: ct</c>
/// form does, with no actor, and a call with no value raises a signal that carries none.
/// </summary>
public sealed class RaiseSignalOverloadTests
{
    [Fact]
    public async Task A_positional_token_raises_with_no_actor_and_passes_the_token_on()
    {
        var fake = new FakeJobs();
        var raise = fake.RaiseSignal;
        var seen = new List<CancellationToken>();
        fake.RaiseSignal = (job, name, value, actor, token) =>
        {
            seen.Add(token);
            return raise(job, name, value, actor, token);
        };
        IJobs jobs = fake;
        using var cancellation = new CancellationTokenSource();

        await jobs.RaiseSignalAsync(FakeJobs.FoundJobRef, "approval", JobPayload.None, cancellation.Token);

        var call = Assert.Single(fake.SignalCalls);
        Assert.Equal("approval", call.Name);
        Assert.Null(call.Actor);
        Assert.Equal(cancellation.Token, Assert.Single(seen));
    }

    [Fact]
    public async Task A_raise_with_no_value_carries_none_and_takes_a_token_as_a_token()
    {
        var fake = new FakeJobs();
        var raise = fake.RaiseSignal;
        var seen = new List<(JobPayload Value, CancellationToken Token)>();
        fake.RaiseSignal = (job, name, value, actor, token) =>
        {
            seen.Add((value, token));
            return raise(job, name, value, actor, token);
        };
        IJobs jobs = fake;
        using var cancellation = new CancellationTokenSource();

        await jobs.RaiseSignalAsync(FakeJobs.FoundJobRef, "approval", TestContext.Current.CancellationToken);
        await jobs.RaiseSignalAsync(FakeJobs.FoundJobRef, "approval", cancellation.Token);

        Assert.All(fake.SignalCalls, call => Assert.Null(call.Actor));
        Assert.All(seen, call => Assert.True(call.Value.IsNone));
        Assert.Equal([TestContext.Current.CancellationToken, cancellation.Token], seen.Select(call => call.Token));
    }
}
