using System.Collections.Concurrent;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Step body for the deferred-retry probe: fails on the first invocation, succeeds on the second.
/// Invocation count is tracked per job id so parallel specs do not collide.
/// </summary>
internal static class DeferredRetryStepHandler
{
    public static readonly ConcurrentDictionary<long, int> BodyInvocations = new();

    private static int RecordInvocation(long jobId) => BodyInvocations.AddOrUpdate(jobId, 1, static (_, n) => n + 1);

    public static async Task RunAsync(JobContext ctx, CancellationToken ct)
    {
        await ctx.RunStepAsync(
            "the-step",
            async _ =>
            {
                var n = RecordInvocation(ctx.JobId);
                await Task.CompletedTask;
                if (n == 1)
                {
                    throw new InvalidOperationException("first attempt fails");
                }
            },
            o => o.MaxAttempts(3).BackoffInitialDelay(TimeSpan.FromSeconds(30)),
            ct
        );
    }
}

/// <summary>
/// Hand-written spec-local manifest for the deferred-retry probe.
/// Isolated from <c>TestJobsManifest</c> so sibling step specs are unaffected.
/// </summary>
public sealed class DeferredRetryStepManifest : IJobManifest
{
    private const string ProbeName = "deferred-retry-step-probe";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            new JobDescriptor(
                JobName: ProbeName,
                HandlerType: typeof(DeferredRetryStepHandler),
                MethodName: nameof(DeferredRetryStepHandler.RunAsync),
                InputType: typeof(NoInput),
                OutputType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                OutputPayloadFormat: null,
                InvocationKind: JobInvocationKind.Task,
                RequiresJobContextParameter: true,
                RequiresCancellationToken: true,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 5,
                AuditLevel: JobAuditLevelCode.Audit,
                AlertProfile: JobAlertProfileCode.OnFailure,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await DeferredRetryStepHandler.RunAsync(ctx, ct);
                    return new JobHandlerInvocationResult(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeOutput: null
            ),
        ]);
}

/// <summary>
/// Conformance for the deferred-retry branch of durable steps: a step that fails with nonzero backoff
/// stores <c>next_retry_at_utc</c> in the future, the parent re-arms to Ready at that instant budget-
/// neutrally, a second <c>RunOnceAsync</c> before the instant returns <c>NothingClaimed</c>, and at/
/// after the instant the body is re-invoked and the parent completes Done on success.
/// </summary>
[ConformanceSpec(
    "step.deferred-retry",
    "Nonzero backoff defers the parent to the retry instant and re-invokes the body",
    Area = "Steps",
    Contract = "A step failure with nonzero backoff re-arms the parent Ready at the retry instant budget-neutrally and gates re-invocation until that instant.",
    Arrange = "A deferred-retry step that fails once then succeeds is registered with MaxAttempts 3 and a 30s initial backoff.",
    Act = "The job runs, is re-run before the retry instant, and runs again after the clock advances to it.",
    Assert = "The parent re-arms Ready at the retry instant budget-neutrally, the early run claims nothing, and the re-invoked body completes the job Done."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.StartStepAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteStepAsync))]
public abstract class StepDeferredRetrySpec<TFixture> : ActaRuntimeTestBase<TFixture, DeferredRetryStepManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string ProbeName = "deferred-retry-step-probe";
    private const string StepName = "the-step";

    private static readonly DateTime FakeT0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Registered before UseActa so TryAddSingleton<IActaClock> no-ops; gives us a deterministic clock
    // for any C#-side schedule walker calls (unused here but consistent with the spec-local pattern).
    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = new FakeClock(FakeT0);
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "After a step failure with nonzero backoff the parent is Ready at the retry instant and NothingClaimed before it")]
    public async Task Parent_is_ready_at_retry_instant_and_nothing_claimed_before_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, ProbeName, JobPayload.None), ct);

        // Tick 1: body fails on attempt 1; step schedules a retry in 30s (real DB clock).
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        // Body invoked exactly once.
        Assert.Equal(1, DeferredRetryStepHandler.BodyInvocations[enqueued.JobId]);

        // Step row: Pending at attempt 1, next_retry_at_utc set to a future instant.
        var step1 = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(StepName, step1.Name);
        Assert.Equal(JobStepStateCode.Pending, step1.State);
        Assert.Equal((short)1, step1.AttemptNumber);
        Assert.NotNull(step1.NextRetryAtUtc);
        var retryInstant = step1.NextRetryAtUtc!.Value;

        // Parent: Ready (not Suspended), failure_count untouched (budget-neutral), next_run pinned to retry instant.
        var job1 = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job1.Status);
        Assert.Equal((short)0, job1.FailureCount);
        Assert.Equal(retryInstant, job1.NextRunAtUtc);

        // Tick 2: single-shot claim before the retry instant returns NothingClaimed (parent not yet due).
        var tick2 = await Runtime.RunOnceAsync(TestNamespace, enqueued.JobId, ct);
        Assert.Equal(RunOnceOutcome.NothingClaimed, tick2);

        // Body not re-invoked.
        Assert.Equal(1, DeferredRetryStepHandler.BodyInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "At the retry instant the step body is re-invoked on attempt 2 and the parent completes Done")]
    public async Task At_retry_instant_body_is_reinvoked_and_parent_completes_done()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, ProbeName, JobPayload.None), ct);

        // Tick 1: body fails, retry deferred.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, DeferredRetryStepHandler.BodyInvocations[enqueued.JobId]);

        // Read the retry instant so we can assert exact equality before and after.
        var step1 = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.NotNull(step1.NextRetryAtUtc);
        var retryInstant = step1.NextRetryAtUtc!.Value;

        // Parent is ready at retryInstant; confirm it matches before DB manipulation.
        var job1 = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(retryInstant, job1.NextRunAtUtc);

        // Advance to the retry instant: move both the parent's next_run and the step's next_retry to the past.
        var past = DateTime.UtcNow.AddMinutes(-1);
        await SetJobNextRunAsync(Db, enqueued.JobId, past, ct);
        await SetStepNextRetryAtAsync(Db, enqueued.JobId, StepName, past, ct);

        // Tick 3: at/after retry instant, body re-invoked (attempt 2 succeeds), parent Done.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        // Body invoked twice total.
        Assert.Equal(2, DeferredRetryStepHandler.BodyInvocations[enqueued.JobId]);

        // Step: Succeeded at attempt 2, no pending retry.
        var step3 = Assert.Single(await ReadStepsAsync(enqueued.JobId, ct));
        Assert.Equal(JobStepStateCode.Succeeded, step3.State);
        Assert.Equal((short)2, step3.AttemptNumber);
        Assert.Null(step3.NextRetryAtUtc);

        // Parent: Done, failure_count still untouched.
        var job3 = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Done, job3.Status);
        Assert.Equal((short)0, job3.FailureCount);
    }

    // ---------- helpers ----------

    private async Task<IReadOnlyList<JobStep>> ReadStepsAsync(long jobId, CancellationToken ct)
    {
        return await Db.From<JobStep>().Where(s => s.JobId == jobId).ToListAsync(ct);
    }

    private static Task SetJobNextRunAsync(IDbSession db, long jobId, DateTime nextRun, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_next", nextRun),
            ("@p_id", jobId)
        );

    private static Task SetStepNextRetryAtAsync(IDbSession db, long jobId, string stepName, DateTime nextRetry, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.steps SET next_retry_at_utc = @p_next WHERE job_id = @p_id AND name = @p_name",
            ct,
            ("@p_next", nextRetry),
            ("@p_id", jobId),
            ("@p_name", stepName)
        );
}
