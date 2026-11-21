using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>
/// Where a job's retention comes from: a definition that states none is written with <c>JobsOptions.Retention</c> at
/// deploy, and <c>JobRetention = "never"</c> is the largest retention the column holds, stamped without overflow by
/// every statement that lands a job. The host here sets the option to 30 days, so the default cannot pass for it.
/// </summary>
[ConformanceSpec(
    "register-definitions.retention",
    "Registration writes JobsOptions.Retention, and never is the longest retention",
    Area = "Retention",
    Contract = "A definition that states no JobRetention takes JobsOptions.Retention at deploy, and a never job lands 68 years out on every path and outlives the sweep."
)]
[Deploys("add-numbers", "keep-forever")]
public sealed class RegisterDefinitionsRetentionSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<RegisterDefinitionsRetentionSpec>>
{
    private static readonly TimeSpan Knob = TimeSpan.FromDays(30);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o => o.Retention = Knob);
    }

    // The database clock, which is the one every stamping statement reads.
    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private async Task<int> DefinitionRetentionAsync(int ns, string jobName, CancellationToken ct) =>
        (
            await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == ns && d.DefinitionName == jobName).SingleOrDefaultAsync(ct)
        )!.JobRetentionSeconds;

    /// <summary>Runs <paramref name="land"/> between two database-clock reads and asserts the job's stamp lies <paramref name="retention"/> past them.</summary>
    private async Task AssertLandsAsync(long jobId, TimeSpan retention, JobStatusCode status, Func<Task> land, CancellationToken ct)
    {
        var before = await NowAsync(ct);
        await land();
        var after = await NowAsync(ct);

        var job = await ReadJobAsync(jobId, ct);
        Assert.Equal(status, job.Status);
        Assert.NotNull(job.RetentionUntilUtc);
        Assert.InRange(job.RetentionUntilUtc!.Value, before.Add(retention).AddSeconds(-1), after.Add(retention).AddSeconds(1));
    }

    [Fact(DisplayName = "A definition that states no JobRetention is written with JobsOptions.Retention, and its job lands with it")]
    public async Task A_job_that_states_no_retention_takes_the_option()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        Assert.Equal((int)Knob.TotalSeconds, await DefinitionRetentionAsync(ns, "add-numbers", ct));

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        await AssertLandsAsync(
            enqueued.JobId,
            Knob,
            JobStatusCode.Succeeded,
            async () => Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct)),
            ct
        );
    }

    // Every statement that stamps retention_until_utc. A laned landing is the worker's too, and PostgreSQL lands it through
    // the other of its two completion statements.
    [Theory(DisplayName = "A never job lands 2,147,483,647 seconds out, whichever statement lands it, and the sweep keeps it")]
    [InlineData("worker")]
    [InlineData("worker, laned")]
    [InlineData("cancel")]
    [InlineData("reclaim")]
    [InlineData("retire")]
    public async Task A_never_job_keeps_its_far_date_on_every_landing(string landing)
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        Assert.Equal(int.MaxValue, await DefinitionRetentionAsync(ns, "keep-forever", ct));
        var job = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "keep-forever", JobPayload.None, Lane: landing == "worker, laned" ? Unique("lane") : null),
            ct
        );
        if (landing == "reclaim")
        {
            await ChaosSpecHelpers.StrandExecutingAsync(Services, ns, await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct), job.JobId, ct);
        }

        await AssertLandsAsync(
            job.JobId,
            TimeSpan.FromSeconds(int.MaxValue),
            landing switch
            {
                "worker" or "worker, laned" => JobStatusCode.Succeeded,
                // One attempt, so the reclaim of its lapsed lease fails the job.
                "reclaim" => JobStatusCode.Failed,
                _ => JobStatusCode.Cancelled,
            },
            async () =>
            {
                switch (landing)
                {
                    case "worker" or "worker, laned":
                        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));
                        break;
                    case "cancel":
                        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(job, "kept for good", ct: ct)).Action);
                        break;
                    case "reclaim":
                        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);
                        break;
                    default:
                        var definition = (await Operations.Definitions.GetAsync(TestNamespace, "keep-forever", ct))!;
                        var retire = await Operations.Definitions.RetireAsync(
                            TestNamespace,
                            "keep-forever",
                            definition.Version,
                            "kept",
                            "spec",
                            ct
                        );
                        Assert.Equal(ControlAction.Applied, retire.Action);
                        break;
                }
            },
            ct
        );

        await RetentionTestOps.PurgeAsync(Services, ns, (int)Knob.TotalDays, 1000, 50, ct);
        Assert.NotNull(await Db.From<JobEntity>().Where(j => j.Id == job.JobId).SingleOrDefaultAsync(ct));
    }
}
