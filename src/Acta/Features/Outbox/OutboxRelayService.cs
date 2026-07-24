using Acta.Features.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta.Features.Outbox;

/// <summary>
/// Provider-neutral relay policy for one <c>sys.outbox</c> tick: claim due source rows in bounded
/// batches, coalesce duplicate handoffs, enqueue representatives through the owned batch path, then
/// finalize under the claim token. Retry, per-group rejection isolation, quarantine, and the single
/// bounded tick summary all live here; the source persistence is the injected <see cref="IOutboxRelayStore"/>
/// and the target is the normal <see cref="IJobs.EnqueueBatchAsync(IReadOnlyList{JobEnqueueRequest}, CancellationToken)"/>.
/// </summary>
internal sealed class OutboxRelayService(IOutboxRelayStore store, IOutboxTarget target, ILogger<OutboxRelayService>? log = null)
{
    // Per tick: at most MaxBatches source claims of BatchSize rows. The next tick continues any backlog.
    private const int MaxBatches = 20;
    private const int BatchSize = 256;

    // One shared per-tick work budget, measured in target-enqueue attempts. Both source claims (each of
    // which makes at least one attempt) and the per-group retries of a rejected batch draw it down, so
    // isolating a rejected batch is work inside the tick bound rather than a way to bypass it. The cap
    // equals the 5,120-row envelope (MaxBatches * BatchSize): the happy path of 20 non-rejecting batches
    // spends only 20 attempts, while a pathological all-rejecting workload releases its unprocessed
    // remainder once the envelope is spent instead of running unbounded retries past the 180s source lease.
    private const int MaxTargetEnqueues = MaxBatches * BatchSize;

    private static readonly Backoff RowBackoff = Backoff.Default;

    private readonly ILogger _log = log ?? NullLogger<OutboxRelayService>.Instance;

    private enum GroupOutcome
    {
        Safe,
        Recoverable,
    }

    private sealed record OutboxGroup(OutboxRow Representative, IReadOnlyList<OutboxRow> Rows)
    {
        // Materialized once at construction (read on both the finalize and quarantine paths).
        public IReadOnlyList<Guid> Ids { get; } = Rows.Select(r => r.OutboxId).ToList();
    }

