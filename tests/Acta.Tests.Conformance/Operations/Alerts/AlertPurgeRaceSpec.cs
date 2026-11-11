using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>
/// Alert writers race the deletes of the jobs they name. For every job at once, a raiser repeats an
/// automatic-shaped incident, a resolver closes it, and an operator purges the job, while retention sweeps
/// the namespace's alerts, or retention itself deletes the jobs. Every path takes the job row before that job's alert rows
/// (docs/sql-internals.md, "Alert lock order"), so none deadlocks, and a raise that loses
/// to the purge is refused rather than landing an alert on a deleted job. Deadlock retry is off, so a
/// deadlock fails the test instead of being absorbed by a retry.
/// </summary>
[ConformanceSpec(
    "alerts.purge-race",
    "Alert writers race job purges without a deadlock or an orphaned alert",
    Area = "Alerts",
    Contract = "Raising, resolving, and sweeping alerts while their jobs are purged never deadlocks and never leaves an alert on a purged job."
)]
// purge-now keeps its jobs no time at all, so a sweep walks the namespace from the cutoff and reaches a job that a
// fact expires by hand.
[Deploys("add-numbers", "purge-now")]
public sealed class AlertPurgeRaceSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<AlertPurgeRaceSpec>>
{
    private const int JobsPerRound = 40;
    private const int Rounds = 3;
    private const int RaisesPerJob = 4;

    // One fact holds an alert row locked until the sweep waits on it, which would stall any spec that scans alerts.
    protected override bool OwnSchema => true;

    [Fact(DisplayName = "Raising, resolving, and sweeping alerts while their jobs are purged never deadlocks or orphans an alert")]
    public async Task Alert_writers_race_job_purges()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var store = Services.GetRequiredService<AlertsOperations>();

