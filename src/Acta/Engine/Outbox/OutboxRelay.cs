using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta;

/// <summary>
/// Provider-neutral relay policy for one <c>sys.outbox</c> tick: claim due source rows in bounded
/// batches, coalesce duplicate handoffs, enqueue representatives through the owned batch path, then
/// finalize under the claim token. Retry, per-group rejection isolation, quarantine, and the single
/// bounded tick summary all live here; the source is the injected <see cref="OutboxOperations.Source"/> and the target is
/// the owned batch enqueue, <see cref="IJobs.EnqueueJobsAsync(IReadOnlyList{EnqueueJobRequest}, CancellationToken)"/>.
/// </summary>
internal sealed class OutboxRelay(OutboxOperations.Source source, JobsOperations jobs, ILogger<OutboxRelay>? log = null)
{
    /// <summary>
    /// Per tick: source claims of BatchSize rows, the batch enqueue's limit, until a claim comes back short or the soft
    /// <see cref="TimeBudget"/> is spent; the next tick continues any backlog. The budget is checked between batches, so
    /// the batch in flight always finishes.
    /// </summary>
    private const int BatchSize = 1000;

    /// <summary>
    /// The soft bound on one tick's drain, well inside the 300s execution timeout <c>sys.outbox</c> inherits. Internal
    /// and init-only so a spec can end a tick after one batch without staging a timed backlog; production never sets it.
    /// </summary>
    internal TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The bound source, exposed so operator surfaces resolve reads (quarantine listing) against the same
    /// source the tick drains - the registry's 1:1 namespace-to-source invariant is what makes this the
    /// right source for the slot's parked commands.
    /// </summary>
    internal OutboxOperations.Source Source => source;

    /// <summary>
    /// One shared per-tick work budget, measured in target-enqueue attempts. Source claims (each at least one
    /// attempt) and the per-group retries of a rejected batch both draw it down, so isolating a rejected batch is work
    /// inside the tick bound rather than a way past it. A non-rejecting batch spends one attempt, so only the time budget
    /// bounds the happy path, while an all-rejecting workload spends a batch's worth per batch and releases its
    /// unprocessed remainder once the attempts run out instead of retrying past the 180s source lease.
    /// </summary>
    private const int MaxTargetEnqueues = 5120;

    /// <summary>
    /// Deliberately shorter than Backoff.Default. A job's retry horizon is sized for a human to
    /// read the alert; a stuck outbox row is undelivered product, so once the target recovers the
    /// relay should not sit on a day-long delay before noticing.
    /// </summary>
    private static readonly Backoff RowBackoff = Backoff.Range(TimeSpan.FromMinutes(1), TimeSpan.FromHours(8));

    private readonly ILogger _log = log ?? NullLogger<OutboxRelay>.Instance;

    private sealed record OutboxGroup(ClaimOutboxRow Representative, IReadOnlyList<ClaimOutboxRow> Rows)
    {
        /// <summary>Materialized once at construction (read on both the finalize and release paths).</summary>
        public IReadOnlyList<Guid> Ids { get; } = Rows.Select(r => r.OutboxId).ToList();
    }

    /// <summary>
    /// The one verdict a claimed group's pass reaches, which alone decides its source finalization and its accounting.
    /// Error is the message of a rejection or a reconstruction failure.
    /// </summary>
    private readonly record struct GroupVerdict(OutboxGroup Group, GroupVerdictKind Kind, string? Error = null);

    private enum GroupVerdictKind
    {
        Inserted,
        Deduplicated,
        Rejected,
        Malformed,
        Deferred,
    }

    /// <summary>
    /// Mutable per-tick target-enqueue allowance shared across every batch and its per-group retries.
    /// </summary>
    private sealed class TickBudget(int remaining)
    {
        public int Remaining { get; private set; } = remaining;

        public bool TryConsume()
        {
            if (Remaining <= 0)
            {
                return false;
            }

            Remaining--;
            return true;
        }
    }

