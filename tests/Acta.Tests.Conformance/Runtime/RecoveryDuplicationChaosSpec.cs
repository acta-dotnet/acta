using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

[ConformanceSpec(
    "chaos.maintenance-duplication",
    "Duplicated maintenance registration still has one slot and one claimant",
    Area = "Chaos",
    Contract = "Repeated runtime initialization for system maintenance jobs is idempotent, and the recurring maintenance slot is claimed by only one worker.",
    Arrange = "Two worker runtimes target the same namespace with system maintenance jobs enabled.",
    Act = "Both runtimes initialize the namespace, then race to claim the due recurring recovery slot.",
    Assert = "One sys.recovery definition, slot job, and schedule exist, and exactly one claimant wins the due slot."
)]
public abstract class RecoveryDuplicationChaosSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // A normal positive lease; the race here is decided by claim ownership.
    private const int LeaseTtlSeconds = 60;

    protected override bool RegisterFrameworkJobs => true;

    [Fact(DisplayName = "Maintenance registration is idempotent (no duplicate slot) and a due tick has exactly one claimant")]
    public async Task Maintenance_job_is_not_duplicated_and_claims_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // --- 1. A second runtime initializes the same namespace and system jobs.
        await using var second = BuildSecondRuntimeProvider();
        var secondRuntime = second.GetServices<WorkerRuntime>().Single();
        await secondRuntime.InitializeAsync(ct);

        // --- 2. Registration is idempotent: one definition, one slot job, one schedule.
        var maintenanceDefinition = Assert.Single(
            await Db.From<JobDefinition>().Where(d => d.NamespaceId == ns && d.Name == "sys.recovery").ToListAsync(ct)
        );
        var maintenanceJob = Assert.Single(
            await Db.From<Job>().Where(j => j.NamespaceId == ns && j.DefinitionId == maintenanceDefinition.Id).ToListAsync(ct)
        );
        var schedule = Assert.Single(
            await Db.From<JobSchedule>()
                .Where(s => s.NamespaceId == ns && s.JobId == maintenanceJob.Id && s.Name == "default")
                .ToListAsync(ct)
        );
        Assert.Equal(maintenanceDefinition.Id, schedule.DefinitionId);

        // --- 3. Two workers race to claim the one due slot; exactly one wins.
        await ChaosSpecHelpers.SetReadyAsync(Db, maintenanceJob.Id, ct);
        var workers = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).ToListAsync(ct);
        Assert.Equal(2, workers.Count);

        var dialect = Services.GetRequiredService<ISqlDialect>();
        var first = Services
            .GetRequiredService<IExecutionStore>()
            .ClaimOneAsync(new ClaimRequest(ns, workers[0].Id, MaxBatch: 1), LeaseTtlSeconds, maintenanceJob.Id, ct);
        var secondClaim = Services
            .GetRequiredService<IExecutionStore>()
            .ClaimOneAsync(new ClaimRequest(ns, workers[1].Id, MaxBatch: 1), LeaseTtlSeconds, maintenanceJob.Id, ct);
        await Task.WhenAll(first, secondClaim);

        var claimed = first.Result.Jobs.Concat(secondClaim.Result.Jobs).ToList();
        Assert.Single(claimed);

        // Read the runtime row directly to assert which worker holds the execution lease.
        var row = await ReadJobAsync(maintenanceJob.Id, ct);
        Assert.Equal(JobStatusCode.Dispatched, row!.Status);
        Assert.Contains(row.LeasedByWorkerId, workers.Select(w => (int?)w.Id));
    }

    private ServiceProvider BuildSecondRuntimeProvider()
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<TestJobs.TestJobsManifest>(TestNamespace, ownerTeam: "test", description: GetType().FullName + ":second");
        });
        services.Configure<JobsOptions>(o => o.RegisterFrameworkJobs = true);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
