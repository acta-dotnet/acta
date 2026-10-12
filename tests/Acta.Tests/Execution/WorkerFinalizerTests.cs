using System.Data.Common;
using System.Text.Json;
using Acta.Tests.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// The finalizer's write. A completion abandoned after a thrown exception leaves the row Executing until its lease
/// lapses, and recovery then charges a failure and runs the job again, so the write is repeated until it lands or the
/// worker stops, and a defect is repeated alongside a provider error because a loud loop costs less than a finished
/// execution run twice. Meanwhile the entry stays held as Finalizing, so the heartbeat keeps renewing its row.
/// </summary>
public sealed class WorkerFinalizerTests
{
    [Fact(DisplayName = "A failure that clears is repeated until the write lands")]
    public async Task A_failure_that_clears_is_repeated_until_the_write_lands()
    {
        var calls = 0;
        var result = await WorkerFinalizer.RepeatAsync<string>(
            _ =>
            {
                calls++;
                return calls < 3 ? throw new ProviderDown() : Task.FromResult("landed");
            },
            NullLogger.Instance,
            jobId: 7,
            TestContext.Current.CancellationToken,
            firstDelay: TimeSpan.Zero
        );

        Assert.Equal("landed", result);
        Assert.Equal(3, calls);
    }

    // Fifty is past any bounded budget: the point is that no counter ends the repeat, and the old
    // five-try budget is the number this has to outlive.
    [Fact(DisplayName = "A provider error outlives any bounded budget and lands when it clears")]
    public async Task A_persistent_provider_error_outlives_a_bounded_budget()
    {
        var calls = 0;
        var result = await WorkerFinalizer.RepeatAsync<string>(
            _ =>
            {
                calls++;
                return calls < 50 ? throw new ProviderDown() : Task.FromResult("landed");
            },
            NullLogger.Instance,
            jobId: 7,
            TestContext.Current.CancellationToken,
            firstDelay: TimeSpan.Zero
        );

        Assert.Equal("landed", result);
        Assert.Equal(50, calls);
    }

    [Fact(DisplayName = "A failure that is not a provider error is repeated too, because abandoning it strands the row")]
    public async Task A_failure_that_is_not_a_provider_error_is_repeated_too()
    {
        var calls = 0;
        await WorkerFinalizer.RepeatAsync<string>(
            _ =>
            {
                calls++;
                return calls < 4 ? throw new InvalidOperationException("a defect in the completion path") : Task.FromResult("landed");
            },
            NullLogger.Instance,
            jobId: 7,
            TestContext.Current.CancellationToken,
            firstDelay: TimeSpan.Zero
        );

        Assert.Equal(4, calls);
    }

