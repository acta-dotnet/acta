using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Signals;

/// <summary>Conformance for the operator raise: one Set slot per name holding the latest payload, an audited raise, and no write on a missing or finished job.</summary>
[ConformanceSpec(
    "raise-signal.operator",
    "An operator raise sets the named slot and records who raised it",
    Area = "Signals",
    Contract = "Raise upserts the job's named slot to Set with the latest payload and records the operator, while a missing job is NotFound and a finished one Rejected."
)]
public sealed class RaiseSignalSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    private async Task<CheckpointEntity> ReadSlotAsync(long jobId, string name, CancellationToken ct) =>
        Assert.Single(
            await Db.From<CheckpointEntity>()
                .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Signal && c.Name == name)
                .ToListAsync(ct)
        );

    [Fact(DisplayName = "A raise stores the payload on a Set slot and records the operator on the event")]
    public async Task Raise_sets_the_slot_and_audits_the_operator()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-typed", JobPayload.None), ct);
        var payload = JobPayload.Json(new ReviewDecision(true, "ship it"));

        var raise = await Jobs.RaiseSignalAsync(enqueued, "review", payload, actor: "op-1", ct: ct);

        Assert.Equal(ControlAction.Applied, raise.Action);
        Assert.Equal(JobStatusCode.Ready, raise.Status);
        Assert.Equal(enqueued.JobId, raise.JobId);
        Assert.Null(raise.Version);

        var slot = await ReadSlotAsync(enqueued.JobId, "review", ct);
        Assert.Equal(CheckpointStatusCode.Set, slot.Status);
        Assert.Equal(payload.Format.Id, slot.ValueFormatId);
        Assert.Equal(payload.Data.ToArray(), slot.Value);

        var evt = await ReadSingleEventAsync(enqueued.JobId, EventCode.JobSignalRaised, ct);
        Assert.Equal(ActorCode.Operator, evt.ActorCode);
        Assert.Equal("op-1", evt.Actor);
        Assert.Equal(EventReasonCode.JobSignalReleased, evt.ReasonCode);
        Assert.Equal(JobStatusCode.Ready, evt.FromStatus);
        Assert.Equal(JobStatusCode.Ready, evt.ToStatus);
    }

    [Fact(DisplayName = "A repeat raise keeps one slot, overwrites its payload, and bumps its version")]
    public async Task Repeat_raise_overwrites_one_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-typed", JobPayload.None), ct);
        await Jobs.RaiseSignalAsync(enqueued, "review", new ReviewDecision(false, "first"), ct: ct);
        var first = await ReadSlotAsync(enqueued.JobId, "review", ct);

        var second = JobPayload.Json(new ReviewDecision(true, "second"));
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "review", second, ct: ct)).Action);

        var slot = await ReadSlotAsync(enqueued.JobId, "review", ct);
        Assert.Equal(first.Version + 1, slot.Version);
        Assert.Equal(second.Data.ToArray(), slot.Value);
    }

    [Fact(DisplayName = "An unknown job is NotFound and a finished job is Rejected, and neither gains a slot or an event")]
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
        Assert.Empty(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobSignalRaised, ct));
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
