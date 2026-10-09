using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Catalog;

/// <summary>
/// Conformance for system-job auto-registration: <c>InitializeAsync</c> registers the
/// <c>sys.recovery</c> system definition, its recurring slot (deduplication_key = the job name), and its
/// schedule into the worker's namespace. The one spec that opts back into
/// <see cref="ActaRuntimeTestBase{ConformanceFixture,TManifest}.RegisterSystemJobs"/>; every other execution
/// spec keeps the slots out so a single drive deterministically claims its own probe.
/// </summary>
[ConformanceSpec(
    "system-jobs.auto-register",
    "Init auto-registers system definitions, slots and schedules",
    Area = "Catalog",
    Contract = "InitializeAsync registers system definitions with a Ready recurring slot keyed on the job name and a default schedule."
)]
public sealed class FrameworkJobRegistrationSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    protected override bool RegisterSystemJobs => true;

    [Fact(
        DisplayName = "Init makes the sys.recovery definition Active with a Ready name-keyed slot, a NextRunAtUtc, and a default schedule"
    )]
    public async Task Initialize_auto_registers_the_maintenance_system_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var def = await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == ns && d.Name == "sys.recovery").SingleOrDefaultAsync(ct);
        Assert.NotNull(def);
        Assert.Equal(DefinitionStatusCode.Active, def!.Status);

        // The recurring slot job carries the job name as its deduplication_key.
        var slot = await Db.From<JobEntity>()
            .Where(j => j.NamespaceId == ns && j.DeduplicationKey == "sys.recovery")
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(slot);
        var slotRow = await ReadJobAsync(slot!.Id, ct);
        Assert.Equal(JobStatusCode.Ready, slotRow.Status);
        Assert.NotNull(slotRow.NextRunAtUtc);

        var schedules = await Db.From<ScheduleEntity>().Where(s => s.DefinitionId == def.Id).ToListAsync(ct);
        Assert.Equal("default", Assert.Single(schedules).Name);
    }

    [Fact(DisplayName = "Init makes the sys.retention definition Active with a Ready name-keyed slot and an hourly default schedule")]
    public async Task Initialize_auto_registers_the_purge_expired_data_system_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var def = await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == ns && d.Name == "sys.retention").SingleOrDefaultAsync(ct);
        Assert.NotNull(def);
        Assert.Equal(DefinitionStatusCode.Active, def!.Status);

        var slot = await Db.From<JobEntity>()
            .Where(j => j.NamespaceId == ns && j.DeduplicationKey == "sys.retention")
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(slot);
        var slotRow = await ReadJobAsync(slot!.Id, ct);
        Assert.Equal(JobStatusCode.Ready, slotRow.Status);
        Assert.NotNull(slotRow.NextRunAtUtc);

        var schedule = Assert.Single(await Db.From<ScheduleEntity>().Where(s => s.DefinitionId == def.Id).ToListAsync(ct));
        Assert.Equal("default", schedule.Name);
        Assert.Equal(Cron.Hourly, schedule.Expression);
    }
}