        for (var round = 0; round < Rounds; round++)
        {
            var ids = await TerminalJobsWithAlertsAsync(round, ct);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racers = new List<Task>();
            foreach (var id in ids)
            {
                racers.Add(Run(start, () => RaiseRepeatedlyAsync(id, ct), ct));
                racers.Add(Run(start, () => store.ResolveAlertsAsync(ns, id, long.MaxValue, ct), ct));
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
                Assert.Empty(await Db.From<AlertEntity>().Where(a => chunk.Contains(a.JobId)).ToListAsync(ct));
            }
        }
    }

    [Fact(DisplayName = "An alert another job's raise takes over while retention deletes its first job stays with the job that took it")]
    public async Task An_alert_moved_to_another_job_survives_the_sweep_of_its_first()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var request = new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2)));
        var jobs = (await Jobs.EnqueueJobsAsync([request, request], ct)).Jobs;
        var (expired, kept) = (jobs[0], jobs[1]);
        await JobStateStaging.SetStatusAsync(Db, expired.JobId, JobStatusCode.Failed, ct);
        await JobStateStaging.SetStatusAsync(Db, kept.JobId, JobStatusCode.Failed, ct);
        await ExpireAsync(expired.JobId, ct);
        var key = $"race:moved:{expired.JobId}";
        await RaiseAsync(expired.JobId, AlertKindCode.FirstFailure, ct, AlertDeliveryStatusCode.Delivered, key);
        var alert = Assert.Single(await Db.From<AlertEntity>().Where(a => a.DeduplicationKey == key).ToListAsync(ct));

        // A deduplicated raise moves the open alert to the job that raised it, under that job's row alone. This is that
        // move, held uncommitted, so the sweep reads the alert as its expired job's and then waits on the row.
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var move = await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = move;
            cmd.CommandText =
                $"UPDATE {Db.Schema}.alerts SET job_id = {kept.JobId}, job_ref = '{kept.JobRef.Value}', "
                + $"occurrence_count = occurrence_count + 1 WHERE alert_id = {alert.Id}";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct));
        }

        var sweep = SweepJobsAsync(ns, () => Task.FromResult(true), ct);
        await LockWaitProbe.WaitAsync(Db, "purge_expired_data", LockWait.Row, sweep, move, ct);
        await move.CommitAsync(ct);
        await sweep;

        Assert.Null(await Db.From<JobEntity>().Where(j => j.Id == expired.JobId).SingleOrDefaultAsync(ct));
        var moved = Assert.Single(await Db.From<AlertEntity>().Where(a => a.DeduplicationKey == key).ToListAsync(ct));
        Assert.Equal(kept.JobId, moved.JobId);
    }

    private Task ExpireAsync(long jobId, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET retention_until_utc = @p_due WHERE job_id = @p_id",
            ct,
            ("@p_due", DateTime.UtcNow.AddDays(-1)),
            ("@p_id", jobId)
        );

    [Fact(DisplayName = "Retention deleting finished jobs while their alerts are raised and resolved never deadlocks or orphans an alert")]
    public async Task Alert_writers_race_the_retention_job_sweep()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var store = Services.GetRequiredService<AlertsOperations>();
        var ids = await TerminalJobsWithAlertsAsync(round: 0, ct, AlertDeliveryStatusCode.Delivered);

        // Each pair is a job past its retention and one that stays, and the expired one holds an alert on a key they share.
        var pairs = ids.Chunk(2).Select(p => (Expired: p[0], Kept: p[1], Key: $"race:pair:{p[0]}")).ToList();
        foreach (var (expired, _, key) in pairs)
        {
            await RaiseAsync(expired, AlertKindCode.FirstFailure, ct, AlertDeliveryStatusCode.Delivered, key);
            await ExpireAsync(expired, ct);
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers = new List<Task>();
        foreach (var (expired, kept, key) in pairs)
        {
            racers.Add(Run(start, () => RaiseRepeatedlyAsync(expired, ct, AlertDeliveryStatusCode.Delivered), ct));
            racers.Add(Run(start, () => store.ResolveAlertsAsync(ns, expired, long.MaxValue, ct), ct));
            racers.Add(Run(start, () => RaiseSharedAsync(kept, key, ct), ct));
        }
        racers.Add(Run(start, () => SweepJobsAsync(ns, () => Task.FromResult(true), ct), ct));
        racers.Add(Run(start, () => SweepJobsAsync(ns, () => Task.FromResult(true), ct), ct));

        start.SetResult();
        await Task.WhenAll(racers);

        // Every alert left on an expired job is settled, so one more sweep takes them all, and an alert that outlived
        // its job would stay.
        var expiredIds = pairs.Select(p => p.Expired).ToList();
        var gone = expiredIds.Select(id => (long?)id).ToList();
        await SweepJobsAsync(ns, async () => await Db.From<JobEntity>().Where(j => expiredIds.Contains(j.Id)).CountAsync(ct) == 0, ct);
        Assert.Empty(await Db.From<AlertEntity>().Where(a => gone.Contains(a.JobId)).ToListAsync(ct));
        foreach (var (_, kept, key) in pairs)
        {
            var moved = await Db.From<AlertEntity>().Where(a => a.DeduplicationKey == key && a.ResolvedAtUtc == null).ToListAsync(ct);
            Assert.Equal(kept, Assert.Single(moved).JobId);
        }
    }

    // The job that stays is the only raiser of the shared key during the race, so once it holds the open alert the count
    // only climbs: one that starts over means the sweep deleted the alert as if it still belonged to the expired job.
    private async Task RaiseSharedAsync(long jobId, string key, CancellationToken ct)
    {
        var last = 0;
        for (var i = 0; i < RaisesPerJob; i++)
        {
            var count = (await RaiseAsync(jobId, AlertKindCode.FirstFailure, ct, AlertDeliveryStatusCode.Delivered, key)).OccurrenceCount;
            Assert.True(i == 0 || count == last + 1, $"the alert job {jobId} had taken started over at {count} after {last}");
            last = count;
        }
    }

    // Only the job section deletes: the event and alert windows are far from due.
    private Task SweepJobsAsync(int ns, Func<Task<bool>> settled, CancellationToken ct) =>
        RetentionTestOps.PurgeUntilAsync(Services, ns, 100_000, 100_000, 100_000_000, 25, 50, settled, ct);

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
    private async Task RaiseRepeatedlyAsync(
        long jobId,
        CancellationToken ct,
        AlertDeliveryStatusCode delivery = AlertDeliveryStatusCode.Pending
    )
    {
        for (var i = 0; i < RaisesPerJob; i++)
        {
            try
            {
                var kind = i % 2 == 0 ? AlertKindCode.FirstFailure : AlertKindCode.FinalFailure;
                await RaiseAsync(jobId, kind, ct, delivery);
            }
            catch (ArgumentException)
            {
                return;
            }
        }
    }

    private Task<RaiseAlertResponse> RaiseAsync(
        long jobId,
        AlertKindCode kind,
        CancellationToken ct,
        AlertDeliveryStatusCode delivery = AlertDeliveryStatusCode.Pending,
        string? key = null
    ) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            AlertSeverityCode.Warning,
            kind,
            "race",
            "race",
            "default",
            delivery,
            key ?? $"auto:race:{jobId}:{kind.Code}:none",
            ct
        );

    private async Task<List<long>> TerminalJobsWithAlertsAsync(
        int round,
        CancellationToken ct,
        AlertDeliveryStatusCode delivery = AlertDeliveryStatusCode.Pending
    )
    {
        var outcomes = (
            await Jobs.EnqueueJobsAsync(
                [
                    .. Enumerable
                        .Range(0, JobsPerRound)
                        .Select(i => new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(round, i)))),
                ],
                ct
            )
        ).Jobs;
        var ids = outcomes.Select(o => o.JobId).Order().ToList();
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET job_status_code = @p_status WHERE namespace_id = @p_ns AND job_id >= @p_first AND job_id <= @p_last",
            ct,
            ("@p_status", (byte)JobStatusCode.Failed),
            ("@p_ns", Runtime.RegisteredNamespaceIds[TestNamespace]),
            ("@p_first", ids[0]),
            ("@p_last", ids[^1])
        );
        foreach (var id in ids)
        {
            await RaiseAsync(id, AlertKindCode.FirstFailure, ct, delivery);
            await RaiseAsync(id, AlertKindCode.FinalFailure, ct, delivery);
        }
        return ids;
    }
}
