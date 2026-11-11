using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Namespaces;

/// <summary>
/// Conformance for how far a suspension reaches: it closes the namespace to new work, a running job's children and the
/// system jobs' schedules included, and the jobs the namespace already holds still run.
/// </summary>
[ConformanceSpec(
    "suspend-namespace.reach",
    "A suspended namespace takes no new work and still runs what it holds",
    Area = "Admin",
    Contract = "A suspended namespace rejects a running job's child start and holds every schedule, the system jobs' too, while the jobs it already holds still run."
)]
public sealed class SuspendNamespaceReachSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // The system jobs' schedules are a subject here: the harness default leaves them out and parks every cursor.
    protected override bool RunSystemJobs => true;

    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = FakeClock.AtRealMinute();
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A suspended namespace's Ready and retrying jobs are still claimed and run")]
    public async Task Held_jobs_still_run()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 1);
        var ready = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct);
        var retrying = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "failures-audit-probe", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(retrying, ct));
        await SuspendAsync(ct);

        // Claimed by the namespace's own claim, not by id: the suspension must not close the claim.
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(TestNamespace, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(TestNamespace, ct));

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(ready.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(retrying.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A suspended namespace's system schedules hold, and a resume hands them back to the admitter")]
    public async Task System_schedules_hold()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var definitions = (await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == ns).ToListAsync(ct))
            .Where(d => d.DefinitionName.StartsWith("sys.", StringComparison.Ordinal))
            .Select(d => d.Id)
            .ToList();
        var slots = (await Db.From<JobEntity>().Where(j => definitions.Contains(j.DefinitionId)).ToListAsync(ct))
            .Where(j => j.Kind == JobKindCode.Permanent)
            .Select(j => (long?)j.Id)
            .ToList();
        var schedules = (await Db.From<ScheduleEntity>().Where(s => slots.Contains(s.JobId)).ToListAsync(ct)).Select(s => s.Id).ToList();
        var due = (await Db.From<ScheduleStateEntity>().Where(s => schedules.Contains(s.Id)).ToListAsync(ct)).Max(s =>
            s.NextOccurrenceAtUtc!.Value
        );
        Assert.True(slots.Count >= 2, "the alerts and retention slots are deployed");
        await SuspendAsync(ct);
        Clock.AdvanceTo(due);

        // Alert projection and retention stop with the namespace: no system firing is admitted while it is suspended.
        Assert.Equal(0, (await Runtime.AdmitOnceAsync(TestNamespace, ct)).Admitted);
        Assert.All(await StatusesAsync(slots, ct), status => Assert.Equal(JobStatusCode.Idle, status));

        await Operations.Namespaces.ResumeAsync(TestNamespace, ct: ct);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.All(await StatusesAsync(slots, ct), status => Assert.Equal(JobStatusCode.Ready, status));
    }

    [Fact(DisplayName = "A running job's child start into its suspended namespace is rejected and fails the attempt")]
    public async Task Child_start_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        await SuspendAsync(ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        Assert.Equal(0, await Db.From<JobEntity>().Where(j => j.ParentJobId == parent.JobId).CountAsync(ct));
        var failed = await ReadSingleEventAsync(parent.JobId, EventCode.JobExecutionFailed, ct);
        Assert.Contains("suspended namespace", failed.EventMessage, StringComparison.Ordinal);
    }

    private async Task SuspendAsync(CancellationToken ct) =>
        Assert.Equal(ControlAction.Applied, (await Operations.Namespaces.SuspendAsync(TestNamespace, "hold", "op-1", ct)).Action);

    private async Task<List<JobStatusCode>> StatusesAsync(List<long?> jobIds, CancellationToken ct)
    {
        var statuses = new List<JobStatusCode>();
        foreach (var id in jobIds)
        {
            statuses.Add((await ReadJobAsync(id!.Value, ct)).Status);
        }

        return statuses;
    }
}
