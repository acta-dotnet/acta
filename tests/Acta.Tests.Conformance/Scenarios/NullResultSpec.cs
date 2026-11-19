using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for "null stores nothing" in a job's result: a handler that returns null, or a value whose JSON is null,
/// succeeds with no result, and every typed wait, call, child call and map over such a job answers a success with no
/// value, which <c>ValueOrThrow</c> refuses.
/// </summary>
[ConformanceSpec(
    "results.null-stores-nothing",
    "Null stores nothing, and a typed outcome answers a success with no value",
    Area = "Results",
    Contract = "A handler returning null stores no result, and a typed wait, call, child call or map answers that success with no value."
)]
[Deploys(
    "job-parent-map-maybe",
    "job-parent-name-clash",
    "job-parent-of-null-child",
    "job-parent-of-null-then-value-child",
    "job-parent-requires-null-child",
    "job-signal-value-seen",
    "json-null-result",
    "maybe-double",
    "null-result",
    "null-then-value",
    "void-child"
)]
public sealed class NullResultSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<NullResultSpec>>
{
    [Fact(DisplayName = "A typed wait on a job that returned null answers Succeeded with no value, for a reference and a value type")]
    public async Task A_typed_wait_answers_a_success_with_no_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var label = await Jobs.EnqueueAsync(TestJobsManifest.NullResult(new NullResultInput(1)), ct: ct);
        var count = await Jobs.EnqueueAsync(TestJobsManifest.MaybeDouble(new MaybeDoubleInput(0)), ct: ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(label, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(count, ct));

        AssertNoValue(await label.WaitAsync(SpecWaits.Gate, ct), label.JobRef, "null-result");
        AssertNoValue(await Jobs.WaitAsync<NullResultOutput>(label, SpecWaits.Gate, ct), label.JobRef, "null-result");

        // A handler declared Task<int?> is read as int: a success with no value reads 0, and HasValue tells it from a 0.
        var counted = await count.WaitAsync(SpecWaits.Gate, ct);
        Assert.True(counted.Succeeded);
        Assert.Equal(0, counted.Value);
        Assert.False(counted.HasValue);
        Assert.False(counted.TryGetValue(out _));
        Assert.Throws<JobResultMissingException>(() => counted.ValueOrThrow());
    }

