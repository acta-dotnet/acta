using System.Globalization;
using System.Text;

namespace Acta;

public abstract partial class JobContext
{
    /// <summary>
    /// Durable, replay-safe wait for a child started by
    /// <see cref="StartChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>, given
    /// what the start answered, to reach a terminal status. Returns the child's terminal outcome immediately when it
    /// already finished; otherwise suspends this Job (budget-neutral) until the child's terminal landing releases it.
    /// Never throws on a failed or cancelled child; branch on <see cref="JobOutcome.Succeeded"/>.
    /// The outcome latch lives on this Job and survives the child row's retention purge.
    /// </summary>
    public async Task<JobOutcome> WaitChildAsync(EnqueueJobResponse child, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(WaitChildAsync));
        ValidateChild(child, nameof(child));
        return (await WaitChildCoreAsync(child, timeout: null, ct)).Outcome;
    }

    /// <summary>
    /// Bounded twin of <see cref="WaitChildAsync"/>. The first pass stores <c>db_now + timeout</c> (whole seconds,
    /// rounded up, positive) as the latch's expiration; a replay reuses that instant and never extends it. A child
    /// landing before the replayed wait settles the latch is still taken. A timeout never cancels this Job: the handler
    /// resumes with <see cref="JobOutcome.TimedOut"/> after Acta cancels the unfinished child and its subtree
    /// (<c>wait-timed-out</c>), free to compensate, start a replacement, or cancel itself. There is no non-Try twin:
    /// a child timeout resolves in the parent's favour, so a non-returning overload would mean nothing.
    /// </summary>
    public async Task<JobOutcome> TryWaitChildAsync(EnqueueJobResponse child, TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(TryWaitChildAsync));
        ValidateChild(child, nameof(child));
        return (await WaitChildCoreAsync(child, timeout, ct)).Outcome;
    }

    /// <summary>
    /// Waits one child's latch, under <paramref name="timeout"/> when one is given, and answers its outcome beside the
    /// execution whose landing set the latch, whose result the typed wait reads; null when the latch names none or the
    /// wait timed out.
    /// </summary>
    private async Task<(JobOutcome Outcome, long? ExecutionNumber)> WaitChildCoreAsync(
        EnqueueJobResponse child,
        TimeSpan? timeout,
        CancellationToken ct
    )
    {
        int? seconds = timeout is { } bound ? ToWaitTimeoutSeconds(bound) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var latch = seconds is null
            ? await WaitSignalCoreAsync(ChildLatchName(child.JobId), linked.Token)
            : await WaitSignalCoreAsync(ChildLatchName(child.JobId), seconds, resumeOnTimeout: true, linked.Token);
        return await ResolveChildWaitAsync(child, latch, linked.Token);
    }

    /// <summary>
    /// A landed child's outcome, or, for a timed-out wait, the child cancelled first. The parent has stopped waiting,
    /// so the child's work is no longer wanted: cancelling before the outcome is handed back means the handler never
    /// sees TimedOut while the subtree is still burning workers, and a replay re-runs a cancel that is a no-op on
    /// terminal rows. A child that landed terminal in the same instant as the expiry keeps the status it reached, while
    /// its outcome still reports Cancelled beside the timeout flag.
    /// </summary>
    private async Task<(JobOutcome Outcome, long? ExecutionNumber)> ResolveChildWaitAsync(
        EnqueueJobResponse child,
        SignalCheckpointState latch,
        CancellationToken ct
    )
    {
        if (!latch.TimedOut)
        {
            var (status, executionNumber) = ChildOutcomeEnvelope.Parse(
                latch.Value ?? throw new InvalidOperationException($"Child latch for job {child.JobId} carries no envelope.")
            );
            return (JobOutcome.Landed(child.JobId, child.JobRef, status), executionNumber);
        }

        await CancelTimedOutChildCoreAsync(child.JobId, ct);
        return (JobOutcome.Expired(child.JobId, child.JobRef, JobStatusCode.Cancelled), null);
    }

    /// <summary>
    /// Read side of the child-latch checkpoint key. The name is persisted in the ledger and matched
    /// as text against the one RaiseChildLatch writes, in another process and possibly another
    /// culture, so the two renderings must agree byte for byte; the invariant culture is stated on
    /// both sides rather than inherited from whatever the ambient one happens to be.
    /// </summary>
    private static string ChildLatchName(long childJobId) => ChildSignalPrefix + childJobId.ToString(CultureInfo.InvariantCulture);

    private static void ValidateChild(EnqueueJobResponse child, string paramName)
    {
        ArgumentNullException.ThrowIfNull(child, paramName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(child.JobId, paramName);
    }

    /// <summary>
    /// Subclass sink: cancel the timed-out <paramref name="childJobId"/> and its non-terminal
    /// descendant subtree, so a wait the parent abandoned does not leave orphaned work running. Must be
    /// idempotent: a replayed handler re-enters the expired wait and calls this again, and a cancel of
    /// an already-terminal job is a no-op. The default does nothing, so a <see cref="JobContext"/>
    /// subclass that only records calls keeps working.
    /// </summary>
    private protected virtual Task CancelTimedOutChildCoreAsync(long childJobId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// The one home of the group deadline rule; every group API funnels through it, and a null timeout is the unbounded
    /// loop. Every member's wait is bounded by the group's stored instant itself, so each latch expires at that instant
    /// whenever it is armed, and a member first reached after the instant has passed times out in the same pass as the
    /// rest instead of arming a fresh bound and suspending the parent once per member.
    /// </summary>
    private async Task<IReadOnlyList<JobOutcome>> WaitChildGroupAsync(
        IReadOnlyList<EnqueueJobResponse> children,
        TimeSpan? timeout,
        CancellationToken ct
    )
    {
        var outcomes = new JobOutcome[children.Count];
        if (timeout is not { } bound)
        {
            for (var i = 0; i < children.Count; i++)
            {
                outcomes[i] = (await WaitChildCoreAsync(children[i], timeout: null, ct)).Outcome;
            }
            return outcomes;
        }

        // Whole seconds, rounded up, as a single child's bound is: a fraction of a second would be spent by the round
        // trip that stores it.
        var deadlineAtUtc = await GetOrSetWaitDeadlineCoreAsync(
            GroupDeadlineName([.. children.Select(child => child.JobId)]),
            TimeSpan.FromSeconds(ToWaitTimeoutSeconds(bound)),
            ct
        );

        for (var i = 0; i < children.Count; i++)
        {
            var latch = await WaitSignalUntilCoreAsync(ChildLatchName(children[i].JobId), deadlineAtUtc, ct);
            outcomes[i] = (await ResolveChildWaitAsync(children[i], latch, ct)).Outcome;
        }
        return outcomes;
    }

    /// <summary>
    /// Reserved deadline checkpoint name: the child ids hashed to a stable tail, under the <c>sys.</c> prefix user variables
    /// cannot take. The ids come back identical on every replay (a child start dedupes onto the same row), so the
    /// name is stable, and two waits on the same children in one Job share the deadline on purpose. The ids are
    /// sorted and de-duplicated first, so the name is a property of the set: reordering them, or listing one twice on
    /// one replay, would otherwise mint a second deadline and a fresh budget, making never-restart a matter of caller
    /// discipline. Caller order survives in the outcome array.
    /// </summary>
    internal static string GroupDeadlineName(IReadOnlyList<long> childJobIds)
    {
        var ordered = new long[childJobIds.Count];
        for (var i = 0; i < childJobIds.Count; i++)
        {
            ordered[i] = childJobIds[i];
        }
        Array.Sort(ordered);

        var canonical = new StringBuilder();
        long? previous = null;
        foreach (var id in ordered)
        {
            if (id != previous)
            {
                canonical.Append(id.ToString(CultureInfo.InvariantCulture)).Append('.');
            }
            previous = id;
        }
        return GroupDeadlinePrefix + ShortHash(canonical.ToString());
    }

    /// <summary>
    /// Internal rather than private so the test host can find the checkpoint it has to rewind to stage a
    /// group expiry, off the one name that writes it.
    /// </summary>
    internal const string GroupDeadlinePrefix = "sys.wait-group.";

    /// <summary>
    /// Rejects a bad timeout before the first enqueue: a wrapper starts every child before it
    /// waits on any of them, so a rejection at wait time would leave enqueued children nothing is
    /// going to wait for. A null timeout is the unbounded overload and has nothing to check.
    /// </summary>
    private static void ValidateGroupTimeout(TimeSpan? timeout)
    {
        if (timeout is { } bound)
        {
            ToWaitTimeoutSeconds(bound);
        }
    }

    /// <summary>
    /// Subclass sink: read the group's stored absolute deadline, writing <c>db_now + timeout</c> on the
    /// first call and returning the stored instant unchanged on every call after it (first-write-wins,
    /// the <see cref="GetOrSetVariableAsync{T}(string, Func{T}, CancellationToken)"/> shape). The default
    /// computes an instant from the host clock and stores nothing, so a <see cref="JobContext"/> subclass
    /// without durable storage keeps working; only a durable implementation makes the deadline survive a replay.
    /// </summary>
    private protected virtual Task<DateTime> GetOrSetWaitDeadlineCoreAsync(string name, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(DateTime.UtcNow + timeout);
}
