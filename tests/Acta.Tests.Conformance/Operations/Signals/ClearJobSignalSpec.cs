using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Signals;

/// <summary>Conformance for the operator clear: a finished job or an idle permanent job forgets one signal, audited, and nothing else is written.</summary>
[ConformanceSpec(
    "clear-job-signal.operator",
    "An operator clear deletes a finished job's signal and records who cleared it",
    Area = "Signals",
    Contract = "A clear deletes a finished or idle job's named signal, so a raise after a restart takes a new value, and refuses a live job."
)]
[Deploys("job-wait-signal-typed", "recurring-ping")]
public sealed class ClearJobSignalSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ClearJobSignalSpec>>
{
    [Fact(
        DisplayName = "A finished job's clear deletes the signal and records the operator, and a raise after a restart takes a new value"
    )]
    public async Task Clear_then_restart_then_raise_takes_a_new_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-typed", JobPayload.None), ct);
        await Jobs.RaiseSignalAsync(enqueued, "review", new ReviewDecision(false, "first"), ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(enqueued, ct: ct)).Action);

        var clear = await Jobs.ClearSignalAsync(enqueued, "review", actor: "Jože Kovač", ct: ct);

        Assert.Equal((ControlAction.Applied, JobStatusCode.Cancelled, (string?)null), (clear.Action, clear.Status, clear.Reason));
        Assert.Empty(await ReadSignalsAsync(enqueued.JobId, ct));
        var evt = await ReadSingleEventAsync(enqueued.JobId, EventCode.JobSignalCleared, ct);
        Assert.Equal("Jože Kovač", evt.Actor);
        Assert.Equal("Signal 'review' cleared.", evt.EventMessage);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, ct: ct)).Action);
        var second = new ReviewDecision(true, "second");
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "review", second, ct)).Action);
        var signal = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(JobPayload.Json(second).Data.ToArray(), signal.Value);
    }

    [Fact(DisplayName = "A live job's clear is Rejected, an absent signal AlreadyInState, an unknown job NotFound, and none writes")]
    public async Task Refusals_write_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var missing = await Jobs.ClearSignalAsync(JobLookup.ByRef(JobRef.New()), "review", ct: ct);
        Assert.Equal((ControlAction.NotFound, (JobStatusCode?)null), (missing.Action, missing.Status));

        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-typed", JobPayload.None), ct);
        await Jobs.RaiseSignalAsync(enqueued, "review", new ReviewDecision(true, "kept"), ct);

        var live = await Jobs.ClearSignalAsync(enqueued, "review", ct: ct);
        Assert.Equal((ControlAction.Rejected, JobStatusCode.Ready, "job-not-finished"), (live.Action, live.Status, live.Reason));
        Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(enqueued, ct: ct)).Action);
        var absent = await Jobs.ClearSignalAsync(enqueued, "other", ct: ct);
        Assert.Equal((ControlAction.AlreadyInState, JobStatusCode.Cancelled), (absent.Action, absent.Status));
        Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobSignalCleared, ct));
    }

    [Fact(DisplayName = "A permanent job between firings forgets a signal, so its next firing waits for it again")]
    public async Task Idle_permanent_job_clears()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);
        var slot = JobLookup.ById(slotId);
        Assert.Equal(JobStatusCode.Idle, (await Jobs.RaiseSignalAsync(slot, "go", JobPayload.None, ct: ct)).Status);

        var clear = await Jobs.ClearSignalAsync(slot, "go", ct: ct);

        Assert.Equal((ControlAction.Applied, JobStatusCode.Idle), (clear.Action, clear.Status));
        Assert.Empty(await ReadSignalsAsync(slotId, ct));
    }

    [Fact(DisplayName = "A malformed or reserved signal name throws before the job is looked up")]
    public async Task Name_is_validated_before_the_lookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var missing = JobLookup.ByRef(JobRef.New());

        await Assert.ThrowsAsync<ArgumentException>(async () => await Jobs.ClearSignalAsync(missing, "Not Kebab", ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Jobs.ClearSignalAsync(missing, "sys.child.1", ct: ct));
    }
}
