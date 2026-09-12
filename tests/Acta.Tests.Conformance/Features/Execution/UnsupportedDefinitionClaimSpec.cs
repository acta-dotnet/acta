using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Execution;

/// <summary>
/// A worker can claim work it cannot run. Claims are selected by namespace and the descriptor index is
/// a process-side dictionary, so a definition dropped from a manifest whose jobs still exist - and
/// every rolling deploy, where one version of the manifest is live beside another - hands some worker
/// a job with no handler behind it. The contract is that the claim is handed back: Ready, lease
/// cleared, retry budget untouched, due again after a short delay. Refusing it by throwing would
/// strand the row instead, because the claim has already stamped the lease and the heartbeat renews
/// every leased row from database state alone, so the lease would never lapse for <c>sys.recovery</c>
/// to reclaim.
/// </summary>
[ConformanceSpec(
    "execution.unsupported-definition-claim",
    "A claim with no handler in this deployment is handed back, not stranded",
    Area = "Execution",
    Contract = "A claimed job this worker carries no descriptor for returns to Ready with the lease cleared and the retry budget untouched.",
    Arrange = "A job is enqueued, then its descriptor is dropped from the live worker index the way a deployment that removed the handler would.",
    Act = "The worker claims the job and runs one tick.",
    Assert = "The job is Ready with no lease, failure_count unchanged, next_run_at_utc pushed by the re-arm delay, and no renewed lease at the next heartbeat."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class UnsupportedDefinitionClaimSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string AuditedJob = "add-numbers";

    // Declared at AuditLevel.Failures, which is the level a bounce must write nothing at: nothing failed.
    private const string FailuresAuditedJob = "failures-audit-probe";

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(
        DisplayName = "A claimed job with no handler in this deployment returns to Ready with the lease cleared, failure_count untouched, and next_run_at_utc pushed by the re-arm delay"
    )]
    public async Task An_unsupported_claim_is_returned_to_ready_budget_neutral()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAndForgetDescriptorAsync(AuditedJob, ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);

        // Budget-neutral: the bounce charges no failure and consumes no attempt beyond the one the
        // claim itself took, so a definition that stays missing cannot burn a job's retries.
        Assert.Equal((short)0, job.FailureCount);
        Assert.Equal(1, job.ExecutionNumber);

        // Held back rather than immediately re-claimable, and measured against the routine's own clock
        // stamp so no host-clock comparison is involved.
        Assert.NotNull(job.NextRunAtUtc);
        Assert.True(
            job.NextRunAtUtc >= job.ModifiedAtUtc.AddSeconds(RearmDelaySeconds),
            "the re-arm pushed next_run_at_utc by the safety-poll interval"
        );

        // Nothing was registered in flight: the claim never became an attempt, so there is no
        // cancellation source, no scope, and nothing for the watchdog to measure.
        Assert.Equal(0, Runtime.InFlightCount);
    }

    [Fact(DisplayName = "The worker heartbeat renews no lease for a job it handed back")]
    public async Task The_heartbeat_renews_nothing_for_a_handed_back_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAndForgetDescriptorAsync(AuditedJob, ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        // extend_worker_leases selects on (leased_by_worker_id, status_code IN (40, 50)) and nothing
        // else, so the only way a job the worker cannot run stops being renewed forever is for the
        // worker to have released it. This is the assertion the defect failed.
        var workerId = await WorkerIdAsync(ct);
        var renewed = await Services
            .GetRequiredService<IWorkerStore>()
            .ExtendWorkerLeasesAsync(workerId, leaseTtlSeconds: 600, draining: false, ct);
        Assert.DoesNotContain(enqueued.JobId, renewed);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Null(job.LeaseExpiresAtUtc);
    }

    [Fact(DisplayName = "Under Audit the bounce writes one execution-finished row naming the definition; under Failures it writes none")]
    public async Task The_bounce_is_recorded_under_audit_and_silent_under_failures()
    {
        var ct = TestContext.Current.CancellationToken;
        var audited = await EnqueueAndForgetDescriptorAsync(AuditedJob, ct);
        var quiet = await EnqueueAndForgetDescriptorAsync(FailuresAuditedJob, ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(audited, ct));
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(quiet, ct));

        var finished = await ReadSingleEventAsync(audited.JobId, EventCode.JobExecutionFinished, ct);
        Assert.Equal(ExecutionStatusCode.Rescheduled, finished.ExecutionStatus);
        Assert.Equal(JobStatusCode.Ready, finished.ToStatus);

        // Unclassified is the reason a worker declining a claim carries, and the message is where the
        // operator-readable story lives: it names the definition whose handler is missing.
        Assert.Equal(JobEventReasonCode.Unclassified, finished.ReasonCode);
        Assert.NotNull(finished.ReasonMessage);
        Assert.Contains(
            $"definition_id={(await ReadJobAsync(audited.JobId, ct)).DefinitionId}",
            finished.ReasonMessage!,
            StringComparison.Ordinal
        );

        // Failures records every failed attempt, and a bounce is not one.
        Assert.Equal(0, await CountEventsAsync(quiet.JobId, EventCode.JobExecutionFinished, ct));
    }

    [Fact(DisplayName = "A handed-back job runs to completion on the next tick once a descriptor for it is back")]
    public async Task A_handed_back_job_runs_once_a_descriptor_is_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await EnqueueAndForgetDescriptorAsync(AuditedJob, ct);
        var definitionId = DefinitionId(AuditedJob);
        var descriptor = Forgotten(definitionId);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        // What the bounce is for: the row is claimable again, and a process that carries the handler
        // runs it with no operator intervention and no retry spent. The re-arm delay is short enough
        // that the by-id drive helper's claim retries outlast it.
        Runtime.Descriptors[definitionId] = descriptor;
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, job.Status);
        Assert.Equal((short)0, job.FailureCount);
    }

    /// <summary>
    /// Whole seconds of re-arm delay the executor applies, read from the same option it reads so the
    /// pin follows a configured interval instead of a copied constant.
    /// </summary>
    private int RearmDelaySeconds =>
        (int)Math.Ceiling(Services.GetRequiredService<IOptions<JobsOptions>>().Value.SafetyPollInterval.TotalSeconds);

    private int DefinitionId(string jobName) =>
        Runtime.TryGetDefinitionId(TestNamespace, jobName, out var id)
            ? id
            : throw new InvalidOperationException($"'{jobName}' is not registered in the test namespace.");

    private JobDescriptor Forgotten(int definitionId) =>
        _forgotten.TryGetValue(definitionId, out var descriptor)
            ? descriptor
            : throw new InvalidOperationException($"definition {definitionId} was never dropped from the index.");

    private readonly Dictionary<int, JobDescriptor> _forgotten = [];

    /// <summary>
    /// Enqueues one job through the public API, then drops its descriptor from the live worker index
    /// the way a deployment that removed the handler would. The catalog row stays: a definition is
    /// retired by registration, not by one worker's manifest, and the jobs outlive both.
    /// </summary>
    private async Task<JobEnqueueOutcome> EnqueueAndForgetDescriptorAsync(string jobName, CancellationToken ct)
    {
        var payload = jobName == AuditedJob ? JobPayload.Json(new AddNumbers(2, 3)) : JobPayload.None;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, jobName, payload), ct);

        var definitionId = DefinitionId(jobName);
        Assert.True(Runtime.Descriptors.TryRemove(definitionId, out var removed));
        _forgotten[definitionId] = removed!;
        return enqueued;
    }

    private async Task<int> WorkerIdAsync(CancellationToken ct)
    {
        var ns = NamespaceId;
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return worker!.Id;
    }
}
