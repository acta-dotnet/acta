using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// <c>IActaBuilder.DoNotRetry</c> through a real host: each fact starts its own, with or without a declaration, so the
/// worker's completion and the handler's <c>ctx.EndsJob</c> are read from the one declaration the host built.
/// </summary>
[ConformanceSpec(
    "handler-control.do-not-retry",
    "DoNotRetry types end a job at once, as a built-in non-retryable exception does",
    Area = "Control",
    Contract = "A type declared with DoNotRetry, or one derived from it, fails the job or the firing at once with its budget untouched, and ctx.EndsJob agrees."
)]
[Deploys("do-not-retry-argument", "do-not-retry-argument-null", "do-not-retry-scheduled", "job-sleep-basic")]
public sealed class DoNotRetrySpec : ActaTestBase<ConformanceFixture>
{
    [Fact(DisplayName = "An ArgumentException is retried when the host declares nothing")]
    public async Task An_argument_exception_is_retried_without_a_declaration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(static _ => { }, ct);
        var enqueued = await EnqueueAsync(host, "do-not-retry-argument", ct);

        Assert.Equal(ActaRunStatus.Rearmed, await host.RunOnceAsync(enqueued, ct));

        var job = (await host.Jobs.GetAsync(enqueued, ct))!;
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Equal(1, job.FailureCount);
        Assert.NotNull(job.NextRunAtUtc);
        var failed = Assert.Single(await EventsAsync(host, enqueued.JobId, EventCode.JobExecutionFailed, ct));
        Assert.Equal(EventReason.UnhandledException, EventDetail(failed, "reason"));
        Assert.Empty(await EventsAsync(host, enqueued.JobId, EventCode.JobFailed, ct));
        Assert.Equal(0, await CountVariableAsync(host, enqueued.JobId, "compensated", ct));
    }

    [Fact(DisplayName = "With ArgumentException declared, the handler's ArgumentException lands Failed at once with its budget untouched")]
    public async Task A_declared_type_lands_failed_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(static j => j.DoNotRetry<ArgumentException>(), ct);
        var enqueued = await EnqueueAsync(host, "do-not-retry-argument", ct);

        Assert.Equal(ActaRunStatus.Failed, await host.RunOnceAsync(enqueued, ct));

        var job = (await host.Jobs.GetAsync(enqueued, ct))!;
        Assert.Equal(JobStatusCode.Failed, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.Null(job.NextRunAtUtc);
        var failed = Assert.Single(await EventsAsync(host, enqueued.JobId, EventCode.JobFailed, ct));
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failed, "reason"));
        Assert.Empty(await EventsAsync(host, enqueued.JobId, EventCode.JobExecutionFailed, ct));
    }

    [Fact(DisplayName = "A type derived from a declared one matches it: ArgumentNullException under a declared ArgumentException")]
    public async Task A_derived_type_matches_its_declared_base()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(static j => j.DoNotRetry<ArgumentException>(), ct);
        var enqueued = await EnqueueAsync(host, "do-not-retry-argument-null", ct);

        Assert.Equal(ActaRunStatus.Failed, await host.RunOnceAsync(enqueued, ct));

        var job = (await host.Jobs.GetAsync(enqueued, ct))!;
        Assert.Equal(JobStatusCode.Failed, job.Status);
        Assert.Equal(0, job.FailureCount);
        var failed = Assert.Single(await EventsAsync(host, enqueued.JobId, EventCode.JobFailed, ct));
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failed, "reason"));
    }

    [Fact(DisplayName = "A compensating catch filtered on ctx.EndsJob runs on the first attempt for a declared type")]
    public async Task A_compensating_catch_runs_on_the_first_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(static j => j.DoNotRetry<ArgumentException>(), ct);
        var enqueued = await EnqueueAsync(host, "do-not-retry-argument", ct);

        Assert.Equal(ActaRunStatus.Failed, await host.RunOnceAsync(enqueued, ct));

        var job = (await host.Jobs.GetAsync(enqueued, ct))!;
        Assert.Equal(1, job.ExecutionNumber);
        Assert.Equal(1, await CountVariableAsync(host, enqueued.JobId, "compensated", ct));
    }

    [Fact(DisplayName = "A permanent job whose handler throws a declared type fails that firing and returns to Idle")]
    public async Task A_permanent_job_fails_the_firing_and_returns_to_idle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(static j => j.DoNotRetry<ArgumentException>(), ct);
        var lookup = new ScheduleLookup(TestNamespace, "do-not-retry-scheduled", "default");
        Assert.Equal(ControlAction.Applied, (await host.Operations.Schedules.TriggerAsync(lookup, ct: ct)).Action);
        var slot = (await host.Operations.Schedules.GetAsync(lookup, ct))!.JobRef!.Value;

        Assert.Equal(ActaRunStatus.Failed, await host.RunOnceAsync(slot, ct));

        // Three attempts are allowed and one is made; the job lives on for its next firing.
        var job = (await host.Jobs.GetAsync(slot, ct))!;
        Assert.Equal(JobStatusCode.Idle, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.Equal(1, job.ExecutionNumber);
        var failed = Assert.Single(await EventsAsync(host, job.JobId, EventCode.JobExecutionFailed, ct));
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failed, "reason"));
        Assert.Empty(await EventsAsync(host, job.JobId, EventCode.JobFailed, ct));
    }

    [Fact(DisplayName = "With Exception declared, a handler that sleeps still parks and its job is not failed")]
    public async Task A_sleep_still_parks_under_do_not_retry_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartHostAsync(static j => j.DoNotRetry<Exception>(), ct);
        var enqueued = await EnqueueAsync(host, "job-sleep-basic", ct);

        // The declaration is live in this host: a thrown exception ends its job, so the sleep below parks under it.
        var thrower = await EnqueueAsync(host, "do-not-retry-argument", ct);
        Assert.Equal(ActaRunStatus.Failed, await host.RunOnceAsync(thrower, ct));

        Assert.Equal(ActaRunStatus.Rearmed, await host.RunOnceAsync(enqueued, ct));

        var job = (await host.Jobs.GetAsync(enqueued, ct))!;
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.NotNull(job.NextRunAtUtc);
        Assert.Single(await EventsAsync(host, enqueued.JobId, EventCode.JobSlept, ct));
        Assert.Empty(await EventsAsync(host, enqueued.JobId, EventCode.JobFailed, ct));
        Assert.Empty(await EventsAsync(host, enqueued.JobId, EventCode.JobExecutionFailed, ct));
    }

    private Task<EnqueueJobResponse> EnqueueAsync(IActaTestHost host, string jobName, CancellationToken ct) =>
        host.Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, jobName, JobPayload.None), ct);

    private static async Task<IReadOnlyList<EventEntity>> EventsAsync(
        IActaTestHost host,
        long jobId,
        EventCode code,
        CancellationToken ct
    ) =>
        await host
            .Services.GetRequiredService<IDbSession>()
            .From<EventEntity>()
            .Where(e => e.EntityCode == EntityCode.Job && e.EntityId == jobId && e.EventCode == code)
            .ToListAsync(ct);

    private static async Task<int> CountVariableAsync(IActaTestHost host, long jobId, string name, CancellationToken ct) =>
        await host
            .Services.GetRequiredService<IDbSession>()
            .From<CheckpointEntity>()
            .Where(v => v.JobId == jobId && v.Kind == CheckpointKindCode.Variable && v.CheckpointName == name)
            .CountAsync(ct);

    private Task<IActaTestHost> StartHostAsync(Action<IActaBuilder> declare, CancellationToken ct) =>
        ActaTestHost.StartAsync(
            (j, schema) =>
            {
                Fixture.ApplyProvider(j, schema);
                j.Run<JobsOf<DoNotRetrySpec>>(TestNamespace, ownerTeam: "test", description: GetType().FullName);
                declare(j);
            },
            new ActaTestHostOptions
            {
                Schema = Schema.SchemaName,
                ConfigureServices = static services => services.Configure<JobsOptions>(o => o.RunSystemJobs = false),
            },
            ct
        );
}
