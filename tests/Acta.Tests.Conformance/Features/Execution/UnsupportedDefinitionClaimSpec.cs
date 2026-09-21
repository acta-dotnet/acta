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
/// <para>The bounce also teaches the worker: the definition is excluded from its later claims, so the
/// cost of an incapable deployment is one bounce per definition rather than one per job per
/// safety-poll interval. The exclusion is per process and reaches the scanning claim only - a caller
/// naming a job id still gets the bounce.</para>
/// </summary>
[ConformanceSpec(
    "execution.unsupported-definition-claim",
    "A claim with no handler is handed back, and its definition is not claimed again",
    Area = "Execution",
    Contract = "A claimed job this worker has no descriptor for returns to Ready budget-neutral, and the worker stops claiming that definition.",
    Arrange = "Jobs are enqueued, then their descriptors are dropped from the live worker index the way a deployment that removed the handler would.",
    Act = "The worker claims and ticks, by job id for the hand-back facts and namespace-wide for the exclusion.",
    Assert = "The job is Ready with no lease and failure_count untouched, nothing renews its lease, and later namespace claims skip it."
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
        // that the by-id drive helper's claim retries outlast it. Restoring the descriptor without
        // clearing the exclusion is not a state production reaches - the deployment that carries the
        // handler is a different process, with an empty exclusion set - so the seam clears it here.
        Runtime.Descriptors[definitionId] = descriptor;
        Assert.True(Runtime.ForgetUnsupportedDefinition(definitionId));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, job.Status);
        Assert.Equal((short)0, job.FailureCount);
    }

    [Fact(
        DisplayName = "After one bounce the worker's namespace claims skip that definition and still take the rest of the namespace's work"
    )]
    public async Task A_bounced_definition_is_not_claimed_again_by_this_worker()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstUnsupported = await EnqueueAndForgetDescriptorAsync(AuditedJob, ct);
        var secondUnsupported = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, AuditedJob, JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var supported = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, FailuresAuditedJob, JobPayload.None), ct);

        // Namespace-level ticks, because the exclusion lives on the scanning claim: the by-id path
        // still hands a named row back.
        var outcomes = await TickUntilNothingClaimedAsync(ct);
        Assert.Contains(RunOnceOutcome.Rearmed, outcomes);
        Assert.Contains(RunOnceOutcome.Completed, outcomes);

        Assert.Contains(DefinitionId(AuditedJob), Runtime.UnsupportedDefinitionIdsSnapshot);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(supported.JobId, ct)).Status);

        // One bounce for the definition, not one per job: the second row was never claimed, which its
        // untouched execution_number is the evidence for. Both are Ready and due, so nothing but the
        // exclusion is holding them back.
        var first = await ReadJobAsync(firstUnsupported.JobId, ct);
        var second = await ReadJobAsync(secondUnsupported.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, first.Status);
        Assert.Equal(JobStatusCode.Ready, second.Status);
        Assert.Equal((short)0, first.FailureCount);
        Assert.Equal((short)0, second.FailureCount);
        Assert.Equal(1, first.ExecutionNumber + second.ExecutionNumber);
    }

    [Fact(
        DisplayName = "Every unsupported definition in a namespace is bounced once and then excluded, whatever order the claims arrive in"
    )]
    public async Task Several_unsupported_definitions_in_one_batch_are_all_released_and_learned()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobs = new List<long>();
        foreach (var jobName in new[] { AuditedJob, FailuresAuditedJob })
        {
            jobs.Add((await EnqueueAndForgetDescriptorAsync(jobName, ct)).JobId);
            var payload = jobName == AuditedJob ? JobPayload.Json(new AddNumbers(2, 3)) : JobPayload.None;
            jobs.Add((await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, jobName, payload), ct)).JobId);
            jobs.Add((await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, jobName, payload), ct)).JobId);
        }

        await TickUntilNothingClaimedAsync(ct);

        Assert.Contains(DefinitionId(AuditedJob), Runtime.UnsupportedDefinitionIdsSnapshot);
        Assert.Contains(DefinitionId(FailuresAuditedJob), Runtime.UnsupportedDefinitionIdsSnapshot);

        var rows = new List<TestJobRow>();
        foreach (var jobId in jobs)
        {
            rows.Add(await ReadJobAsync(jobId, ct));
        }
        Assert.All(
            rows,
            row =>
            {
                Assert.Equal(JobStatusCode.Ready, row.Status);
                Assert.Equal((short)0, row.FailureCount);
                Assert.Null(row.LeasedByWorkerId);
            }
        );

        // Two definitions, two bounces, each claimed exactly once: the claim that taught the worker
        // about the second definition is the only one the first definition's exclusion let through,
        // and no row was claimed again after its bounce.
        Assert.Equal(2, rows.Count(row => row.ExecutionNumber == 1));
        Assert.DoesNotContain(rows, row => row.ExecutionNumber > 1);
    }

    /// <summary>
    /// Drives namespace-level ticks (the claim path the exclusion filters) until one claims nothing,
    /// and returns what each tick answered. Bounded so a claim that never settles fails the test
    /// instead of hanging it.
    /// </summary>
    private async Task<IReadOnlyList<RunOnceOutcome>> TickUntilNothingClaimedAsync(CancellationToken ct)
    {
        var outcomes = new List<RunOnceOutcome>();
        for (var tick = 0; tick < 32; tick++)
        {
            var outcome = await Runtime.RunOnceAsync(TestNamespace, ct);
            if (outcome == RunOnceOutcome.NothingClaimed)
            {
                return outcomes;
            }
            outcomes.Add(outcome);
        }

        Assert.Fail($"the namespace still claimed work after 32 ticks: {string.Join(", ", outcomes)}.");
        return outcomes;
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
