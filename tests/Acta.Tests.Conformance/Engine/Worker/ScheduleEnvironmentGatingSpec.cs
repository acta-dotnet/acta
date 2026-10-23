using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Environment-gated schedule registration: at startup a <c>[JobSchedule]</c> registers only when it is
/// active in this worker's <see cref="JobsOptions.EnvironmentName"/>: a schedule that declares no
/// environments is a wildcard, otherwise its declared set must contain the worker's environment
/// (case-insensitive). Driven through the real <c>CatalogRegistrar</c> reconcile by running the
/// worker as <c>staging</c> against jobs that mix staging-, production-, and unscoped schedules.
/// </summary>
[ConformanceSpec(
    "register-scheduled-jobs.environment-gating",
    "Schedule registration is gated by the worker's environment",
    Area = "Scheduling",
    Contract = "Schedule registration honors each schedule's declared environments, registering only those active in the worker's environment and withholding the rest."
)]
public sealed class ScheduleEnvironmentGatingSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string WorkerEnvironment = "staging";

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o => o.EnvironmentName = WorkerEnvironment);
    }

    [Fact(DisplayName = "A staging worker registers the staging-scoped schedule and withholds the production-scoped one")]
    public async Task Registers_only_the_schedule_active_in_this_environment()
    {
        var ct = TestContext.Current.CancellationToken;

        var slotId = (await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, "env-gated-ping"), ct))?.JobId;
        Assert.NotNull(slotId);

        var rows = await Db.From<ScheduleEntity>().Where(s => s.JobId == slotId!.Value).ToListAsync(ct);
        var row = Assert.Single(rows);
        Assert.Equal("staging-tick", row.ScheduleName);
    }

    [Fact(DisplayName = "A staging worker creates no recurring slot for a job whose only schedule is production-scoped")]
    public async Task Creates_no_slot_when_every_schedule_is_excluded()
    {
        var ct = TestContext.Current.CancellationToken;

        var slotId = (await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, "env-prod-only-ping"), ct))?.JobId;
        Assert.Null(slotId);
    }
}