    [Fact(DisplayName = "A cancelled worker stops on the first try")]
    public async Task A_cancelled_worker_stops_on_the_first_try()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WorkerFinalizer.RepeatAsync<int>(
                token =>
                {
                    calls++;
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(0);
                },
                NullLogger.Instance,
                jobId: 7,
                cts.Token,
                firstDelay: TimeSpan.Zero
            )
        );

        Assert.Equal(1, calls);
    }

    [Fact(DisplayName = "A worker that stops mid-repeat surfaces the write's own failure, not the stop")]
    public async Task A_worker_that_stops_mid_repeat_surfaces_the_write_failure()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;

        // The stop says why the repeat ended; the failure says why the row is still Executing, and that
        // is what the caller logs.
        await Assert.ThrowsAsync<ProviderDown>(() =>
            WorkerFinalizer.RepeatAsync<int>(
                _ =>
                {
                    calls++;
                    cts.Cancel();
                    throw new ProviderDown();
                },
                NullLogger.Instance,
                jobId: 7,
                cts.Token,
                firstDelay: TimeSpan.FromMinutes(1)
            )
        );

        Assert.Equal(1, calls);
    }

    [Fact(DisplayName = "A logger that throws while the repeat reports a failure does not end the repeat")]
    public async Task A_throwing_logger_does_not_end_the_repeat()
    {
        var calls = 0;
        var result = await WorkerFinalizer.RepeatAsync<string>(
            _ =>
            {
                calls++;
                return calls < 2 ? throw new ProviderDown() : Task.FromResult("landed");
            },
            new ThrowingLogger(),
            jobId: 7,
            TestContext.Current.CancellationToken,
            firstDelay: TimeSpan.Zero
        );

        Assert.Equal("landed", result);
        Assert.Equal(2, calls);
    }

    [Fact(DisplayName = "An entry is Finalizing and renewed between its executor's release and the commit of its write")]
    public async Task A_finalizing_entry_is_renewed_until_its_write_lands()
    {
        var ct = TestContext.Current.CancellationToken;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var session = new ScriptedSession(call =>
            call.Stem switch
            {
                "CompleteExecutions" when Interlocked.Increment(ref writes) == 1 => FailOnce(failed),
                "CompleteExecutions" => new List<CompleteExecutionsRow>
                {
                    new(42, 3, CompleteExecutionsAction.Completed, (byte)JobStatusCode.Ready, null, DateTime.UtcNow, false, false),
                },
                "ExtendWorkerLeases" => new ExtendWorkerLeasesRow(42, ExecutionNumber: 3, LockKey: null, Renewed: true, null),
                _ => throw new NotSupportedException(call.Stem),
            }
        );
        var services = session.Services();
        var options = new JobsOptions();
        var context = new WorkerContext(new WorkerRegistration("orders", null, null, [], []));
        context.NamespaceIds["orders"] = 1;
        context.WorkerIdByNamespace["orders"] = 1;
        var registry = new InFlightJobRegistry(options);
        var finalizer = new WorkerFinalizer(
            services.GetRequiredService<ExecutionOperations>(),
            null!,
            new WorkerWakeupPublisher(new InProcessWakeup()),
            options,
            context,
            registry,
            NullLogger.Instance,
            metrics: null
        );
        var heartbeat = new WorkerHeartbeat(
            services.GetRequiredService<WorkersOperations>(),
            ScriptedSession.Unreachable.GetRequiredService<DefinitionsOperations>(),
            Options.Create(options),
            context.WorkerRegistration,
            context,
            registry,
            NullLogger.Instance
        );

        // An unsupported claim's hand-back, the completion that needs no descriptor.
        var entry = WorkerHeartbeatLeaseRunwayTests.Entry(42, executionNumber: 3, leaseRunway: TimeSpan.FromMinutes(1));
        entry.Completion = new ExecutionCompletion(CompletionKind.Rescheduled) { ReasonMessage = "handed back" };
        entry.State = InFlightJobState.Finalizing;
        registry.TryAdd(entry);

        var finalizing = finalizer.FinalizeAsync(entry, ct);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await heartbeat.TickAsync(ct);

        Assert.Equal(
            """[{"job_id":42,"execution_number":3}]""",
            Assert.Single(session.Calls, call => call.Stem == "ExtendWorkerLeases").Parameters["p_executions"]
        );
        Assert.Equal(InFlightJobState.Finalizing, Assert.Single(registry.Snapshot()).State);
        Assert.False(entry.AttemptToken.IsCancellationRequested);

        Assert.Equal(RunOnceStatus.Rearmed, await finalizing.WaitAsync(TimeSpan.FromSeconds(30), ct));
        Assert.Equal(2, writes);
        Assert.Equal(0, registry.Count());
    }

    [Fact(DisplayName = "Completions waiting together are written in one call")]
    public async Task Completions_waiting_together_are_written_in_one_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = new ScriptedSession(call =>
            call.Stem switch
            {
                "CompleteExecutions" => new List<CompleteExecutionsRow>
                {
                    new(1, 0, CompleteExecutionsAction.Completed, (byte)JobStatusCode.Succeeded, null, DateTime.UtcNow, false, false),
                    new(2, 0, CompleteExecutionsAction.Completed, (byte)JobStatusCode.Succeeded, null, DateTime.UtcNow, false, false),
                    new(3, 0, CompleteExecutionsAction.Completed, (byte)JobStatusCode.Succeeded, null, DateTime.UtcNow, false, false),
                },
                _ => throw new NotSupportedException(call.Stem),
            }
        );
        var options = new JobsOptions();
        var context = new WorkerContext(new WorkerRegistration("orders", null, null, [], []));
        context.NamespaceIds["orders"] = 1;
        context.WorkerIdByNamespace["orders"] = 1;
        var registry = new InFlightJobRegistry(options);
        var finalizer = new WorkerFinalizer(
            session.Services().GetRequiredService<ExecutionOperations>(),
            null!,
            new WorkerWakeupPublisher(new InProcessWakeup()),
            options,
            context,
            registry,
            NullLogger.Instance,
            metrics: null
        );
        foreach (var jobId in new long[] { 1, 2, 3 })
        {
            var entry = WorkerHeartbeatLeaseRunwayTests.Entry(jobId, state: InFlightJobState.Finalizing);
            entry.Descriptor = Descriptor;
            entry.Completion = new ExecutionCompletion(CompletionKind.Succeeded);
            registry.TryAdd(entry);
            await registry.Completed.Writer.WriteAsync(entry, ct);
        }
        registry.Completed.Writer.Complete();

        await finalizer.RunLoopAsync(ct);

        Assert.Equal(["CompleteExecutions"], session.Calls.Select(call => call.Stem));
        using var batch = JsonDocument.Parse((string)session.Calls[0].Parameters["p_completions"]!);
        Assert.Equal([1L, 2L, 3L], batch.RootElement.EnumerateArray().Select(row => row.GetProperty("job_id").GetInt64()));
        Assert.Equal(0, registry.Count());
    }

    [Fact(DisplayName = "A row the database keeps refusing is written alone, and the rest of its batch lands")]
    public async Task A_refused_row_is_written_alone_while_its_batch_lands()
    {
        var ct = TestContext.Current.CancellationToken;
        var batchTries = 0;
        // Each call yields, so the refused row's zero-delay repeat cannot spin the test's own thread.
        var session = new ScriptedSession(
            async (call, _) =>
            {
                await Task.Yield();
                using var batch = JsonDocument.Parse((string)call.Parameters["p_completions"]!);
                var jobs = batch.RootElement.EnumerateArray().Select(row => row.GetProperty("job_id").GetInt64()).ToList();
                if (jobs.Count > 1)
                {
                    Interlocked.Increment(ref batchTries);
                }
                if (jobs.Contains(2))
                {
                    throw new ProviderDown();
                }
                return (object?)
                    jobs.Select(id => new CompleteExecutionsRow(
                            id,
                            0,
                            CompleteExecutionsAction.Completed,
                            (byte)JobStatusCode.Succeeded,
                            null,
                            DateTime.UtcNow,
                            false,
                            false
                        ))
                        .ToList();
            }
        );
        var options = new JobsOptions();
        var context = new WorkerContext(new WorkerRegistration("orders", null, null, [], []));
        context.NamespaceIds["orders"] = 1;
        context.WorkerIdByNamespace["orders"] = 1;
        var registry = new InFlightJobRegistry(options);
        var finalizer = new WorkerFinalizer(
            session.Services().GetRequiredService<ExecutionOperations>(),
            null!,
            new WorkerWakeupPublisher(new InProcessWakeup()),
            options,
            context,
            registry,
            NullLogger.Instance,
            metrics: null
        )
        {
            RepeatFirstDelay = TimeSpan.Zero,
        };
        foreach (var jobId in new long[] { 1, 2, 3 })
        {
            var entry = WorkerHeartbeatLeaseRunwayTests.Entry(jobId, state: InFlightJobState.Finalizing);
            entry.Descriptor = Descriptor;
            entry.Completion = new ExecutionCompletion(CompletionKind.Succeeded);
            registry.TryAdd(entry);
            await registry.Completed.Writer.WriteAsync(entry, ct);
        }
        registry.Completed.Writer.Complete();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = finalizer.RunLoopAsync(stop.Token);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (registry.Count() > 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }

        Assert.Equal(WorkerFinalizer.BatchTries, batchTries);
        Assert.Equal(2, Assert.Single(registry.Snapshot()).Key.JobId);
        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    // A one-shot definition: the batch reads only its retention, and the plan its budget and deadline behavior.
    private static readonly JobDescriptor Descriptor = new(
        "receipt",
        typeof(WorkerFinalizerTests),
        MethodName: "Run",
        InputType: typeof(object),
        OutputType: null,
        InputPayloadFormat: default,
        OutputPayloadFormat: null,
        InvocationKind: default,
        RequiresJobContextParameter: false,
        RequiresCancellationToken: false,
        Priority: default,
        MaxAttempts: 1,
        AuditLevel: default,
        AlertProfile: default,
        Invoker: null!,
        DeserializeInput: null!,
        SerializeOutput: null
    );

    private static object FailOnce(TaskCompletionSource failed)
    {
        failed.SetResult();
        throw new ProviderDown();
    }

    private sealed class ProviderDown() : DbException("connection dropped");

    // Stands in for any diagnostic sink that fails: the repeat's job is the row, not the log line.
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => throw new InvalidOperationException("the log sink is down");
    }
}
