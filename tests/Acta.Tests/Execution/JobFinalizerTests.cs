using System.Data.Common;
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
public sealed class JobFinalizerTests
{
    [Fact(DisplayName = "A failure that clears is repeated until the write lands")]
    public async Task A_failure_that_clears_is_repeated_until_the_write_lands()
    {
        var calls = 0;
        var result = await JobFinalizer.RepeatAsync<string>(
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
        var result = await JobFinalizer.RepeatAsync<string>(
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
        await JobFinalizer.RepeatAsync<string>(
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
            JobFinalizer.RepeatAsync<int>(
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
            JobFinalizer.RepeatAsync<int>(
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
        var result = await JobFinalizer.RepeatAsync<string>(
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
                "CompleteExecution" when Interlocked.Increment(ref writes) == 1 => FailOnce(failed),
                "CompleteExecution" => new CompleteExecutionResponse(
                    CompleteExecutionAction.Completed,
                    (byte)JobStatusCode.Ready,
                    FinalNextRunAtUtc: null,
                    DateTime.UtcNow,
                    ParentReleased: false
                ),
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
        var finalizer = new JobFinalizer(
            services.GetRequiredService<Acta.Execution>(),
            null!,
            new WorkerWakeupPublisher(new InProcessWakeup()),
            options,
            context,
            registry,
            NullLogger.Instance,
            metrics: null
        );
        var heartbeat = new WorkerHeartbeat(
            services.GetRequiredService<WorkerLifecycle>(),
            ScriptedSession.Unreachable.GetRequiredService<GetDefinitionContractsHandler>(),
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
