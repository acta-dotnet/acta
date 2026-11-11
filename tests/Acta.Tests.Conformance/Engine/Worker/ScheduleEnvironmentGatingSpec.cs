using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Environment-gated schedule registration: a deploy writes a <c>[Schedule]</c> only when it is
/// active in the deploying process's <see cref="JobsOptions.EnvironmentName"/>: a schedule that declares no
/// environments is a wildcard, otherwise its declared set must contain the deploy's environment
/// (case-insensitive). Driven through the real <c>CatalogRegistrar</c> reconcile by deploying
/// as <c>staging</c> jobs that mix staging-, production-, and unscoped schedules.
/// </summary>
[ConformanceSpec(
    "register-scheduled-jobs.environment-gating",
    "Schedule registration is gated by the deploy's environment",
    Area = "Scheduling",
    Contract = "Schedule registration honors each schedule's declared environments, registering only those active in the deploy's environment and withholding the rest."
)]
[Deploys("env-gated-ping", "env-prod-only-ping")]
public sealed class ScheduleEnvironmentGatingSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ScheduleEnvironmentGatingSpec>>
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string WorkerEnvironment = "staging";

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o => o.EnvironmentName = WorkerEnvironment);
    }

    [Fact(DisplayName = "A staging deploy registers the staging-scoped schedule and withholds the production-scoped one")]
    public async Task Registers_only_the_schedule_active_in_this_environment()
    {
        var ct = TestContext.Current.CancellationToken;

        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, "env-gated-ping", ct);

        var rows = await Db.From<ScheduleEntity>().Where(s => s.JobId == slotId).ToListAsync(ct);
        var row = Assert.Single(rows);
        Assert.Equal("staging-tick", row.ScheduleName);
    }

    [Fact(DisplayName = "A staging deploy creates no recurring slot for a job whose only schedule is production-scoped")]
    public async Task Creates_no_slot_when_every_schedule_is_excluded()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await ScheduleTestOps.TrySlotIdAsync(Db, TestNamespace, "env-prod-only-ping", ct));
    }
}
