using System.Globalization;
using System.Text;

namespace Acta;

public abstract partial class JobContext
{
    /// <summary>
    /// Durable, replay-safe wait for a child started by
    /// <see cref="StartChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/> to reach
    /// a terminal status. Returns the child's terminal outcome immediately when it already finished;
    /// otherwise suspends this Job (budget-neutral) until the child's terminal landing releases it.
    /// Never throws on a failed or cancelled child; branch on <see cref="ChildJobOutcome.Succeeded"/>.
    /// The outcome latch lives on this Job and survives the child row's retention purge.
    /// </summary>
    public async Task<ChildJobOutcome> WaitChildAsync(long childJobId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childJobId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(ChildLatchName(childJobId), linked.Token);
        return ParseChildOutcome(childJobId, outcome);
    }

    /// <summary>
    /// Bounded twin of <see cref="WaitChildAsync"/>. The first pass stores <c>db_now + timeout</c> (whole seconds,
    /// rounded up, positive) as the latch's expiration; a replay reuses that instant and never extends it. A child
    /// landing before the replayed wait settles the latch is still taken. A timeout never cancels this Job: the handler
    /// resumes with <see cref="ChildWaitOutcome.TimedOut"/> after Acta cancels the unfinished child and its subtree
    /// (<c>job.wait-timed-out</c>), free to compensate, start a replacement, or cancel itself. There is no non-Try twin:
    /// a child timeout resolves in the parent's favour, so a non-returning overload would mean nothing.
    /// </summary>
    public async Task<ChildWaitOutcome> TryWaitChildAsync(long childJobId, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childJobId);
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(ChildLatchName(childJobId), seconds, resumeOnTimeout: true, linked.Token);
        return await ResolveChildWaitAsync(childJobId, outcome, linked.Token);
    }

    /// <summary>
    /// A landed child's outcome, or, for a timed-out wait, the child cancelled first. The parent has stopped waiting,
    /// so the child's work is no longer wanted: cancelling before the result is handed back means the handler never
    /// sees TimedOut while the subtree is still burning workers, and a replay re-runs a cancel that is a no-op on
    /// terminal rows.
    /// </summary>
    private async Task<ChildWaitOutcome> ResolveChildWaitAsync(long childJobId, SignalSlotState outcome, CancellationToken ct)
    {
        if (!outcome.TimedOut)
        {
            return ChildWaitOutcome.Landed(ParseChildOutcome(childJobId, outcome));
        }

        await CancelTimedOutChildCoreAsync(childJobId, ct);
        return ChildWaitOutcome.Expired(childJobId);
    }

    /// <summary>
    /// Read side of the child-latch checkpoint key. The name is persisted in the ledger and matched
    /// as text against the one RaiseChildLatch writes, in another process and possibly another
    /// culture, so the two renderings must agree byte for byte; the invariant culture is stated on
    /// both sides rather than inherited from whatever the ambient one happens to be.
    /// </summary>
    private static string ChildLatchName(long childJobId) => ChildSignalPrefix + childJobId.ToString(CultureInfo.InvariantCulture);

    private static ChildJobOutcome ParseChildOutcome(long childJobId, SignalSlotState outcome) =>
        outcome.Value is null
            ? throw new InvalidOperationException($"Child outcome slot for job {childJobId} carries no envelope.")
            : ChildOutcomeEnvelope.Parse(outcome.Value);

    /// <summary>
    /// Subclass sink: cancel the timed-out <paramref name="childJobId"/> and its non-terminal
    /// descendant subtree, so a wait the parent abandoned does not leave orphaned work running. Must be
    /// idempotent: a replayed handler re-enters the expired wait and calls this again, and a cancel of
    /// an already-terminal job is a no-op. The default does nothing, so a <see cref="JobContext"/>
    /// subclass that only records calls keeps working.
    /// </summary>
    private protected virtual Task CancelTimedOutChildCoreAsync(long childJobId, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Waits for every listed child to reach a terminal status (all-of), suspending as needed. Outcomes
    /// are returned in the order given. Replay-safe: finished children resolve from their latches
    /// without suspending.
    /// </summary>
    public async Task<IReadOnlyList<ChildJobOutcome>> WaitChildrenAsync(IReadOnlyList<long> childJobIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(childJobIds);
        var outcomes = new ChildJobOutcome[childJobIds.Count];
        for (var i = 0; i < childJobIds.Count; i++)
        {
            outcomes[i] = await WaitChildAsync(childJobIds[i], ct);
        }
        return outcomes;
    }

    /// <summary>
    /// Bounded twin of <see cref="WaitChildrenAsync(IReadOnlyList{long}, CancellationToken)"/>, the same call as
    /// <see cref="TryWaitChildrenAsync"/>. It answers a <see cref="ChildrenWaitOutcome"/> rather than the unbounded
    /// form's plain list, because a group that ran out of time has more to report than an outcome per child.
    /// </summary>
    /// <param name="childJobIds">The children to wait for, in the order the outcomes come back.</param>
    /// <param name="timeout">Budget for the whole group from DB now, not per child. Must be positive.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task<ChildrenWaitOutcome> WaitChildrenAsync(IReadOnlyList<long> childJobIds, TimeSpan timeout, CancellationToken ct = default) =>
        TryWaitChildrenAsync(childJobIds, timeout, ct);

    /// <summary>
    /// Durable, replay-safe bounded wait for a whole group of children. The first pass stores <c>db_now + timeout</c>
    /// once as the group's deadline (whole seconds, positive, for the group rather than per child), and every child on
    /// every replay waits toward that one instant: the budget never restarts, except after <see cref="ResetStateAsync"/>,
    /// which clears the stored deadline with every other checkpoint. Children that landed keep their outcome; on expiry
    /// Acta cancels only the unfinished ones and their subtrees (<c>job.wait-timed-out</c>) and this Job resumes with
    /// the result. There is no non-Try twin, for the reason <see cref="TryWaitChildAsync"/> has none.
    /// </summary>
    public async Task<ChildrenWaitOutcome> TryWaitChildrenAsync(
        IReadOnlyList<long> childJobIds,
        TimeSpan timeout,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(childJobIds);
        // Every argument is checked before the first store call, and a bad id anywhere in the list stops
        // the whole call, so a rejected group never leaves a deadline slot or a half-armed latch behind.
        ToWaitTimeoutSeconds(timeout);
        for (var i = 0; i < childJobIds.Count; i++)
        {
            if (childJobIds[i] <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(childJobIds), childJobIds[i], "Child job ids must be positive.");
            }
        }

        if (childJobIds.Count == 0)
        {
            // No group, so no deadline to persist: an empty wait is over before it starts.
            return ChildrenWaitOutcome.From([]);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return ChildrenWaitOutcome.From(await WaitChildGroupAsync(childJobIds, timeout, linked.Token));
    }

    /// <summary>
    /// The one home of the group deadline rule; every bounded group API funnels through it, and a null timeout is the
    /// unbounded loop. Every member's wait is bounded by the group's stored instant itself, so each latch expires at
    /// that instant whenever it is armed, and a member first reached after the instant has passed times out in the same
    /// pass as the rest instead of arming a fresh bound and suspending the parent once per member.
    /// </summary>
    private async Task<IReadOnlyList<ChildJobOutcome>> WaitChildGroupAsync(
        IReadOnlyList<long> childJobIds,
        TimeSpan? timeout,
        CancellationToken ct
    )
    {
        if (timeout is not { } bound)
        {
            return await WaitChildrenAsync(childJobIds, ct);
        }

        var deadlineAtUtc = await GetOrSetWaitDeadlineCoreAsync(GroupDeadlineName(childJobIds), bound, ct);

        var outcomes = new ChildJobOutcome[childJobIds.Count];
        for (var i = 0; i < childJobIds.Count; i++)
        {
            var latch = await WaitSignalUntilCoreAsync(ChildLatchName(childJobIds[i]), deadlineAtUtc, ct);
            var result = await ResolveChildWaitAsync(childJobIds[i], latch, ct);
            outcomes[i] = result.Outcome ?? ChildJobOutcome.Expired(childJobIds[i]);
        }
        return outcomes;
    }

    /// <summary>
    /// Reserved deadline-slot name: the child ids hashed to a stable tail, under the <c>sys.</c> prefix user variables
    /// cannot take. The ids come back identical on every replay (a child start dedupes onto the same row), so the
    /// name is stable, and two waits on the same children in one Job share the deadline on purpose. The ids are
    /// sorted and de-duplicated first, so the name is a property of the set: reordering them, or listing one twice on
    /// one replay, would otherwise mint a second slot and a fresh budget, making never-restart a matter of caller
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
    /// Internal rather than private so the test host can find the slot it has to rewind to stage a
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