    /// <summary>
    /// Runs one relay tick and returns its accounting (persisted as the <c>sys.outbox</c> job result).
    /// Infrastructure failures and any quarantine transition fail the tick (so the system-job alert path
    /// fires); recoverable row rejections below the threshold reschedule quietly.
    /// </summary>
    public async Task<OutboxTickSummary> RunTickAsync(OutboxRelayTickOptions options, CancellationToken ct)
    {
        // No pre-flight shape check: the table comes from the tested DDL API, so an incompatible table
        // surfaces as a claim/finalize SQL error, an infrastructure failure that fails only this tick.
        var token = Guid.NewGuid();
        var quarantinedIds = new List<Guid>();
        var budget = new TickBudget(MaxTargetEnqueues);
        var claimedTotal = 0;
        var relayed = 0;
        var deduplicated = 0;
        var elapsed = Stopwatch.StartNew();

        while (budget.Remaining > 0)
        {
            var claimed = (await source.ClaimAsync(new ClaimOutboxCommand(token, BatchSize, options.LeaseTtlSeconds), ct)).Rows;
            if (claimed.Count == 0)
            {
                break;
            }

            claimedTotal += claimed.Count;
            bool deferred;
            try
            {
                var batch = await ProcessBatchAsync(claimed, token, options, budget, ct);
                relayed += batch.Relayed;
                deduplicated += batch.Deduplicated;
                quarantinedIds.AddRange(batch.Quarantined);
                deferred = batch.Deferred;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await ReleaseBestEffortAsync(token, claimed.Select(r => r.OutboxId).ToList());
                throw;
            }

            // A short batch means the backlog is drained, except that a claim takes one row per lane: the row
            // behind a delivered lane head became claimable only now.
            if (deferred || (claimed.Count < BatchSize && !claimed.Any(static r => r.Lane is not null)))
            {
                break;
            }

            if (elapsed.Elapsed >= TimeBudget)
            {
                break;
            }
        }

        if (quarantinedIds.Count > 0)
        {
            if (_log.IsEnabled(LogLevel.Error))
            {
                ActaLog.OutboxRowsQuarantined(
                    _log,
                    $"{quarantinedIds.Count} row(s), source '{options.SourceName}', ids [{OutboxQuarantineTickException.FormatSample(quarantinedIds)}]"
                );
            }
            throw new OutboxQuarantineTickException(options.SourceName, quarantinedIds);
        }

        // Both totals are read after finalization, so the summary reports what still awaits relay and
        // what currently sits in quarantine - the cross-peer channel operator surfaces read.
        var backlog = await source.CountBacklogAsync(ct);
        var quarantineTotal = await source.CountQuarantinedAsync(ct);
        return new OutboxTickSummary(claimedTotal, relayed, deduplicated, backlog, quarantineTotal);
    }

