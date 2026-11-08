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
        // The repeat logs through the guard its callers hand it.
        var result = await WorkerFinalizer.RepeatAsync<string>(
            _ =>
            {
                calls++;
                return calls < 2 ? throw new ProviderDown() : Task.FromResult("landed");
            },
            new ActaLog.NonThrowingLogger(new ThrowingLoggerProvider.ThrowingLogger()),
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
        var landing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;

        // The retried write waits for the test to land it, so the heartbeat below finds the entry still in flight.
        var session = new ScriptedSession(
            async (call, token) =>
                call.Stem switch
                {
                    "CompleteExecutions" when Interlocked.Increment(ref writes) == 1 => FailOnce(failed),
                    "CompleteExecutions" => await LandAsync(landing, token),
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
            recoveryPass: null!,
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

        // The session records the finalizer's retried write from another thread, under the list's lock.
        BoundCall[] calls;
        lock (session.Calls)
        {
            calls = [.. session.Calls];
        }
        Assert.Equal(
            """[{"job_id":42,"execution_number":3}]""",
            Assert.Single(calls, call => call.Stem == "ExtendWorkerLeases").Parameters["p_executions"]
        );
        Assert.Equal(InFlightJobState.Finalizing, Assert.Single(registry.Snapshot()).State);
        Assert.False(entry.AttemptToken.IsCancellationRequested);

        landing.SetResult();
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

    [Fact(DisplayName = "A logger that throws on a handler's failure line does not stop that completion's batch landing")]
    public async Task A_throwing_logger_does_not_stop_a_failed_attempts_batch_landing()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = new ScriptedSession(call =>
            call.Stem switch
            {
                "CompleteExecutions" => new List<CompleteExecutionsRow>
                {
                    new(1, 0, CompleteExecutionsAction.Completed, (byte)JobStatusCode.Succeeded, null, DateTime.UtcNow, false, false),
                    new(2, 0, CompleteExecutionsAction.Completed, (byte)JobStatusCode.Failed, null, DateTime.UtcNow, false, false),
                    new(3, 0, CompleteExecutionsAction.Completed, (byte)JobStatusCode.Succeeded, null, DateTime.UtcNow, false, false),
                },
                _ => throw new NotSupportedException(call.Stem),
            }
        );
        // A real factory hands the sink's failure back as an AggregateException, as a host's does.
        using var logging = new LoggerFactory([new ThrowingLoggerProvider()]);
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
            logging.CreateLogger("Acta"),
            metrics: null
        );
        foreach (var jobId in new long[] { 1, 2, 3 })
        {
            var entry = WorkerHeartbeatLeaseRunwayTests.Entry(jobId, state: InFlightJobState.Finalizing);
            entry.Descriptor = Descriptor;
            entry.Completion =
                jobId == 2
                    ? new ExecutionCompletion(CompletionKind.Failed)
                    {
                        Reason = EventReason.UnhandledException,
                        HandlerException = new InvalidOperationException("503 from the carrier"),
                    }
                    : new ExecutionCompletion(CompletionKind.Succeeded);
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

    [Fact(DisplayName = "A log sink that throws while a batch keeps failing does not stop its rows landing one by one")]
    public async Task A_throwing_log_sink_does_not_stop_a_failing_batch_landing_row_by_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = new ScriptedSession(call => JobIds(call) is { Length: > 1 } ? throw new ProviderDown() : Landed(JobIds(call)));
        using var logging = new LoggerFactory([new ThrowingLoggerProvider()]);
        var (finalizer, registry) = Finalizer(session, logging.CreateLogger("Acta"));
        Complete(registry, 1);
        Complete(registry, 2, failed: true);
        Complete(registry, 3);
        registry.Completed.Writer.Complete();

        await finalizer.RunLoopAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);

        var written = session.Calls.Select(JobIds).ToList();
        Assert.Equal(WorkerFinalizer.BatchTries, written.Count(jobs => jobs.Length == 3));
        Assert.Equal([1L, 2L, 3L], written.Where(jobs => jobs.Length == 1).Select(jobs => jobs[0]).Order());
        Assert.Equal(0, registry.Count());
    }

    [Fact(DisplayName = "A log sink that throws on a skipped completion does not stop the rest of its batch landing")]
    public async Task A_throwing_log_sink_does_not_stop_a_batch_landing_past_a_skipped_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        // Another worker holds job 1's row now, so its completion is skipped; job 2's lands and wakes its waiters.
        var session = new ScriptedSession(_ => new List<CompleteExecutionsRow>
        {
            new(1, 0, CompleteExecutionsAction.NotOwner, (byte)JobStatusCode.Executing, null, DateTime.UtcNow, false, false),
            new(2, 0, CompleteExecutionsAction.Completed, (byte)JobStatusCode.Succeeded, null, DateTime.UtcNow, false, false),
        });
        using var logging = new LoggerFactory([new ThrowingLoggerProvider()]);
        var wakeup = new RecordingWakeup();
        var (finalizer, registry) = Finalizer(session, logging.CreateLogger("Acta"), wakeup);
        Complete(registry, 1);
        Complete(registry, 2);
        registry.Completed.Writer.Complete();

        await finalizer.RunLoopAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal([1L, 2L], Assert.Single(session.Calls.Select(JobIds)));
        Assert.Equal([WorkerWakeupChannel.JobCompletion(2)], wakeup.Woken);
        Assert.Equal(0, registry.Count());
    }

    [Fact(DisplayName = "A log sink that throws on a finalizer fault does not end the loop: the completions behind the fault land")]
    public async Task A_throwing_log_sink_does_not_end_the_finalizer_loop_on_a_fault()
    {
        var ct = TestContext.Current.CancellationToken;
        // An answer that leaves out job 2's row is a defect the batch cannot land, so the loop faults on it.
        var session = new ScriptedSession(call => Landed(JobIds(call).Where(jobId => jobId != 2)));
        using var logging = new LoggerFactory([new ThrowingLoggerProvider()]);
        var (finalizer, registry) = Finalizer(session, logging.CreateLogger("Acta"));
        Complete(registry, 1);
        Complete(registry, 2);

        var loop = finalizer.RunLoopAsync(ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (registry.Count() > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }
        Complete(registry, 3);
        registry.Completed.Writer.Complete();
        await loop.WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(
            [
                [1L, 2L],
                [3L],
            ],
            session.Calls.Select(JobIds)
        );
        Assert.Equal(0, registry.Count());
    }

    [Fact(DisplayName = "A failed completion still queued when the worker stops is logged once, as reclaimed after its lease")]
    public async Task A_failure_queued_at_a_stop_is_logged_once_as_reclaimed()
    {
        var log = new RecordingLogger();
        var (finalizer, registry) = Finalizer(new ScriptedSession(_ => throw new NotSupportedException()), log);
        Complete(registry, 1, failed: true);
        Complete(registry, 2);

        // Handed over after the loop's last read and before the stop, so the loop's next wait sees the stop first.
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        await finalizer.RunLoopAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var line = Assert.Single(log.Lines);
        Assert.StartsWith("Handler for job (receipt) (job_", line, StringComparison.Ordinal);
        Assert.EndsWith(
            "threw on attempt (1 of 1), and the worker stopped before writing the failure; the job is reclaimed after its lease expires.",
            line,
            StringComparison.Ordinal
        );
    }

    private const int Cap = WorkerFinalizer.MaxBatchBytes;

    // Each case's results, in bytes, in the order the executors hand them over, and the batches written, "|" between calls.
    [Theory(
        DisplayName = "A result that would take a batch past the byte cap starts the next batch, and one past the cap is written alone"
    )]
    [InlineData(new[] { Cap / 2, Cap / 2 + 1 }, "1|2")]
    [InlineData(new[] { Cap / 2, Cap / 2 + 1, 1 }, "1|2,3")]
    [InlineData(new[] { 1, Cap + 1, 1 }, "1|2|3")]
    [InlineData(new[] { Cap / 2, Cap / 2, 1 }, "1,2|3")]
    public async Task A_result_past_the_byte_cap_starts_the_next_batch(int[] resultBytes, string batches)
    {
        var ct = TestContext.Current.CancellationToken;
        var written = new List<long[]>();
        var session = new ScriptedSession(call =>
        {
            using var batch = JsonDocument.Parse((string)call.Parameters["p_completions"]!);
            var jobs = batch.RootElement.EnumerateArray().Select(row => row.GetProperty("job_id").GetInt64()).ToArray();
            written.Add(jobs);
            return jobs.Select(id => new CompleteExecutionsRow(
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
        });
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
        var result = new byte[resultBytes.Max()];
        for (var i = 0; i < resultBytes.Length; i++)
        {
            var entry = WorkerHeartbeatLeaseRunwayTests.Entry(i + 1, state: InFlightJobState.Finalizing);
            entry.Descriptor = Descriptor;
            entry.Completion = new ExecutionCompletion(CompletionKind.Succeeded) { Result = result.AsMemory(0, resultBytes[i]) };
            registry.TryAdd(entry);
            await registry.Completed.Writer.WriteAsync(entry, ct);
        }
        // Completed before the loop reads, so an entry held out of the last batch is all that is left when the channel ends.
        registry.Completed.Writer.Complete();

        await finalizer.RunLoopAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(batches, string.Join("|", written.Select(jobs => string.Join(",", jobs))));
        Assert.Equal(0, registry.Count());
    }

    [Fact(DisplayName = "A completion whose row already landed still wakes its waiters, since a lost answer sent none")]
    public async Task A_skipped_completion_still_wakes_its_waiters()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = new ScriptedSession(call =>
            call.Stem switch
            {
                "CompleteExecutions" => new List<CompleteExecutionsRow>
                {
                    new(1, 0, CompleteExecutionsAction.AlreadyTerminal, (byte)JobStatusCode.Succeeded, null, DateTime.UtcNow, false, false),
                },
                _ => throw new NotSupportedException(call.Stem),
            }
        );
        var options = new JobsOptions();
        var context = new WorkerContext(new WorkerRegistration("orders", null, null, [], []));
        context.NamespaceIds["orders"] = 1;
        context.WorkerIdByNamespace["orders"] = 1;
        var registry = new InFlightJobRegistry(options);
        var wakeup = new RecordingWakeup();
        var finalizer = new WorkerFinalizer(
            session.Services().GetRequiredService<ExecutionOperations>(),
            null!,
            new WorkerWakeupPublisher(wakeup),
            options,
            context,
            registry,
            NullLogger.Instance,
            metrics: null
        );
        var entry = WorkerHeartbeatLeaseRunwayTests.Entry(1, state: InFlightJobState.Finalizing);
        entry.Descriptor = Descriptor;
        entry.Completion = new ExecutionCompletion(CompletionKind.Succeeded);
        registry.TryAdd(entry);

        Assert.Equal(RunOnceStatus.NothingClaimed, await finalizer.FinalizeAsync(entry, ct));
        Assert.Equal([WorkerWakeupChannel.JobCompletion(1)], wakeup.Woken);
    }

    [Theory(DisplayName = "A completion that settles a firing wakes this hosts schedule admitter; a lane promotion wakes the claim loop")]
    [InlineData(JobKindCode.Permanent, null, JobStatusCode.Idle, false, false, false, true)]
    [InlineData(JobKindCode.Permanent, null, JobStatusCode.Failed, false, false, true, true)]
    [InlineData(JobKindCode.Permanent, null, JobStatusCode.Idle, false, true, false, true)]
    [InlineData(JobKindCode.Ordinary, null, JobStatusCode.Succeeded, false, true, true, false)]
    [InlineData(JobKindCode.Ordinary, 7, JobStatusCode.Succeeded, false, false, true, true)]
    [InlineData(JobKindCode.Ordinary, 7, JobStatusCode.Cancelled, false, false, true, true)]
    [InlineData(JobKindCode.Ordinary, 7, JobStatusCode.Succeeded, true, false, true, true)]
    [InlineData(JobKindCode.Ordinary, null, JobStatusCode.Succeeded, false, false, true, false)]
    public async Task A_settled_firing_wakes_the_schedule_admitter(
        JobKindCode kind,
        int? scheduleId,
        JobStatusCode finalStatus,
        bool answerLost,
        bool lanePromoted,
        bool wakesCompletion,
        bool wakesAdmitter
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var session = new ScriptedSession(call =>
            call.Stem switch
            {
                "CompleteExecutions" => new List<CompleteExecutionsRow>
                {
                    new(
                        1,
                        0,
                        answerLost ? CompleteExecutionsAction.AlreadyTerminal : CompleteExecutionsAction.Completed,
                        (byte)finalStatus,
                        null,
                        DateTime.UtcNow,
                        false,
                        lanePromoted
                    ),
                },
                _ => throw new NotSupportedException(call.Stem),
            }
        );
        var options = new JobsOptions();
        var context = new WorkerContext(new WorkerRegistration("orders", null, null, [], []));
        context.NamespaceIds["orders"] = 1;
        context.WorkerIdByNamespace["orders"] = 1;
        var registry = new InFlightJobRegistry(options);
        var wakeup = new RecordingWakeup();
        var finalizer = new WorkerFinalizer(
            session.Services().GetRequiredService<ExecutionOperations>(),
            null!,
            new WorkerWakeupPublisher(wakeup),
            options,
            context,
            registry,
            NullLogger.Instance,
            metrics: null
        );
        var claim = WorkerHeartbeatLeaseRunwayTests.Entry(1).Claim with { ScheduleId = scheduleId, Kind = kind };
        var entry = new InFlightJob(claim, null, CancellationToken.None)
        {
            State = InFlightJobState.Finalizing,
            Descriptor = Descriptor,
            Completion = new ExecutionCompletion(CompletionKind.Succeeded),
        };
        registry.TryAdd(entry);

        await finalizer.FinalizeAsync(entry, ct);
        List<WorkerWakeupChannel> expected = [];
        if (wakesCompletion)
        {
            expected.Add(WorkerWakeupChannel.JobCompletion(1));
        }
        if (lanePromoted)
        {
            expected.Add(WorkerWakeupChannel.WorkerNamespace("orders"));
        }
        if (wakesAdmitter)
        {
            expected.Add(WorkerWakeupChannel.Admission("orders"));
        }
        Assert.Equal(expected, wakeup.Woken);
    }

    [Fact(DisplayName = "A row the database keeps refusing holds only itself: its batch and the completions behind it land")]
    public async Task A_refused_row_holds_only_itself()
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
        async Task CompleteAsync(long jobId)
        {
            var entry = WorkerHeartbeatLeaseRunwayTests.Entry(jobId, state: InFlightJobState.Finalizing);
            entry.Descriptor = Descriptor;
            entry.Completion = new ExecutionCompletion(CompletionKind.Succeeded);
            registry.TryAdd(entry);
            await registry.Completed.Writer.WriteAsync(entry, ct);
        }
        async Task WaitForOnlyRefusedAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (registry.Count() > 1 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, ct);
            }
            Assert.Equal(2, Assert.Single(registry.Snapshot()).Key.JobId);
        }
        foreach (var jobId in new long[] { 1, 2, 3 })
        {
            await CompleteAsync(jobId);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = finalizer.RunLoopAsync(stop.Token);
        await WaitForOnlyRefusedAsync();
        Assert.Equal(WorkerFinalizer.BatchTries, batchTries);

        // The refused row repeats apart, so the loop goes on to the next completion.
        await CompleteAsync(4);
        await WaitForOnlyRefusedAsync();
        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    [Fact(DisplayName = "A drain waits for the descendant cancel that a row repeating apart starts when it lands")]
    public async Task A_drain_waits_for_the_cancel_a_row_landing_apart_starts()
    {
        var ct = TestContext.Current.CancellationToken;
        var writes = 0;
        var childReads = 0;
        var land = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var walking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var walked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Each call yields, so the refused row's zero-delay repeat cannot spin the test's own thread.
        var session = new ScriptedSession(
            async (call, token) =>
            {
                await Task.Yield();
                switch (call.Stem)
                {
                    case "CompleteExecutions":
                        Interlocked.Increment(ref writes);
                        return land.Task.IsCompleted
                            ? new List<CompleteExecutionsRow>
                            {
                                new(
                                    1,
                                    0,
                                    CompleteExecutionsAction.Completed,
                                    (byte)JobStatusCode.Cancelled,
                                    null,
                                    DateTime.UtcNow,
                                    false,
                                    false
                                ),
                            }
                            : throw new ProviderDown();
                    case "ListChildJobIds":
                        // The walk's first read fails, so the walk the row starts apart runs again before it gets here.
                        if (Interlocked.Increment(ref childReads) == 1)
                        {
                            throw new ProviderDown();
                        }
                        walking.TrySetResult();
                        await walked.Task.WaitAsync(token);
                        return Array.Empty<long>();
                    default:
                        throw new NotSupportedException(call.Stem);
                }
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
            services.GetRequiredService<JobsOperations>(),
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

        // A handler that cancelled its job, whose completion lands only after its batch tries ran out.
        var entry = WorkerHeartbeatLeaseRunwayTests.Entry(1, state: InFlightJobState.Finalizing);
        entry.Descriptor = Descriptor;
        entry.Completion = new ExecutionCompletion(CompletionKind.Cancelled) { HandlerStatusCode = JobStatusCode.Cancelled };
        registry.TryAdd(entry);
        await registry.Completed.Writer.WriteAsync(entry, ct);
        registry.Completed.Writer.Complete();

        async Task WaitForAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, ct);
            }
            Assert.True(condition());
        }

        // The executors are done, so once the row repeats apart the loop leaves its channel for the drain.
        var loop = finalizer.RunLoopAsync(ct);
        await WaitForAsync(() => Volatile.Read(ref writes) > WorkerFinalizer.BatchTries);
        land.SetResult();

        // The row landed and left the registry, and the walk it started is still under way.
        await walking.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await WaitForAsync(() => registry.Count() == 0);
        await Task.WhenAny(loop, Task.Delay(50, ct));
        Assert.False(loop.IsCompleted);

        walked.SetResult();
        await loop.WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(2, childReads);
    }

    [Fact(DisplayName = "A handler cancel's descendant walk that fails partway runs again from the root and cancels the rest")]
    public async Task A_failed_descendant_walk_runs_again_from_the_root()
    {
        var ct = TestContext.Current.CancellationToken;
        var refused = 0;
        var session = new ScriptedSession(call =>
            call.Stem switch
            {
                "CompleteExecutions" => new List<CompleteExecutionsRow>
                {
                    new(1, 0, CompleteExecutionsAction.Completed, (byte)JobStatusCode.Cancelled, null, DateTime.UtcNow, false, false),
                },
                "ListChildJobIds" => call.Get(ActaSchema.Job.ParentJobId) == 1 ? new long[] { 2, 3 } : Array.Empty<long>(),
                "CancelJob" when call.Get(ActaSchema.Job.JobId) == 3 && Interlocked.Increment(ref refused) == 1 => throw new ProviderDown(),
                "CancelJob" => new CancelJobRow(ControlAction.Applied, JobStatusCode.Cancelled, null, 1, false),
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
            services.GetRequiredService<JobsOperations>(),
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
        var entry = WorkerHeartbeatLeaseRunwayTests.Entry(1, state: InFlightJobState.Finalizing);
        entry.Descriptor = Descriptor;
        entry.Completion = new ExecutionCompletion(CompletionKind.Cancelled) { HandlerStatusCode = JobStatusCode.Cancelled };
        registry.TryAdd(entry);

        Assert.Equal(RunOnceStatus.Completed, await finalizer.FinalizeAsync(entry, ct));
        Assert.Equal(
            [2L, 3L, 2L, 3L],
            session.Calls.Where(call => call.Stem == "CancelJob").Select(call => call.Get(ActaSchema.Job.JobId))
        );
    }

    // A one-shot definition: the batch reads only its retention, and the plan its budget and deadline behavior.
    private static readonly JobDescriptor Descriptor = new(
        "receipt",
        typeof(WorkerFinalizerTests),
        MethodName: "Run",
        InputType: typeof(object),
        ResultType: null,
        InputPayloadFormat: default,
        ResultPayloadFormat: null,
        Priority: default,
        MaxAttempts: 1,
        EventRecording: default,
        AlertProfile: default,
        FiringFailurePolicy: FiringFailurePolicyCode.Continue,
        ScheduledDelivery: ScheduledDeliveryCode.Permanent,
        Invoker: null!,
        DeserializeInput: null!,
        SerializeResult: null
    );

    // The finalizer the loop tests drive, for one namespace, with no delay between repeated writes.
    private static (WorkerFinalizer Finalizer, InFlightJobRegistry Registry) Finalizer(
        ScriptedSession session,
        ILogger log,
        IWorkerWakeup? wakeup = null
    )
    {
        var options = new JobsOptions();
        var context = new WorkerContext(new WorkerRegistration("orders", null, null, [], []));
        context.NamespaceIds["orders"] = 1;
        context.WorkerIdByNamespace["orders"] = 1;
        var registry = new InFlightJobRegistry(options);
        var finalizer = new WorkerFinalizer(
            session.Services().GetRequiredService<ExecutionOperations>(),
            null!,
            new WorkerWakeupPublisher(wakeup ?? new InProcessWakeup()),
            options,
            context,
            registry,
            log,
            metrics: null
        )
        {
            RepeatFirstDelay = TimeSpan.Zero,
        };
        return (finalizer, registry);
    }

    // Hands the finalizer job's completion, as its executor does once the attempt ends.
    private static void Complete(InFlightJobRegistry registry, long jobId, bool failed = false)
    {
        var entry = WorkerHeartbeatLeaseRunwayTests.Entry(jobId, state: InFlightJobState.Finalizing);
        entry.Descriptor = Descriptor;
        entry.Completion = failed
            ? new ExecutionCompletion(CompletionKind.Failed)
            {
                Reason = EventReason.UnhandledException,
                HandlerException = new InvalidOperationException("503 from the carrier"),
            }
            : new ExecutionCompletion(CompletionKind.Succeeded);
        registry.TryAdd(entry);
        Assert.True(registry.Completed.Writer.TryWrite(entry));
    }

    private static long[] JobIds(BoundCall call)
    {
        using var batch = JsonDocument.Parse((string)call.Parameters["p_completions"]!);
        return [.. batch.RootElement.EnumerateArray().Select(row => row.GetProperty("job_id").GetInt64())];
    }

    private static List<CompleteExecutionsRow> Landed(IEnumerable<long> jobIds) =>
        [
            .. jobIds.Select(jobId => new CompleteExecutionsRow(
                jobId,
                0,
                CompleteExecutionsAction.Completed,
                (byte)JobStatusCode.Succeeded,
                null,
                DateTime.UtcNow,
                false,
                false
            )),
        ];

    private static object FailOnce(TaskCompletionSource failed)
    {
        failed.SetResult();
        throw new ProviderDown();
    }

    private static async Task<object?> LandAsync(TaskCompletionSource gate, CancellationToken ct)
    {
        await gate.Task.WaitAsync(ct);
        return new List<CompleteExecutionsRow>
        {
            new(42, 3, CompleteExecutionsAction.Completed, (byte)JobStatusCode.Ready, null, DateTime.UtcNow, false, false),
        };
    }

    private sealed class ProviderDown() : DbException("connection dropped");

    private sealed class RecordingWakeup : IWorkerWakeup
    {
        public List<WorkerWakeupChannel> Woken { get; } = [];

        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
        {
            Woken.Add(channel);
            return ValueTask.CompletedTask;
        }

        public ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add(formatter(state, exception));
    }
}
