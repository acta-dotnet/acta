using Acta.Relational.Commands;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Concurrent producers batch-enqueue into one namespace at the same time, at the size the Anvil
/// enqueue-batch cell uses (1000-row calls, tens of thousands of rows in all), which is the regime where
/// SQL Server picks page locks and set-based plans. Deadlock retry is off for this spec, so a deadlock
/// fails the test instead of being absorbed by a retry. SQLite runs one writer at a time, so it runs a
/// smaller load that proves the same contract.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.concurrent-batches",
    "Concurrent batch producers in one namespace never deadlock each other",
    Area = "Enqueue",
    Contract = "Batch enqueues racing into one namespace all land without a deadlock, unlaned ones as Ready and laned ones in batch order behind their lane head.",
    Arrange = "An add-numbers definition is registered in the test namespace, and deadlock retry is off so a deadlock victim surfaces.",
    Act = "Four producers each enqueue a run of 1000-row batches at the same time, first unlaned, then spread over shared lanes.",
    Assert = "Every call succeeds and every job lands, each lane with one Ready head and the rest Blocked in batch order."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
public abstract class ConcurrentEnqueueBatchSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int Producers = 4;
    private const int BatchSize = 1000;
    private const int Lanes = 4;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        var provider = services.Last(d => d.ServiceType == typeof(SqlProviderOptions));
        services.Remove(provider);
        services.AddSingleton(sp =>
        {
            var options = (SqlProviderOptions)provider.ImplementationFactory!(sp);
            DeadlockRetryOff.Apply(sp, options);
            options.CommandTimeout = TimeSpan.FromMinutes(2);
            return options;
        });
    }

    [Fact(DisplayName = "A batch row deduplicated against a producer that commits while the batch waits returns that producer's job")]
    public async Task A_batch_row_deduplicated_against_a_concurrent_commit_returns_the_winner()
    {
        if (Services.GetRequiredService<ISqlDialect>().Provider == DbProvider.Sqlite)
        {
            Assert.Skip("SQLite has one writer: the batch cannot start until the producer commits.");
        }
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("race-key");
        await using var conn = await Db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var winner = await Jobs.EnqueueAsync(
            tx,
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 1)), DeduplicationKey: key),
            ct
        );

        // The batch meets the uncommitted key and waits on it; the producer then commits.
        var batch = Jobs.EnqueueBatchAsync(
            [
                new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 2)), DeduplicationKey: key),
                new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 3))),
            ],
            ct
        );
        await Task.Delay(500, ct);
        Assert.False(batch.IsCompleted, "the batch did not wait on the uncommitted key");
        await tx.CommitAsync(ct);
        var outcomes = await batch;

        Assert.Equal((JobEnqueueAction.Deduplicated, winner.JobId), (outcomes[0].Action, outcomes[0].JobId));
        Assert.Equal(winner.JobRef, outcomes[0].JobRef);
        Assert.Equal(JobEnqueueAction.Inserted, outcomes[1].Action);
    }

    [Fact(DisplayName = "Four producers racing 1000-row unlaned batches into one namespace all land as Ready")]
    public async Task Concurrent_unlaned_batches_all_land()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        var batchesPerProducer = BatchesPerProducer(unlaned: true);

        var batches = await RaceAsync(batchesPerProducer, lane: null, ct);

        var ids = batches.SelectMany(b => b).ToList();
        Assert.Equal(Producers * batchesPerProducer * BatchSize, ids.Distinct().Count());
        var statuses = await StatusesAsync(ct);
        Assert.All(ids, id => Assert.Equal(JobStatusCode.Ready, statuses[id].Status));
        Assert.All(ids, id => Assert.Null(statuses[id].LaneId));
    }

    [Fact(DisplayName = "Four producers racing 1000-row batches over shared lanes land in batch order behind one head per lane")]
    public async Task Concurrent_laned_batches_keep_lane_order()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        var batchesPerProducer = BatchesPerProducer(unlaned: false);

        var batches = await RaceAsync(batchesPerProducer, lane: i => $"lane-{i % Lanes}", ct);

        var ids = batches.SelectMany(b => b).ToList();
        Assert.Equal(Producers * batchesPerProducer * BatchSize, ids.Distinct().Count());

        // Job-id order equals batch order within each call, so it does within each lane of that call.
        foreach (var batch in batches)
        {
            Assert.True(batch.Zip(batch.Skip(1)).All(pair => pair.First < pair.Second), "a batch's job ids must rise with its ordinals");
        }

        var statuses = await StatusesAsync(ct);
        var byLane = ids.GroupBy(id => statuses[id].LaneId).ToList();
        Assert.Equal(Lanes, byLane.Count);
        foreach (var lane in byLane)
        {
            Assert.NotNull(lane.Key);
            var ordered = lane.Order().ToList();
            Assert.Equal(JobStatusCode.Ready, statuses[ordered[0]].Status);
            Assert.All(ordered.Skip(1), id => Assert.Equal(JobStatusCode.Blocked, statuses[id].Status));
        }
    }

    private int BatchesPerProducer(bool unlaned) =>
        Services.GetRequiredService<ISqlDialect>().Provider == DbProvider.Sqlite ? 2
        : unlaned ? 10
        : 5;

    /// <summary>Runs every producer at once and returns each batch's job ids in ordinal order.</summary>
    private async Task<List<long[]>> RaceAsync(int batchesPerProducer, Func<int, string>? lane, CancellationToken ct)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producers = Enumerable
            .Range(0, Producers)
            .Select(p =>
                Task.Run(
                    async () =>
                    {
                        await start.Task;
                        var ids = new List<long[]>(batchesPerProducer);
                        for (var b = 0; b < batchesPerProducer; b++)
                        {
                            var outcomes = await Jobs.EnqueueBatchAsync(Batch(p, b, lane), ct);
                            Assert.All(outcomes, o => Assert.Equal(JobEnqueueAction.Inserted, o.Action));
                            ids.Add([.. outcomes.Select(o => o.JobId)]);
                        }
                        return ids;
                    },
                    ct
                )
            )
            .ToList();

        start.SetResult();
        var results = await Task.WhenAll(producers);
        return [.. results.SelectMany(r => r)];
    }

    private List<JobEnqueueRequest> Batch(int producer, int batch, Func<int, string>? lane) =>
        [
            .. Enumerable
                .Range(0, BatchSize)
                .Select(i => new JobEnqueueRequest(
                    TestNamespace,
                    "add-numbers",
                    JobPayload.Json(new AddNumbers(producer * 1_000_000 + batch * BatchSize + i, i)),
                    Lane: lane?.Invoke(i)
                )),
        ];

    private async Task<Dictionary<long, JobRuntime>> StatusesAsync(CancellationToken ct)
    {
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var rows = await Db.From<JobRuntime>().Where(r => r.NamespaceId == namespaceId).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id);
    }
}