    [Fact(DisplayName = "A typed CallAsync on a job that returned null answers Succeeded with no value")]
    public async Task A_typed_call_answers_a_success_with_no_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("lookup");
        var job = await Jobs.EnqueueAsync(new NullResultInput(1), o => o.DeduplicationKey(key), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));

        // The call's deduplication key lands it on the job that already succeeded with no result.
        var outcome = await Jobs.CallAsync(
            TestJobsManifest.NullResult(new NullResultInput(1)),
            new JobExecutionOptions { DeduplicationKey = key, WaitTimeout = SpecWaits.Gate },
            ct
        );

        Assert.Equal(job.JobId, outcome.JobId);
        AssertNoValue(outcome, job.JobRef, "null-result");
    }

    [Fact(DisplayName = "A handler result whose JSON is null stores nothing, as a null result does")]
    public async Task A_result_whose_json_is_null_stores_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "json-null-result", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Succeeded, snapshot!.Status);
        Assert.Equal(0, snapshot.ResultFormatId);
        Assert.Null(snapshot.ResultByteLength);
        Assert.False((await Jobs.TryGetResultAsync<JsonNullValue>(enqueued, ct)).Found);
        AssertNoValue(await Jobs.WaitAsync<JsonNullValue>(enqueued, SpecWaits.Gate, ct), enqueued.JobRef, "json-null-result");
    }

    [Fact(DisplayName = "A job declared with a nullable value-type result reads as that type made nullable, with a value and without")]
    public async Task A_value_type_result_reads_as_its_nullable_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var none = await Jobs.EnqueueAsync(TestJobsManifest.MaybeDouble(new MaybeDoubleInput(0)), ct: ct);
        var four = await Jobs.EnqueueAsync(TestJobsManifest.MaybeDouble(new MaybeDoubleInput(2)), ct: ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(none, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(four, ct));

        Assert.Equal((false, null), await Jobs.TryGetResultAsync<int?>(none, ct));
        Assert.Equal((true, 4), await Jobs.TryGetResultAsync<int?>(four, ct));
        Assert.Equal(4, await ReadAsNullableIntAsync(nameof(IJobs.GetResultAsync), (JobLookup)four, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReadAsNullableIntAsync(nameof(IJobs.GetResultAsync), (JobLookup)none, ct)
        );

        var waitedNone = (JobOutcome)
            (await ReadAsNullableIntAsync(nameof(IJobs.WaitAsync), (JobLookup)none, (TimeSpan?)SpecWaits.Gate, ct))!;
        var waitedFour = (JobOutcome)
            (await ReadAsNullableIntAsync(nameof(IJobs.WaitAsync), (JobLookup)four, (TimeSpan?)SpecWaits.Gate, ct))!;
        Assert.Equal((true, false, (object?)null), (waitedNone.Succeeded, HasValue(waitedNone), Value(waitedNone)));
        Assert.Equal((true, true, (object?)4), (waitedFour.Succeeded, HasValue(waitedFour), Value(waitedFour)));
    }

    [Fact(DisplayName = "A typed wait on a job of a result-less definition this process does not register answers Succeeded with no value")]
    public async Task A_typed_wait_on_an_unregistered_result_less_job_answers_no_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "void-child", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));

        // A routing that registers no definition reads the job as one this process does not register.
        var unregistered = new JobRequestRouting(
            Services.GetRequiredService<IJobPayloadSerializerRegistry>(),
            Services.GetRequiredService<JobTypeRegistry>(),
            Services.GetRequiredService<JobContractRegistry>(),
            JobDescriptorRegistry.Build([])
        );
        var outcome = await JobRunAndWait.WaitAsync<NullResultOutput>(
            Jobs,
            Services.GetRequiredService<IWorkerWakeup>(),
            unregistered,
            Services.GetRequiredService<GetJobResultHandler>(),
            job,
            SpecWaits.Gate,
            ct
        );

        AssertNoValue(outcome, job.JobRef, "void-child");
    }

    [Fact(DisplayName = "A typed child call on a child that returned null answers Succeeded with no value")]
    public async Task A_typed_child_call_answers_a_success_with_no_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-of-null-child", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await ReadChildrenAsync(parent.JobId, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child.Id, ct));

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(NoValueSeen(child, "null-result"), await Jobs.GetResultAsync<ChildValueSeen>(parent, ct));
    }

    [Fact(DisplayName = "ValueOrThrow on a child's success with no value fails the parent for good, its budget untouched")]
    public async Task ValueOrThrow_on_a_success_with_no_value_fails_the_parent_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-requires-null-child", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await ReadChildrenAsync(parent.JobId, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child.Id, ct));

        // The default budget has attempts left: the outcome is final and a replay reads the same, so no retry runs.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(parent, ct));

        var job = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        Assert.Equal(0, job.FailureCount);
        var failed = await ReadLatestEventAsync(parent.JobId, EventCode.JobFailed, ct);
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failed, "reason"));
        Assert.Contains(
            $"Job {new JobRef(child.JobRef)} ('null-result') succeeded and stored no result",
            failed.EventMessage,
            StringComparison.Ordinal
        );
    }

    [Fact(
        DisplayName = "A child restarted after its success retries the parent, whose retry reads the value the child's next execution stored"
    )]
    public async Task A_child_restarted_after_its_success_retries_the_parent()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-parent-of-null-then-value-child", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await ReadChildrenAsync(parent.JobId, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child.Id, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(new JobRef(child.JobRef), ct: ct)).Action);

        // The latch names a success the child no longer holds, so the parent is retried rather than answered no value.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var retried = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, retried.Status);
        Assert.Equal(1, retried.FailureCount);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child.Id, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(new ChildValueSeen(true, true, 5, null), await Jobs.GetResultAsync<ChildValueSeen>(parent, ct));
    }

    [Fact(DisplayName = "A latch that names no execution answers what the child holds now: its value after a rerun, or no value")]
    public async Task A_latch_naming_no_execution_reads_what_the_child_holds_now()
    {
        // The recovery backstop and the C#-side raises write a latch that names no execution.
        var ct = TestContext.Current.CancellationToken;
        var rerun = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-parent-of-null-then-value-child", JobPayload.None),
            ct
        );
        var empty = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-of-null-child", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(rerun, ct));
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(empty, ct));
        var rerunChild = Assert.Single(await ReadChildrenAsync(rerun.JobId, ct));
        var emptyChild = Assert.Single(await ReadChildrenAsync(empty.JobId, ct));

        // The first child returns null, is restarted, and stores 5 from its second execution.
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(rerunChild.Id, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(new JobRef(rerunChild.JobRef), ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(rerunChild.Id, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(emptyChild.Id, ct));
        await StageLatchNamingNoExecutionAsync(rerun.JobId, rerunChild.Id, ct);
        await StageLatchNamingNoExecutionAsync(empty.JobId, emptyChild.Id, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(rerun, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(empty, ct));
        Assert.Equal(new ChildValueSeen(true, true, 5, null), await Jobs.GetResultAsync<ChildValueSeen>(rerun, ct));
        Assert.Equal(NoValueSeen(emptyChild, "null-result"), await Jobs.GetResultAsync<ChildValueSeen>(empty, ct));
    }

    [Fact(DisplayName = "A success with no value of a child of another definition is refused as a stored result of another type is")]
    public async Task A_success_with_no_value_passes_the_result_type_check()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-name-clash", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await ReadChildrenAsync(parent.JobId, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child.Id, ct));

        // The call under the name finds the result-less child, which never ran as the called job.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(parent, ct));
        var failed = await ReadLatestEventAsync(parent.JobId, EventCode.JobFailed, ct);
        Assert.Contains("void-child' produces 'no result'", failed.EventMessage, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "A typed map answers a child that returned null as a success with no value and keeps its siblings' results")]
    public async Task A_typed_map_answers_a_null_child_without_disturbing_its_siblings()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-map-maybe", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var children = await ReadChildrenAsync(parent.JobId, ct);
        Assert.Equal(3, children.Count);
        foreach (var child in children)
        {
            Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child.Id, ct));
        }

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal("1=Succeeded:2,0=Succeeded:none,3=Succeeded:6", await Jobs.GetResultAsync<string>(parent, ct));
    }

    [Fact(DisplayName = "A bounded typed signal wait tells a raise that carried a value, a default one included, from a raise without one")]
    public async Task A_bounded_signal_wait_tells_a_raised_value_from_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-signal-value-seen", JobPayload.None), ct);
        // Each value raised is 0, a default the outcome must still tell from a raise without one, through both bounds.
        foreach (var name in (string[])["with-value", "until-with-value"])
        {
            Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, name, 0, ct: ct)).Action);
        }
        foreach (var name in (string[])["without-value", "until-without-value"])
        {
            Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, name, JobPayload.None, ct: ct)).Action);
        }

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal("True:0,False:none,True:0,False:none", await Jobs.GetResultAsync<string>(enqueued, ct));
    }

    [Fact(
        DisplayName = "A success read by a latch that names no execution is the child's only while a second read finds it at that execution"
    )]
    public async Task A_success_with_no_value_needs_a_second_read_at_the_same_execution()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(TestJobsManifest.NullThenValue(new NullThenValueInput(5)), ct: ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));
        var seen = await Jobs.GetAsync(job, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(job, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));
        var routing = Services.GetRequiredService<JobRequestRouting>();
        var getJobResult = Services.GetRequiredService<GetJobResultHandler>();

        // The first read sees the first execution's success with no value, which the restart and the second execution
        // replaced: the second read finds the job at another execution, so no success is answered for the one first seen.
        var (stale, staleNow) = await JobRunAndWait.SuccessAsync<NullResultOutput>(
            TypedEnqueueSpec.StaleReads.Wrap(Jobs, seen!, alternate: false),
            routing,
            getJobResult,
            job.JobId,
            job.JobRef,
            executionNumber: null,
            ct
        );
        Assert.Null(stale);
        Assert.Equal((JobStatusCode.Succeeded, 2L), (staleNow!.Status, staleNow.ExecutionNumber));

        var (current, _) = await JobRunAndWait.SuccessAsync<NullResultOutput>(
            Jobs,
            routing,
            getJobResult,
            job.JobId,
            job.JobRef,
            executionNumber: null,
            ct
        );
        Assert.Equal(new NullResultOutput(5), current!.ValueOrThrow());
    }

    private static void AssertNoValue<T>(JobOutcome<T> outcome, JobRef jobRef, string jobName)
        where T : class
    {
        Assert.True(outcome.Succeeded);
        Assert.False(outcome.HasValue);
        Assert.Null(outcome.Value);
        Assert.False(outcome.TryGetValue(out _));
        var missing = Assert.Throws<JobResultMissingException>(() => outcome.ValueOrThrow());
        Assert.Equal(jobRef, missing.JobRef);
        Assert.Contains($"('{jobName}') succeeded and stored no result", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>What a parent reads from a child's success with no value: ValueOrThrow names the child's job.</summary>
    private static ChildValueSeen NoValueSeen(TestJobRow child, string jobName) =>
        new(
            Succeeded: true,
            HasValue: false,
            Value: null,
            Refusal: $"Job {new JobRef(child.JobRef)} ('{jobName}') succeeded and stored no result. Read an optional result with TryGetValue."
        );

    /// <summary>
    /// An <see cref="IJobs"/> read whose result type parameter is notnull, called with <c>int?</c>. The constraint binds the
    /// compiler alone: a caller reads an <c>int</c> result as <c>int?</c> past a nullable warning, which this project
    /// builds as an error, so the call goes through reflection.
    /// </summary>
    private async Task<object?> ReadAsNullableIntAsync(string method, params object?[] arguments)
    {
        var read = typeof(IJobs).GetMethods().Single(m => m.Name == method && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(int?));
        var pending = read.Invoke(Jobs, arguments)!;
        var task = (Task)pending.GetType().GetMethod(nameof(ValueTask.AsTask))!.Invoke(pending, null)!;
        await task;
        return task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task);
    }

    private static bool HasValue(JobOutcome outcome) => (bool)outcome.GetType().GetProperty("HasValue")!.GetValue(outcome)!;

    private static object? Value(JobOutcome outcome) => outcome.GetType().GetProperty("Value")!.GetValue(outcome);

    /// <summary>Rewrites the parent's latch on the child as the backstop writes it: the outcome without an execution number.</summary>
    private Task StageLatchNamingNoExecutionAsync(long parentId, long childId, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.checkpoints SET value = @p_value WHERE job_id = @p_parent AND checkpoint_name = @p_name",
            ct,
            ("@p_value", ChildOutcomeEnvelope.Write(childId, JobStatusCode.Succeeded, executionNumber: null)),
            ("@p_parent", parentId),
            ("@p_name", $"sys.child.{childId}")
        );

    private async Task<IReadOnlyList<TestJobRow>> ReadChildrenAsync(long parentId, CancellationToken ct)
    {
        var children = await Db.From<JobEntity>().Where(j => j.ParentJobId == parentId).ToListAsync(ct);
        var rows = new List<TestJobRow>(children.Count);
        foreach (var child in children)
        {
            rows.Add(await ReadJobAsync(child.Id, ct));
        }

        return rows;
    }
}
