using Acta.Tests.AspNetCore.Fakes;
using Xunit;

namespace Acta.Tests.Jobs;

/// <summary>
/// The positional-<c>ct</c> overload of <c>IJobs.RaiseSignalAsync</c>: a token in the fourth slot resolves to it and
/// raises as the named <c>ct: ct</c> form does, with no actor.
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
}