    // Mutable per-tick target-enqueue allowance shared across every batch and its per-group retries.
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
    /// Runs one relay tick. Infrastructure failures and any quarantine transition fail the tick (so the
    /// system-job alert path fires); recoverable row rejections below the threshold reschedule quietly.
    /// </summary>
    public async Task RunTickAsync(OutboxRelayTickOptions options, CancellationToken ct)
    {
        // No pre-flight shape check: the table comes from the tested DDL API, so an incompatible table
        // surfaces as a claim/finalize SQL error, an infrastructure failure that fails only this tick.
        var token = Guid.NewGuid();
        var quarantinedIds = new List<Guid>();
        var budget = new TickBudget(MaxTargetEnqueues);

        for (var batch = 0; batch < MaxBatches && budget.Remaining > 0; batch++)
        {
            var claimed = await store.ClaimDueAsync(new ClaimOutboxCommand(token, BatchSize, options.LeaseTtlSeconds), ct);
            if (claimed.Count == 0)
            {
                break;
            }

            bool budgetExhausted;
            try
            {
                budgetExhausted = await ProcessBatchAsync(claimed, token, options, quarantinedIds, budget, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await ReleaseBestEffortAsync(token, claimed.Select(r => r.OutboxId).ToList(), ct);
                throw;
            }

            if (budgetExhausted || claimed.Count < BatchSize)
            {
                break;
            }
        }

        if (quarantinedIds.Count > 0)
        {
            _log.LogError(
                "ACTA sys.outbox: source '{Source}' quarantined {Count} row(s) this tick: [{Ids}].",
                options.SourceName,
                quarantinedIds.Count,
                OutboxQuarantineTickException.FormatSample(quarantinedIds)
            );
            throw new OutboxQuarantineTickException(options.SourceName, quarantinedIds);
        }
    }

    // Processes one claimed batch. Returns true when the per-tick target-enqueue budget was exhausted
    // mid-batch: the unprocessed remainder was released and the tick must end.
    private async Task<bool> ProcessBatchAsync(
        IReadOnlyList<OutboxRow> claimed,
        Guid token,
        OutboxRelayTickOptions options,
        List<Guid> quarantinedIds,
        TickBudget budget,
        CancellationToken ct
    )
    {
        var groups = Coalesce(claimed);

        var immediateQuarantine = new List<(OutboxGroup Group, string Error)>();
        var valid = new List<(JobEnqueueRequest Request, OutboxGroup Group)>();
        foreach (var group in groups)
        {
            try
            {
                valid.Add((OutboxRequestReconstruction.ToRequest(group.Representative, options.MaxInlinePayloadBytes), group));
            }
            catch (OutboxContractException ex)
            {
                immediateQuarantine.Add((group, ex.Message));
            }
        }

        var results = new Dictionary<OutboxGroup, GroupOutcome>();
        var errors = new Dictionary<OutboxGroup, string>();
        try
        {
            await EnqueueGroupsAsync(valid, results, errors, budget, ct);
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
            await ReleaseBestEffortAsync(token, claimed.Select(r => r.OutboxId).ToList(), ct);
            _log.LogWarning(
                ex,
                "ACTA sys.outbox: source '{Source}' target enqueue failed; releasing claim and failing tick.",
                options.SourceName
            );
            throw;
        }

        foreach (var (group, error) in immediateQuarantine)
        {
            // Malformed/oversize is a pre-target contract failure: quarantine at once, leaving each row's
            // existing failure count untouched (no retry budget was consumed).
            await store.QuarantineAsync(
                new QuarantineOutboxCommand(
                    token,
                    group.Rows.Select(r => new OutboxQuarantine(r.OutboxId, r.FailureCount, error)).ToList()
                ),
                ct
            );
            quarantinedIds.AddRange(group.Ids);
        }

        var reschedules = new List<OutboxReschedule>();
        foreach (var (group, outcome) in results)
        {
            if (outcome == GroupOutcome.Safe)
            {
                await store.DeleteClaimedAsync(new FinalizeOutboxCommand(token, group.Ids), ct);
                continue;
            }

            var error = errors.GetValueOrDefault(group, "recoverable target rejection");
            // Retry/quarantine state is row-specific and monotonic: each claimed row of the group advances
            // its OWN failure count, so a group can partially quarantine (over-threshold rows quarantine,
            // under-threshold rows reschedule) rather than inheriting one representative's count.
            var quarantines = new List<OutboxQuarantine>();
            foreach (var row in group.Rows)
            {
                var failureCount = row.FailureCount + 1;
                if (failureCount >= options.QuarantineThreshold)
                {
                    quarantines.Add(new OutboxQuarantine(row.OutboxId, failureCount, error));
                }
                else
                {
                    reschedules.Add(
                        new OutboxReschedule(
                            row.OutboxId,
                            failureCount,
                            BackoffSchedule.ComputeDelaySeconds(failureCount, RowBackoff),
                            error
                        )
                    );
                }
            }

            if (quarantines.Count > 0)
            {
                await store.QuarantineAsync(new QuarantineOutboxCommand(token, quarantines), ct);
                quarantinedIds.AddRange(quarantines.Select(q => q.OutboxId));
            }

            _log.LogInformation(
                "ACTA sys.outbox: source '{Source}' row group ({Namespace}/{DedupKey}) rejected; {Rescheduled} rescheduled, {Quarantined} quarantined.",
                options.SourceName,
                group.Representative.JobNamespace,
                group.Representative.DeduplicationKey,
                group.Rows.Count - quarantines.Count,
                quarantines.Count
            );
        }

        if (reschedules.Count > 0)
        {
            await store.RescheduleAsync(new RescheduleOutboxCommand(token, reschedules), ct);
        }

        // Any valid group whose target enqueue never ran (budget exhausted mid-batch) is unprocessed:
        // release its claimed rows so the next tick continues the backlog, and end this tick.
        var unprocessed = valid.Where(v => !results.ContainsKey(v.Group)).SelectMany(v => v.Group.Ids).ToList();
        if (unprocessed.Count > 0)
        {
            await ReleaseBestEffortAsync(token, unprocessed, ct);
            return true;
        }

        return false;
    }

    // The whole batch goes to the target in one round trip; a deterministic batch rejection falls back to
    // one target call per group so a single offending group is isolated while the rest still ingest. Every
    // attempt draws down the shared tick budget; when it is spent the remaining groups are left unresolved
    // (released by the caller). Infrastructure exceptions propagate.
    private async Task EnqueueGroupsAsync(
        IReadOnlyList<(JobEnqueueRequest Request, OutboxGroup Group)> groups,
        Dictionary<OutboxGroup, GroupOutcome> results,
        Dictionary<OutboxGroup, string> errors,
        TickBudget budget,
        CancellationToken ct
    )
    {
        if (groups.Count == 0)
        {
            return;
        }

        var (exhausted, batchError) = await TryEnqueueAsync(groups, budget, ct);
        if (exhausted)
        {
            return;
        }
        if (batchError is null)
        {
            MarkSafe(groups, results);
            return;
        }
        if (groups.Count == 1)
        {
            // A single group cannot be isolated further: the rejection is its own.
            results[groups[0].Group] = GroupOutcome.Recoverable;
            errors[groups[0].Group] = batchError;
            return;
        }

        // Deterministic batch rejection: retry each group on its own. A budget exhausted mid-loop leaves
        // the remaining groups unresolved (absent from results), so the caller releases them.
        foreach (var pair in groups)
        {
            var (groupExhausted, groupError) = await TryEnqueueAsync([pair], budget, ct);
            if (groupExhausted)
            {
                return;
            }
            if (groupError is null)
            {
                results[pair.Group] = GroupOutcome.Safe;
            }
            else
            {
                results[pair.Group] = GroupOutcome.Recoverable;
                errors[pair.Group] = groupError;
            }
        }
    }

    // One target round trip drawing a single unit of the shared budget. Reports (Exhausted: true) when the
    // budget is spent and no call was made, (Exhausted: false, Error: null) on safe ingestion (Inserted or
    // Deduplicated), and the rejection message on a deterministic rejection. Infrastructure faults throw.
    private async Task<(bool Exhausted, string? Error)> TryEnqueueAsync(
        IReadOnlyList<(JobEnqueueRequest Request, OutboxGroup Group)> groups,
        TickBudget budget,
        CancellationToken ct
    )
    {
        if (!budget.TryConsume())
        {
            return (true, null);
        }

        try
        {
            _ = await target.EnqueueBatchAsync(groups.Select(g => g.Request).ToList(), ct);
            return (false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsDeterministicRejection(ex))
        {
            return (false, ex.Message);
        }
    }

    private static void MarkSafe(
        IReadOnlyList<(JobEnqueueRequest Request, OutboxGroup Group)> groups,
        Dictionary<OutboxGroup, GroupOutcome> results
    )
    {
        foreach (var (_, group) in groups)
        {
            results[group] = GroupOutcome.Safe;
        }
    }

    // Group claimed rows by target identity; the earliest (created_at_utc, outbox_id) row is the
    // representative sent to the target, avoiding the ledger's same-batch duplicate-key rejection. The
    // group keeps every member row so retry/quarantine can advance each row's own failure count. The
    // grouping key is case-folded (keys are ASCII by contract) to match the target's dedup normalization,
    // so two case-variant handoffs coalesce into one group instead of colliding at the target.
    private static List<OutboxGroup> Coalesce(IReadOnlyList<OutboxRow> claimed) =>
        claimed
            .GroupBy(r => (r.JobNamespace.ToLowerInvariant(), r.DeduplicationKey.ToLowerInvariant()))
            .Select(g =>
            {
                var ordered = g.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.OutboxId).ToList();
                return new OutboxGroup(ordered[0], ordered);
            })
            .ToList();

    private async Task ReleaseBestEffortAsync(Guid token, IReadOnlyList<Guid> outboxIds, CancellationToken ct)
    {
        try
        {
            await store.ReleaseClaimedAsync(new FinalizeOutboxCommand(token, outboxIds), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "ACTA sys.outbox: best-effort claim release failed; lease expiry and token CAS keep the rows safe.");
        }
    }

    // Deterministic target rejections consume the row's budget (reschedule with backoff, quarantine at
    // threshold): input problems (validation, oversize) plus the ledger's typed routing/target-state
    // rejections (unknown route, suspended namespace/tenant, unknown tenant) surfaced as
    // EnqueueRejectedException. Everything else is infrastructure and retried without consuming budget.
    private static bool IsDeterministicRejection(Exception ex) =>
        ex is ArgumentException or PayloadTooLargeException or EnqueueRejectedException;
}

/// <summary>Per-tick relay configuration resolved from the source registration and worker options.</summary>
internal sealed record OutboxRelayTickOptions(string SourceName, int QuarantineThreshold, int LeaseTtlSeconds, int MaxInlinePayloadBytes);
