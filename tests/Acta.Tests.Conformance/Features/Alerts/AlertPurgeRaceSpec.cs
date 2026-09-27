using Acta.Relational.Commands;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Alert writers race the deletes of the jobs they name. For every job at once, a raiser repeats an
/// automatic-shaped incident, a resolver closes it, and an operator purges the job, while retention sweeps
/// the namespace's alerts. Every path takes the job row before that job's alert rows
/// (docs/internals/sql-execution-policy.md, "Alert lock order"), so none deadlocks, and a raise that loses
/// to the purge is refused rather than landing an alert on a deleted job. Deadlock retry is off, so a
/// deadlock fails the test instead of being absorbed by a retry.
/// </summary>
[ConformanceSpec(
    "alerts.purge-race",
    "Alert writers race job purges without a deadlock or an orphaned alert",
    Area = "Alerts",
    Contract = "Raising, resolving, and sweeping alerts while their jobs are purged never deadlocks and never leaves an alert on a purged job.",
    Arrange = "Forty terminal jobs each carry two open automatic alerts, and deadlock retry is off so a deadlock victim surfaces.",
    Act = "Per job, a raiser, a resolver, and a purge run at once, beside a retention sweep of the namespace's alerts, over several rounds.",
    Assert = "Every purge applies, every raise either lands or is refused for an unknown job, and no alert names a purged job."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.PurgeJobAsync))]
public abstract class AlertPurgeRaceSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int JobsPerRound = 40;
    private const int Rounds = 3;
    private const int RaisesPerJob = 4;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        var provider = services.Last(d => d.ServiceType == typeof(SqlProviderOptions));
        services.Remove(provider);
        services.AddSingleton(sp =>
        {
            var options = (SqlProviderOptions)provider.ImplementationFactory!(sp);
            options.DeadlockRetryAttempts = 1;
            return options;
        });
    }

    [Fact(DisplayName = "Raising, resolving, and sweeping alerts while their jobs are purged never deadlocks or orphans an alert")]
    public async Task Alert_writers_race_job_purges()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var store = Services.GetRequiredService<IAlertStore>();

        for (var round = 0; round < Rounds; round++)
        {
            var ids = await TerminalJobsWithAlertsAsync(round, ct);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racers = new List<Task>();
            foreach (var id in ids)
            {
                racers.Add(Run(start, () => RaiseRepeatedlyAsync(id, ct), ct));
                racers.Add(Run(start, () => store.ResolveJobAlertsAsync(ns, id, long.MaxValue, ct), ct));
                racers.Add(
                    Run(
                        start,
                        async () => Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(JobLookup.ById(id), ct: ct)).Action),
                        ct
                    )
                );
            }
            racers.Add(Run(start, () => RetentionTestOps.PurgeAsync(Services, ns, 100_000, -1, 100_000_000, 25, 50, ct), ct));

            start.SetResult();
            await Task.WhenAll(racers);

            foreach (var chunk in ids.Chunk(20).Select(c => c.Select(id => (long?)id).ToList()))
            {
                Assert.Empty(await Db.From<JobAlert>().Where(a => chunk.Contains(a.JobId)).ToListAsync(ct));
            }
        }
    }

    private static Task Run(TaskCompletionSource start, Func<Task> action, CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                await start.Task;
                await action();
            },
            ct
        );

    // A raise that loses to the purge meets a missing job and is refused; any other failure fails the fact.
    private async Task RaiseRepeatedlyAsync(long jobId, CancellationToken ct)
    {
        for (var i = 0; i < RaisesPerJob; i++)
        {
            try
            {
                await RaiseAsync(jobId, i % 2 == 0 ? AlertKindCode.FirstFailure : AlertKindCode.FinalFailure, ct);
            }
            catch (ArgumentException)
            {
                return;
            }
        }
    }

    private Task<AlertRaiseOutcome> RaiseAsync(long jobId, AlertKindCode kind, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            AlertOriginCode.Automatic,
            AlertSeverityCode.Warning,
            kind,
            "race",
            "race",
            "default",
            AlertDeliveryStatusCode.Pending,
            $"auto:race:{jobId}:{kind.Code}:none",
            ct
        );

    private async Task<List<long>> TerminalJobsWithAlertsAsync(int round, CancellationToken ct)
    {
        var outcomes = await Jobs.EnqueueBatchAsync(
            [
                .. Enumerable
                    .Range(0, JobsPerRound)
                    .Select(i => new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(round, i)))),
            ],
            ct
        );
        var ids = outcomes.Select(o => o.JobId).Order().ToList();
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status WHERE namespace_id = @p_ns AND job_id >= @p_first AND job_id <= @p_last",
            ct,
            ("@p_status", (byte)JobStatusCode.Failed),
            ("@p_ns", Runtime.RegisteredNamespaceIds[TestNamespace]),
            ("@p_first", ids[0]),
            ("@p_last", ids[^1])
        );
        foreach (var id in ids)
        {
            await RaiseAsync(id, AlertKindCode.FirstFailure, ct);
            await RaiseAsync(id, AlertKindCode.FinalFailure, ct);
        }
        return ids;
    }
}