    /// <summary>
    /// Processes one claimed batch and finalizes each group by its verdict, answering the batch's accounting and the
    /// rows it quarantined. Deferred is true when the per-tick target-enqueue budget ran out mid-batch: the deferred
    /// groups were released and the tick must end.
    /// </summary>
    private async Task<(int Relayed, int Deduplicated, List<Guid> Quarantined, bool Deferred)> ProcessBatchAsync(
        IReadOnlyList<ClaimOutboxRow> claimed,
        Guid token,
        OutboxRelayTickOptions options,
        TickBudget budget,
        CancellationToken ct
    )
    {
        var verdicts = new List<GroupVerdict>();
        var valid = new List<(EnqueueJobRequest Request, OutboxGroup Group)>();
        foreach (var group in Coalesce(claimed))
        {
            try
            {
                valid.Add((OutboxRequestReconstruction.ToRequest(group.Representative, options.MaxInlinePayloadBytes), group));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reconstruction does no I/O, so any throw is deterministic for this row: quarantine the row
                // instead of failing the batch, which the claim order would hand back every tick.
                verdicts.Add(new GroupVerdict(group, GroupVerdictKind.Malformed, ex.Message));
            }
        }

        try
        {
            verdicts.AddRange(await EnqueueGroupsAsync(valid, budget, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Infrastructure/target-availability failure: release the claimed batch best-effort and fail
            // the tick without consuming any row's quarantine budget. Partially-enqueued rows are safe;
            // the next tick re-enqueues them and the target deduplicates.
            await ReleaseBestEffortAsync(token, claimed.Select(r => r.OutboxId).ToList());
            ActaLog.OutboxTargetEnqueueFailed(_log, ex, options.SourceName);
            throw;
        }

        var relayed = 0;
        var deduplicated = 0;
        var safeIds = new List<Guid>();
        var quarantines = new List<OutboxQuarantine>();
        var reschedules = new List<OutboxReschedule>();
        var deferredIds = new List<Guid>();
        foreach (var (group, kind, error) in verdicts)
        {
            switch (kind)
            {
                case GroupVerdictKind.Inserted:
                    // The representative relayed one new job and its coalesced members were absorbed.
                    relayed++;
                    deduplicated += group.Rows.Count - 1;
                    safeIds.AddRange(group.Ids);
                    break;
                case GroupVerdictKind.Deduplicated:
                    deduplicated += group.Rows.Count;
                    safeIds.AddRange(group.Ids);
                    break;
                case GroupVerdictKind.Malformed:
                    // A reconstruction failure is a pre-target contract failure: quarantine at once, leaving each row's
                    // existing failure count untouched (no retry budget was consumed).
                    quarantines.AddRange(group.Rows.Select(r => new OutboxQuarantine(r.OutboxId, r.FailureCount, error!)));
                    break;
                case GroupVerdictKind.Rejected:
                    // Retry/quarantine state is row-specific and monotonic: each claimed row of the group advances
                    // its OWN failure count, so a group can partially quarantine (over-threshold rows quarantine,
                    // under-threshold rows reschedule) rather than inheriting one representative's count.
                    var groupQuarantined = 0;
                    foreach (var row in group.Rows)
                    {
                        var failureCount = row.FailureCount + 1;
                        if (failureCount >= options.QuarantineThreshold)
                        {
                            quarantines.Add(new OutboxQuarantine(row.OutboxId, failureCount, error!));
                            groupQuarantined++;
                        }
                        else
                        {
                            var backoffSeconds = BackoffSchedule.ComputeDelaySeconds(failureCount, RowBackoff);
                            reschedules.Add(new OutboxReschedule(row.OutboxId, failureCount, backoffSeconds, error!));
                        }
                    }

                    if (_log.IsEnabled(LogLevel.Information))
                    {
                        var detail =
                            $"{groupQuarantined} row(s) quarantined, {group.Rows.Count - groupQuarantined} rescheduled, "
                            + $"source '{options.SourceName}', dedup key '{group.Representative.DeduplicationKey}'";
                        ActaLog.OutboxRowGroupRejected(_log, group.Representative.JobNamespace, detail);
                    }
                    break;
                case GroupVerdictKind.Deferred:
                    deferredIds.AddRange(group.Ids);
                    break;
            }
        }

        // Finalization is one store round trip per outcome kind for the whole batch, not one per group.
        if (safeIds.Count > 0)
        {
            await source.RemoveClaimedAsync(new RemoveClaimedOutboxCommand(token, safeIds), ct);
        }

        if (quarantines.Count > 0)
        {
            await source.QuarantineAsync(new QuarantineOutboxCommand(token, quarantines), ct);
        }

        if (reschedules.Count > 0)
        {
            await source.RescheduleAsync(new RescheduleOutboxCommand(token, reschedules), ct);
        }

        // A group whose target enqueue never ran is released so the next tick continues the backlog, and this tick ends.
        if (deferredIds.Count > 0)
        {
            await ReleaseBestEffortAsync(token, deferredIds);
        }

        return (relayed, deduplicated, [.. quarantines.Select(static q => q.OutboxId)], deferredIds.Count > 0);
    }

    /// <summary>
    /// The whole batch goes to the target in one round trip; a deterministic batch rejection falls back to one target
    /// call per group so a single offending group is isolated while the rest still ingest. Every attempt draws down the
    /// shared tick budget, and once it is spent every remaining group is Deferred. Answers one verdict per group, in
    /// order; infrastructure exceptions propagate.
    /// </summary>
    private async Task<List<GroupVerdict>> EnqueueGroupsAsync(
        IReadOnlyList<(EnqueueJobRequest Request, OutboxGroup Group)> groups,
        TickBudget budget,
        CancellationToken ct
    )
    {
        if (groups.Count == 0)
        {
            return [];
        }

        // A single group cannot be isolated further: a rejection of it is its own.
        var verdicts = await TryEnqueueAsync(groups, budget, ct);
        if (groups.Count == 1 || verdicts[0].Kind != GroupVerdictKind.Rejected)
        {
            return verdicts;
        }

        verdicts.Clear();
        foreach (var group in groups)
        {
            verdicts.AddRange(await TryEnqueueAsync([group], budget, ct));
        }
        return verdicts;
    }

    /// <summary>
    /// One target round trip drawing a single unit of the shared budget, answering one verdict per group: all Deferred
    /// when the budget is spent and no call was made, Inserted or Deduplicated per group on safe ingestion, and all
    /// Rejected with the message on a deterministic rejection. Infrastructure faults throw.
    /// </summary>
    private async Task<List<GroupVerdict>> TryEnqueueAsync(
        IReadOnlyList<(EnqueueJobRequest Request, OutboxGroup Group)> groups,
        TickBudget budget,
        CancellationToken ct
    )
    {
        if (!budget.TryConsume())
        {
            return [.. groups.Select(static g => new GroupVerdict(g.Group, GroupVerdictKind.Deferred))];
        }

        try
        {
            // Outcomes align with request order.
            var outcomes = (await jobs.EnqueueJobsAsync(groups.Select(g => g.Request).ToList(), ct)).Jobs;
            var verdicts = new List<GroupVerdict>(groups.Count);
            for (var index = 0; index < groups.Count; index++)
            {
                var inserted = outcomes[index].Action == EnqueueJobAction.Inserted;
                verdicts.Add(new GroupVerdict(groups[index].Group, inserted ? GroupVerdictKind.Inserted : GroupVerdictKind.Deduplicated));
            }
            return verdicts;
        }
        catch (Exception ex) when (IsDeterministicRejection(ex))
        {
            return [.. groups.Select(g => new GroupVerdict(g.Group, GroupVerdictKind.Rejected, ex.Message))];
        }
    }

    /// <summary>
    /// Group claimed rows by target identity; the earliest staged row is the representative sent to the
    /// target, avoiding the ledger's same-batch duplicate-key rejection. Groups keep the claim order.
    /// The group keeps every member row so retry/quarantine can advance each row's own failure
    /// count. The grouping key matches the target's dedup: the namespace folded like every name, the
    /// deduplication key exact, so two handoffs whose keys differ only by case are two jobs.
    /// </summary>
    private static List<OutboxGroup> Coalesce(IReadOnlyList<ClaimOutboxRow> claimed) =>
        [
            .. claimed
                .GroupBy(r => (r.JobNamespace.ToLowerInvariant(), r.DeduplicationKey))
                .Select(g =>
                {
                    var ordered = g.OrderBy(static r => r.Id).ToList();
                    return new OutboxGroup(ordered[0], ordered);
                }),
        ];

    private async Task ReleaseBestEffortAsync(Guid token, IReadOnlyList<Guid> outboxIds)
    {
        try
        {
            // Uncancellable, because the tick's cancellation is the commonest reason to release.
            await source.ReleaseClaimedAsync(new ReleaseClaimedOutboxCommand(token, outboxIds), CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort: lease expiry and the token compare-and-swap keep the rows safe.
        }
    }

    /// <summary>
    /// Deterministic target rejections consume the row's budget (reschedule with backoff,
    /// quarantine at threshold): input problems (validation, oversize) plus the ledger's typed
    /// routing/target-state rejections (unknown route, suspended namespace/tenant, unknown tenant)
    /// surfaced as EnqueueRejectedException. Everything else is infrastructure and retried without
    /// consuming budget.
    /// </summary>
    private static bool IsDeterministicRejection(Exception ex) =>
        ex is ArgumentException or PayloadTooLargeException or EnqueueRejectedException;
}
