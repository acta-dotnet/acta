using Acta.Relational.Commands;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Bulk flushers group-commit large completion batches with results into one namespace at the same time,
/// each flusher taking every fourth job so concurrent batches share job, runtime, and result pages.
/// Deadlock retry is off for this spec, so a deadlock fails the test instead of being absorbed by a retry.
/// SQLite has no batched completion (Bulk runs as Direct there), so it skips.
/// </summary>
[ConformanceSpec(
    "runtime.concurrent-completion-batches",
    "Concurrent Bulk flushers group-commit large batches without a deadlock",
    Area = "Execution",
    Contract = "Batched completions racing in one namespace all finalize without a deadlock, each job Succeeded with its result stored.",
    Arrange = "Twenty thousand jobs are staged Executing under one worker, and deadlock retry is off so a deadlock victim surfaces.",
    Act = "Four flushers each group-commit a run of 1000-row completion batches with results at the same time.",
    Assert = "Every call succeeds, every row finalizes, and every job is Succeeded with its result row."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionsBatchAsync))]
public abstract class ConcurrentCompletionBatchSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int Flushers = 4;
    private const int BatchSize = 1000;
    private const int BatchesPerFlusher = 5;

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

    [Fact(DisplayName = "Four flushers racing 1000-row completion batches with results in one namespace all finalize")]
    public async Task Concurrent_completion_batches_all_finalize()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!Services.GetRequiredService<ISqlDialect>().SupportsBatchCompletion)
        {
            Assert.Skip("SQLite has no batched completion; the Bulk profile runs as Direct there.");
        }

        var ids = await StageExecutingAsync(Flushers * BatchesPerFlusher * BatchSize, ct);
        var store = Services.GetRequiredService<IExecutionStore>();
        var result = JobPayload.Json(new AddNumbersResult(3)).Data;

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flushers = Enumerable
            .Range(0, Flushers)
            .Select(f =>
                Task.Run(
                    async () =>
                    {
                        await start.Task;
                        var mine = ids.Where((_, i) => i % Flushers == f).ToList();
                        foreach (var chunk in mine.Chunk(BatchSize))
                        {
                            var requests = chunk
                                .Select(id => new CompleteExecutionRequest(
                                    id,
                                    RuntimeStateStaging.StagedWorkerId,
                                    ExpectedExecutionNumber: 1,
                                    ExecutionOutcome.Succeeded,
                                    JobPayloadFormat.Json.Id,
                                    result,
                                    DurationMs: 1
                                ))
                                .ToList();
                            var outcomes = await store.CompleteExecutionsBatchAsync(requests, ct);
                            Assert.All(outcomes, o => Assert.True(o.Finalized));
                        }
                    },
                    ct
                )
            )
            .ToList();

        start.SetResult();
        await Task.WhenAll(flushers);

        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var statuses = (await Db.From<JobRuntime>().Where(r => r.NamespaceId == namespaceId).ToListAsync(ct)).ToDictionary(r => r.Id);
        Assert.All(ids, id => Assert.Equal(JobStatusCode.Succeeded, statuses[id].Status));

        long first = ids[0],
            last = ids[^1];
        var stored = (await Db.From<JobResult>().Where(r => r.JobId >= first && r.JobId <= last).ToListAsync(ct))
            .Select(r => r.JobId)
            .ToHashSet();
        Assert.All(ids, id => Assert.Contains(id, stored));
    }

    /// <summary>Enqueues <paramref name="count"/> jobs and stages them Executing under the staged worker, in id order.</summary>
    private async Task<List<long>> StageExecutingAsync(int count, CancellationToken ct)
    {
        var ids = new List<long>(count);
        for (var b = 0; b < count / BatchSize; b++)
        {
            var batch = Enumerable
                .Range(0, BatchSize)
                .Select(i => new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(b, i))))
                .ToList();
            ids.AddRange((await Jobs.EnqueueBatchAsync(batch, ct)).Select(o => o.JobId));
        }
        ids.Sort();

        await Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, leased_by_worker_id = @p_worker, lease_expires_at_utc = @p_expires, "
                + "execution_number = 1 WHERE namespace_id = @p_ns AND job_id >= @p_first AND job_id <= @p_last",
            ct,
            ("@p_status", (byte)JobStatusCode.Executing),
            ("@p_worker", RuntimeStateStaging.StagedWorkerId),
            ("@p_expires", DateTime.UtcNow.AddMinutes(10)),
            ("@p_ns", Runtime.RegisteredNamespaceIds[TestNamespace]),
            ("@p_first", ids[0]),
            ("@p_last", ids[^1])
        );
        return ids;
    }
}
