using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Signals;

/// <summary>Conformance for the operator raise: one Set checkpoint per name holding its first payload, an audited raise, and no write on a repeat, a missing job or a finished one.</summary>
[ConformanceSpec(
    "raise-job-signal.operator",
    "An operator raise sets the named checkpoint and records who raised it",
    Area = "Signals",
    Contract = "A raise sets the named checkpoint once and records the operator, while a repeat is AlreadyInState or Rejected and a missing job NotFound."
)]
[Deploys("job-wait-signal", "job-wait-signal-typed")]
public sealed class RaiseJobSignalSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<RaiseJobSignalSpec>>
{
    private async Task<CheckpointEntity> ReadCheckpointAsync(long jobId, string name, CancellationToken ct) =>
        Assert.Single(
            await Db.From<CheckpointEntity>()
                .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Signal && c.CheckpointName == name)
                .ToListAsync(ct)
        );

    [Fact(DisplayName = "A raise stores the payload on a Set checkpoint and records the operator and the signal on the event")]
    public async Task Raise_sets_the_checkpoint_and_audits_the_operator()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-typed", JobPayload.None), ct);
        var payload = JobPayload.Json(new ReviewDecision(true, "ship it"));

        var raise = await Jobs.RaiseSignalAsync(enqueued, "review", payload, actor: "Jože Kovač", ct: ct);

        Assert.Equal(ControlAction.Applied, raise.Action);
        Assert.Equal(JobStatusCode.Ready, raise.Status);
        Assert.Equal(enqueued.JobId, raise.JobId);

        var checkpoint = await ReadCheckpointAsync(enqueued.JobId, "review", ct);
        Assert.Equal(CheckpointStatusCode.Set, checkpoint.Status);
        Assert.Equal(payload.Format.Id, checkpoint.ValueFormatId);
        Assert.Equal(payload.Data.ToArray(), checkpoint.Value);

        var evt = await ReadSingleEventAsync(enqueued.JobId, EventCode.JobSignalRaised, ct);
        Assert.Equal("Jože Kovač", evt.Actor);
        Assert.Equal("Signal 'review' raised.", evt.EventMessage);
        Assert.Null(EventDetail(evt, "actor"));
        Assert.Equal(EventReason.SignalReleased, EventDetail(evt, "reason"));
    }

    [Fact(DisplayName = "A repeat raise writes nothing: the same value is AlreadyInState and another value Rejected")]
    public async Task Repeat_raise_keeps_the_first_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-typed", JobPayload.None), ct);
        var decision = new ReviewDecision(false, "first");
        await Jobs.RaiseSignalAsync(enqueued, "review", decision, ct);
        var first = await ReadCheckpointAsync(enqueued.JobId, "review", ct);

        Assert.Equal(ControlAction.AlreadyInState, (await Jobs.RaiseSignalAsync(enqueued, "review", decision, ct)).Action);
        var other = new ReviewDecision(true, "second");
        var differs = await Jobs.RaiseSignalAsync(enqueued, "review", other, ct);
        Assert.Equal((ControlAction.Rejected, "value-differs"), (differs.Action, differs.Reason));

        // A later replay reads what the first raise stored, so a wait that already returned can never read another value.
        var checkpoint = await ReadCheckpointAsync(enqueued.JobId, "review", ct);
        Assert.Equal(first.Version, checkpoint.Version);
        Assert.Equal(first.Value, checkpoint.Value);
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, EventCode.JobSignalRaised, ct));
    }

    [Fact(DisplayName = "An unknown job is NotFound and a finished job is Rejected, and neither gains a checkpoint or an event")]
    public async Task Missing_or_finished_job_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var missing = await Jobs.RaiseSignalAsync(JobLookup.ByRef(JobRef.New()), "go", JobPayload.None, ct: ct);
        Assert.Equal(ControlAction.NotFound, missing.Action);
        Assert.Null(missing.Status);

        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(enqueued, ct: ct)).Action);

        var finished = await Jobs.RaiseSignalAsync(enqueued, "go", JobPayload.None, ct: ct);
        Assert.Equal(ControlAction.Rejected, finished.Action);
        Assert.Equal(JobStatusCode.Cancelled, finished.Status);
        Assert.Equal("job-finished", finished.Reason);
        Assert.Empty(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobSignalRaised, ct));
    }

    [Fact(DisplayName = "A latch raised for a child with no row names it by its id on the parent's timeline")]
    public async Task A_latch_for_a_missing_child_names_it_by_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        var input = new JobControlInput(null, EventReason.SignalReleased, null);

        // long.MaxValue is no job's id, as a purged child's is no longer one.
        await Services
            .GetRequiredService<SignalsOperations>()
            .RaiseJobSignalAsync(
                new RaiseJobSignalCheckpoint(
                    parent.JobId,
                    CheckpointKindCode.ChildLatch,
                    $"sys.child.{long.MaxValue}",
                    0,
                    null,
                    input,
                    long.MaxValue,
                    JobStatusCode.Failed
                ),
                ct
            );

        var raised = await ReadSingleEventAsync(parent.JobId, EventCode.JobSignalRaised, ct);
        Assert.Equal($"Child job {long.MaxValue} failed.", raised.EventMessage);
    }

    [Fact(DisplayName = "A malformed or reserved signal name throws before the job is looked up")]
    public async Task Name_is_validated_before_the_lookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var missing = JobLookup.ByRef(JobRef.New());

        await Assert.ThrowsAsync<ArgumentException>(async () => await Jobs.RaiseSignalAsync(missing, "Not Kebab", JobPayload.None, ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Jobs.RaiseSignalAsync(missing, "sys", JobPayload.None, ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Jobs.RaiseSignalAsync(missing, "sys.child.1", JobPayload.None, ct: ct)
        );
    }
}
