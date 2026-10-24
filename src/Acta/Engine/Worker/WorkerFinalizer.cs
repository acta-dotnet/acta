using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// Writes each execution's completion: the one-shot retry budget and its Strict deadline guard, the
/// <c>complete_executions</c> row for each completion shape (including the recurring-completion outcome math), the
/// write, repeated until it lands, and the wakes and follow-ups its final state calls for. Its loop takes what the
/// executors completed and writes them together in one <c>complete_executions</c> call, so an executor is free as soon
/// as its completion is built, while the entry stays held, and renewed, until the write commits.
/// </summary>
internal sealed partial class WorkerFinalizer(
    ExecutionOperations execution,
    JobsOperations jobs,
    WorkerWakeupPublisher wakeupPublisher,
    JobsOptions options,
    WorkerContext context,
    InFlightJobRegistry registry,
    ILogger log,
    ActaMetrics? metrics
)
{
    // One second doubling to a thirty-second ceiling, on the runtime's one retry curve: a dropped
    // connection costs almost nothing, and a failover measured in minutes is ridden out without
    // hammering the provider that is coming back. A quarter of jitter keeps a fleet whose writes failed
    // together from coming back in lockstep.
    private static readonly Backoff RepeatBackoff = Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)).WithJitter(0.25);

    // Warning while a blip is still the likely story, Error once the failure has outlived one.
    private const int WarningTries = 3;

    // Re-arm delay for a claim handed back unrun, in whole seconds (the store verb's unit).
    // SafetyPollInterval is the right unit for it: the option is documented as the bound on how long
    // a Ready row waits to be found by a process this one shares no wakeup transport with, and a
    // process that carries the missing handler is exactly who has to pick this job up. Validation
    // keeps it at or above one second, so the delay never rounds to zero.
    private readonly int _handBackDelaySeconds = (int)Math.Ceiling(options.SafetyPollInterval.TotalSeconds);

    /// <summary>The most completions one call writes; commit cost flattens well before this.</summary>
    public const int MaxBatch = 128;

    /// <summary>
    /// The most result bytes one call carries, so a batch of large results never makes one huge transaction; a single
    /// result past it is written alone.
    /// </summary>
    public const int MaxBatchBytes = 4 * 1024 * 1024;

    /// <summary>
    /// How long a batch that found a backlog waits for more before it is written. Without the wait a batch holds only
    /// what arrived during the previous write, a handful of rows at any rate; with it the cap fills at a few thousand
    /// completions a second, which halves the commits per job. A lone completion never waits: the window opens only
    /// when the first read already found more than one waiting.
    /// </summary>
    public static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How many times a batch is tried before each of its rows is written alone, so a row the database keeps refusing
    /// holds only itself: the others land, and it repeats alone until it lands or the worker stops.
    /// </summary>
    internal const int BatchTries = 3;

    /// <summary>The first delay between repeated writes; null keeps the one-second curve. Tests set zero.</summary>
    internal TimeSpan? RepeatFirstDelay { get; init; }

    // Rows still failing alone after BatchTries, each repeating apart from the loop so it holds only itself. The bound
    // is the executor count: past it the loop repeats in place, so a failure that refuses every completion still stops
    // intake (docs/known-limitations.md, the completion backpressure).
    private readonly ConcurrentDictionary<InFlightJob, Task> _apart = new();
    private readonly int _apartLimit = options.MaxConcurrentExecutors;

    // Descendant cancels the loop started and did not wait for; a drain waits for them.
    private readonly List<Task> _cancels = [];

    private int WorkerId => context.ResolveWorker(context.WorkerRegistration!.NamespaceName).WorkerId;

    /// <summary>
    /// The worker's one finalizer loop: finalizes each completion together with whatever else is already waiting,
    /// until the channel is completed and empty, so a batch forms only when the writes fall behind. A fault ends its batch.
    /// </summary>
    public async Task RunLoopAsync(CancellationToken hostCt)
    {
        var reader = registry.Completed.Reader;
        try
        {
            await foreach (var first in reader.ReadAllAsync(hostCt))
            {
                List<InFlightJob> entries = [first];
                var bytes = first.Completion!.Result.Length;
                using var window = new CancellationTokenSource(BatchWindow);
                while (entries.Count < MaxBatch && bytes < MaxBatchBytes)
                {
                    if (!reader.TryRead(out var next))
                    {
                        if (entries.Count == 1 || !await WaitForNextAsync(reader, window.Token))
                        {
                            break;
                        }
                        reader.TryRead(out next);
                    }
                    entries.Add(next!);
                    bytes += next!.Completion!.Result.Length;
                }

                try
                {
                    // Written first, so a metrics listener that throws cannot strand the batch unwritten.
                    await FinalizeAsync(entries, apart: true, hostCt);
                    metrics?.RecordCompletionBatch(context.WorkerRegistration!.NamespaceName, entries.Count);
                }
                catch (Exception ex)
                {
                    // On a hard stop the rows fall to recovery, and the write's own failure is not a fault.
                    if (!hostCt.IsCancellationRequested)
                    {
                        log.LogError(ex, "WorkerRuntime: finalizer faulted on {Count} completions.", entries.Count);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
        {
            // A hard stop: completions still queued re-run after recovery, as an interrupted write does.
        }

        // A drain ends once the rows repeating apart land and the descendant cancels finish; a hard stop ends them. The
        // rows go first, since a row that lands apart can start a cancel of its own, and only the loop sets rows apart.
        await Task.WhenAll(_apart.Values);
        Task[] cancels;
        lock (_cancels)
        {
            cancels = [.. _cancels];
        }
        await Task.WhenAll(cancels);
    }

    /// <summary>Waits for another completion until the window closes or the channel ends; false when nothing more comes.</summary>
    private static async Task<bool> WaitForNextAsync(ChannelReader<InFlightJob> reader, CancellationToken window)
    {
        try
        {
            return await reader.WaitToReadAsync(window);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Writes <paramref name="entry"/>'s completion on its own and removes the entry.</summary>
    public async Task<RunOnceStatus> FinalizeAsync(InFlightJob entry, CancellationToken hostCt) =>
        (await FinalizeAsync([entry], apart: false, hostCt))[0];

    /// <summary>
    /// Writes the completions in one call, lands each answer, and removes every entry. A batch still failing after
    /// <see cref="BatchTries"/> tries is written row by row, each row landed and removed as soon as it commits; with
    /// <paramref name="apart"/>, a row still failing alone after as many tries repeats apart from the caller.
    /// </summary>
    private async Task<RunOnceStatus[]> FinalizeAsync(List<InFlightJob> entries, bool apart, CancellationToken hostCt)
    {
        try
        {
            var writes = entries.Select(Plan).ToList();
            CompleteExecutionsResponse? answer = null;
            if (writes.Count > 1)
            {
                try
                {
                    answer = await RepeatAsync(
                        token => CompleteAsync(writes, token),
                        log,
                        writes[0].Row.JobId,
                        hostCt,
                        metrics,
                        RepeatFirstDelay,
                        BatchTries
                    );
                }
                catch (Exception ex) when (!hostCt.IsCancellationRequested)
                {
                    log.LogError(ex, "WorkerRuntime: a batch of {Count} completions kept failing; writing each alone.", writes.Count);
                }
            }
            if (answer is null)
            {
                return await Task.WhenAll(writes.Select(write => FinalizeAloneAsync(write, apart, hostCt)));
            }

            var rows = answer.Rows.ToDictionary(row => (row.JobId, row.ExecutionNumber));
            var statuses = new RunOnceStatus[writes.Count];
            for (var i = 0; i < writes.Count; i++)
            {
                statuses[i] = await LandAsync(writes[i], rows[writes[i].Entry.Key], apart, hostCt);
            }
            return statuses;
        }
        finally
        {
            foreach (var entry in entries.Where(entry => !_apart.ContainsKey(entry)))
            {
                registry.Remove(entry);
            }
        }
    }

    /// <summary>
    /// Writes one row alone until it lands. With <paramref name="apart"/>, a row still failing after
    /// <see cref="BatchTries"/> tries repeats apart while there is room, and answers NothingClaimed for now.
    /// </summary>
    private async Task<RunOnceStatus> FinalizeAloneAsync(PlannedWrite write, bool apart, CancellationToken hostCt)
    {
        if (apart)
        {
            try
            {
                return await WriteAloneAsync(write, BatchTries, apart, hostCt);
            }
            catch (Exception ex) when (!hostCt.IsCancellationRequested)
            {
                if (SetApart(write, ex, hostCt))
                {
                    return RunOnceStatus.NothingClaimed;
                }
            }
        }
        return await WriteAloneAsync(write, int.MaxValue, apart, hostCt);
    }

    /// <summary>Starts <paramref name="write"/>'s repeat apart, unless as many rows as the bound already repeat apart.</summary>
    private bool SetApart(PlannedWrite write, Exception ex, CancellationToken hostCt)
    {
        lock (_apart)
        {
            if (_apart.Count >= _apartLimit)
            {
                return false;
            }
            _apart[write.Entry] = RepeatApartAsync(write, hostCt);
        }
        log.LogError(ex, "WorkerRuntime: job {JobId}'s completion keeps failing alone; it repeats apart from the others.", write.Row.JobId);
        return true;
    }

    private async Task RepeatApartAsync(PlannedWrite write, CancellationToken hostCt)
    {
        // Yields first, so the repeat never runs under SetApart's lock; taking the lock to leave the set waits for the add.
        await Task.Yield();
        try
        {
            await WriteAloneAsync(write, int.MaxValue, apart: true, hostCt);
        }
        catch (Exception) when (hostCt.IsCancellationRequested)
        {
            // A hard stop: the row falls to recovery, as an interrupted write does.
        }
        finally
        {
            lock (_apart)
            {
                _apart.TryRemove(write.Entry, out _);
            }
            registry.Remove(write.Entry);
        }
    }

    /// <summary>Writes one row alone until it lands or <paramref name="tries"/> run out, then lands its answer and removes its entry.</summary>
    private async Task<RunOnceStatus> WriteAloneAsync(PlannedWrite write, int tries, bool apart, CancellationToken hostCt)
    {
        var answer = await RepeatAsync(
            token => CompleteAsync([write], token),
            log,
            write.Row.JobId,
            hostCt,
            metrics,
            RepeatFirstDelay,
            tries
        );
        var status = await LandAsync(write, answer.Rows[0], apart, hostCt);
        registry.Remove(write.Entry);
        return status;
    }

    private Task<CompleteExecutionsResponse> CompleteAsync(List<PlannedWrite> writes, CancellationToken ct) =>
        execution.CompleteExecutionsAsync(new CompleteExecutionsCommand(WorkerId, [.. writes.Select(write => write.Row)]), ct);

    /// <summary>
    /// Plans <paramref name="entry"/>'s write and the status it reports once the write lands. A job whose definition is
    /// retired never runs again, so a landing that would leave it runnable (a re-arm, a hand-back, or a recurring
    /// rollover) lands Cancelled instead.
    /// </summary>
    private PlannedWrite Plan(InFlightJob entry)
    {
        var planned = PlanCompletion(entry);
        if (
            planned.Row is { RescheduleEventCode: null, FinalStatus: null }
            || !context.RetentionSecondsByRetiredDefinitionId.TryGetValue(entry.Claim.DefinitionId, out var retentionSeconds)
        )
        {
            return planned;
        }

        var retired = new ExecutionCompletion(CompletionKind.Cancelled)
        {
            Reason = EventReason.DefinitionRetired,
            ReasonMessage = "The job's definition is retired, so the job does not run again.",
            HandlerStatusCode = JobStatusCode.Cancelled,
            DurationMs = planned.Completion.DurationMs,
        };
        return new(entry, retired, Retire(entry.Claim, retired, retentionSeconds), RunOnceStatus.Completed);
    }

    /// <summary>Plans <paramref name="entry"/>'s write as its completion stands.</summary>
    private PlannedWrite PlanCompletion(InFlightJob entry)
    {
        var job = entry.Claim;
        var completion = entry.Completion!;
        if (entry.Descriptor is not { } descriptor)
        {
            return new(entry, completion, HandBack(job, completion), RunOnceStatus.Rearmed);
        }

        var retry = PlanRetry(descriptor, job, entry.Fire, completion);

        // Deadline reschedule guard (Strict): if the next retry would land after the deadline,
        // terminate Cancelled+JobDeadlineExceeded via the handler status instead of re-arming.
        // Budget is left untouched; the handler-status shape handles completion. Same worker-clock
        // comparison as the admission check: deliberate and skew-guarded, not a DB read.
        if (
            retry.InBudget
            && descriptor.DeadlineBehavior == DeadlineBehaviorCode.Strict
            && entry.Context?.TimeUntilDeadline is { } untilDeadline
            && untilDeadline < TimeSpan.FromSeconds(retry.DelaySeconds)
        )
        {
            completion = completion with
            {
                Outcome = CompletionKind.Cancelled,
                Reason = EventReason.DeadlineExceeded,
                ReasonMessage = "Next retry would exceed the job deadline.",
                HandlerStatusCode = JobStatusCode.Cancelled,
            };
        }

        var status = completion.Outcome switch
        {
            // A failed one-shot that re-armed for another attempt: the row is Ready, not Failed.
            CompletionKind.Failed when retry.InBudget => RunOnceStatus.Rearmed,
            CompletionKind.Succeeded or CompletionKind.Cancelled => RunOnceStatus.Completed,
            CompletionKind.Rescheduled or CompletionKind.Suspended or CompletionKind.Paused => RunOnceStatus.Rearmed,
            _ => RunOnceStatus.Failed,
        };
        return new(entry, completion, BuildCommand(descriptor, job, entry.Fire, completion, retry), status);
    }

    /// <summary>
    /// What follows <paramref name="write"/>'s answer: only the wakes when the row no longer matched (NotOwner,
    /// AlreadyTerminal), since the entry can outlive its lease while it waits; otherwise the wakes, descendant cancel,
    /// and metric its final state calls for, none of them for a hand-back, since every claim loop re-polls within its
    /// delay. A retirement cancels no descendants, as the retire itself does not. With <paramref name="apart"/> the
    /// descendant cancel runs off the caller.
    /// </summary>
    private async Task<RunOnceStatus> LandAsync(PlannedWrite write, CompleteExecutionsRow complete, bool apart, CancellationToken hostCt)
    {
        var (entry, completion, _, status) = write;
        var job = entry.Claim;
        var ns = context.WorkerRegistration!.NamespaceName;
        var landed = complete.Action == CompleteExecutionsAction.Completed;
        var retired = completion.Reason == EventReason.DefinitionRetired;
        if (!landed)
        {
            // An external cancel, cascade, or stolen lease moved the row out of execution, or a first try
            // committed and lost its answer, so the compare-and-swap completed no work. The answer still carries
            // the row's state, so its wakes go out: a lost answer sent none, and one for a moved row costs a poll.
            log.LogInformation(
                "WorkerRuntime: ({Namespace}) job {JobId} completion skipped: ({Outcome}).",
                ns,
                job.JobId,
                complete.Action.ToString()
            );
        }

        if (entry.Descriptor is not null || retired)
        {
            try
            {
                await WakeAsync(complete, job, ns, hostCt);

                // Handler cancel: cancel the descendant subtree as follow-up transactions (the completion
                // above already raised this job's own latch in-routine). A first try that committed and lost its
                // answer reads back Cancelled, and the walk is idempotent, so it runs then too; a crash before the
                // walk leaves the descendants to an operator cancel of this job, which walks them again.
                if (
                    completion.HandlerStatusCode == JobStatusCode.Cancelled
                    && !retired
                    && (landed || complete.FinalJobStatusCode == (byte)JobStatusCode.Cancelled)
                )
                {
                    if (apart)
                    {
                        CancelDescendantsApart(job.JobId, hostCt);
                    }
                    else
                    {
                        await WalkDescendantsAsync(job.JobId, hostCt);
                    }
                }

                // The reason tag is the attempt's EventReason kebab (null on a plain success).
                if (landed && entry.Descriptor is { } descriptor)
                {
                    metrics?.RecordExecution(
                        ns,
                        descriptor.JobName,
                        OutcomeTag(completion.Outcome),
                        completion.Reason,
                        completion.DurationMs
                    );
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !hostCt.IsCancellationRequested)
            {
                log.LogError(ex, "WorkerRuntime: follow-ups for job {JobId} failed after its completion landed.", job.JobId);
            }
        }

        if (!landed)
        {
            return RunOnceStatus.NothingClaimed;
        }
        log.LogInformation("WorkerRuntime: ({Namespace}) job {JobId} -> ({Outcome})", ns, job.JobId, status.ToString());
        return status;
    }

    /// <summary>
    /// Cancels <paramref name="jobId"/>'s descendants off the loop, since a subtree is one transaction per descendant
    /// and the completions behind it would wait; a drain waits for it.
    /// </summary>
    private void CancelDescendantsApart(long jobId, CancellationToken hostCt)
    {
        lock (_cancels)
        {
            _cancels.RemoveAll(cancel => cancel.IsCompleted);
            _cancels.Add(CancelDescendantsAsync(jobId, hostCt));
        }
    }

    private async Task CancelDescendantsAsync(long jobId, CancellationToken hostCt)
    {
        await Task.Yield();
        try
        {
            await WalkDescendantsAsync(jobId, hostCt);
        }
        catch (Exception) when (hostCt.IsCancellationRequested)
        {
            // The walk repeats until it finishes, so only a hard stop ends it early; the rest run until an
            // operator cancels the job again.
        }
    }

    /// <summary>
    /// Walks <paramref name="jobId"/>'s descendants, running a walk that fails again from the root until one finishes or
    /// the worker stops: nothing else cancels the rest of a tree whose root its own handler cancelled.
    /// </summary>
    private async Task WalkDescendantsAsync(long jobId, CancellationToken hostCt) =>
        await RepeatAsync(
            async token =>
            {
                await jobs.CancelDescendantsAsync(jobId, token);
                return true;
            },
            log,
            jobId,
            hostCt,
            firstDelay: RepeatFirstDelay
        );

    /// <summary>
    /// Repeats a write an execution cannot afford to lose until it lands or the worker stops: an abandoned completion
    /// leaves the row Executing until its lease lapses, and recovery then charges a failure and runs the job again.
    /// The write is a compare-and-swap, so repeating is safe, and every failure is repeated, since a defect that loops
    /// loudly costs less than a finished execution charged and run twice. The token bounds only the waits between tries;
    /// <paramref name="tries"/> bounds the tries for a caller with a fallback, and the last failure is rethrown.
    /// </summary>
    internal static async Task<T> RepeatAsync<T>(
        Func<CancellationToken, Task<T>> write,
        ILogger log,
        long jobId,
        CancellationToken ct,
        ActaMetrics? metrics = null,
        TimeSpan? firstDelay = null,
        int tries = int.MaxValue
    )
    {
        // A zero first delay is the test seam: the curve stays at zero however many tries it takes.
        var backoff = firstDelay is { } first
            ? Backoff.Exponential(first, first > RepeatBackoff.MaxDelay ? first : RepeatBackoff.MaxDelay)
            : RepeatBackoff;
        var counted = false;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await write(ct);
                }
                // A cancelled token is the only exit that is not a landed write: the worker is stopping,
                // so whatever the write threw propagates as it is and the row falls to recovery.
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (attempt >= tries)
                    {
                        throw;
                    }

                    var delay = TimeSpan.FromSeconds(BackoffSchedule.ComputeDelaySeconds(attempt, backoff));
                    // Diagnostics cannot be allowed to end the repeat: a metrics listener or a logger that
                    // throws would abandon the row the repeat exists to keep.
                    try
                    {
                        if (!counted)
                        {
                            counted = true;
                            metrics?.RecordUnsettledCompletion(1);
                        }
                        log.Log(
                            attempt <= WarningTries ? LogLevel.Warning : LogLevel.Error,
                            ex,
                            "WorkerRuntime: a database call for job {JobId} failed on try {Count}; retrying in {DurationMs}ms until it lands or this worker stops.",
                            jobId,
                            attempt,
                            (long)delay.TotalMilliseconds
                        );
                    }
                    catch (Exception)
                    {
                        // Swallowed on purpose; the write's own failure is the one being handled.
                    }

                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Stopped mid-wait: report the write's own failure, not the stop. The stop says why
                        // the repeat ended; the failure says why the row is still Executing.
                        ExceptionDispatchInfo.Capture(ex).Throw();
                    }
                }
            }
        }
        finally
        {
            if (counted)
            {
                try
                {
                    metrics?.RecordUnsettledCompletion(-1);
                }
                catch (Exception)
                {
                    // A listener that throws must not turn a landed write into a failure.
                }
            }
        }
    }

    /// <summary>
    /// Publishes on the FINAL state the routine reports, never the category the caller guessed: a signal that arrived
    /// Set while the handler ran lands the job Ready inside complete_executions, which no other publish site sees. A
    /// final Ready wakes the namespace's claim loops (due now is WorkAvailable, a run time ahead of db_now is
    /// HorizonChanged so sleeping loops re-read their horizon); a final terminal status wakes the job's completion
    /// channel for a colocated RunAndWaitAsync caller.
    /// </summary>
    private async Task WakeAsync(CompleteExecutionsRow complete, ClaimedJob job, string ns, CancellationToken ct)
    {
        if (complete.FinalJobStatusCode == (byte)JobStatusCode.Ready)
        {
            var wakeReason =
                complete.FinalNextRunAtUtc is { } nextRun && nextRun > complete.DbNowUtc
                    ? WorkerWakeupReason.HorizonChanged
                    : WorkerWakeupReason.WorkAvailable;
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(ns), wakeReason, ct);
        }
        else if (complete.FinalJobStatusCode is { } finalStatus && ((JobStatusCode)finalStatus).IsTerminal)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.JobCompletion(job.JobId), WorkerWakeupReason.JobFinished, ct);
        }

        // The completion handed a lane to its next member inside complete_executions; lanes are namespace-scoped,
        // so the member is claimable in this namespace.
        if (complete.LanePromoted)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(ns), WorkerWakeupReason.WorkAvailable, ct);
        }

        // A terminal child's raise flipped its Suspended parent to Ready inside complete_executions;
        // the routine knows only the parent's numeric namespace id, so every worker namespace hears it.
        if (complete.ParentReleased)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }
    }

    /// <summary>One entry's write as planned: the completion after the deadline guard, its batch row, and its status.</summary>
    private readonly record struct PlannedWrite(
        InFlightJob Entry,
        ExecutionCompletion Completion,
        CompleteExecutionsValues Row,
        RunOnceStatus Status
    );
}
