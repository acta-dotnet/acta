using Acta.Features.Outbox;
using Xunit;

namespace Acta.Tests.Outbox;

/// <summary>
/// Provider-neutral relay policy proven against an in-memory <see cref="IOutboxRelayStore"/> and a spy
/// target: coalescing, target deduplication, per-group rejection isolation, backoff, quarantine at
/// threshold, immediate malformed quarantine, the bounded tick summary, the 20x256 tick bound, and
/// best-effort cancellation release.
/// </summary>
public sealed class OutboxRelayServiceTests
{
    private static readonly DateTime Now = new(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);

    private static OutboxRelayTickOptions Options(int threshold = 5) => new("orders", threshold, 180, 256 * 1024);

    private static OutboxRow Row(
        string dedup,
        string ns = "orders",
        string job = "process",
        int failureCount = 0,
        DateTime? created = null,
        Guid? id = null,
        byte[]? data = null,
        string? meta = null
    ) =>
        new(
            id ?? Guid.NewGuid(),
            ns,
            job,
            data is null ? (byte)0 : (byte)1,
            data,
            dedup,
            null,
            null,
            null,
            null,
            null,
            null,
            meta,
            created ?? Now,
            failureCount
        );

    [Fact]
    public async Task Duplicate_handoffs_coalesce_to_the_earliest_representative_and_delete_the_group()
    {
        var early = Row("k1", created: Now, id: Guid.NewGuid());
        var late = Row("k1", created: Now.AddSeconds(5), id: Guid.NewGuid());
        var store = new FakeStore(early, late);
        var target = new FakeTarget();
        var svc = new OutboxRelayService(store, target);

        var summary = await svc.RunTickAsync(Options(), TestContext.Current.CancellationToken);

        var batch = Assert.Single(target.Batches);
        var request = Assert.Single(batch);
        Assert.Equal("k1", request.DeduplicationKey);
        Assert.Equal(new HashSet<Guid> { early.OutboxId, late.OutboxId }, store.Deleted.ToHashSet());
        // Two rows claimed, one job inserted, one coalesced member absorbed as a dedup.
        Assert.Equal(new OutboxTickSummary(2, 1, 1, 0, 0), summary);
        Assert.Equal("claimed=2 relayed=1 dedup=1 quarantined=0 backlog=0", summary.ToString());
    }

    [Fact]
    public async Task Case_variant_keys_coalesce_to_one_group_and_delete_together()
    {
        // Keys are ASCII by contract and fold case-insensitively at the target: "ORD-1" and "ord-1" are the
        // same handoff, so they must coalesce into ONE group (one representative, one target entry) and both
        // rows delete. Raw grouping would send two colliding entries and the target would reject the batch.
        var upper = Row("ORD-1", created: Now, id: Guid.NewGuid());
        var lower = Row("ord-1", created: Now.AddSeconds(5), id: Guid.NewGuid());
        var store = new FakeStore(upper, lower);
        var target = new FakeTarget();
        var svc = new OutboxRelayService(store, target);

        await svc.RunTickAsync(Options(), TestContext.Current.CancellationToken);

        var batch = Assert.Single(target.Batches);
        Assert.Single(batch);
        Assert.Equal(new HashSet<Guid> { upper.OutboxId, lower.OutboxId }, store.Deleted.ToHashSet());
    }

    [Fact]
    public async Task Target_deduplication_is_a_safe_outcome_and_deletes_the_group()
    {
        var row = Row("k1");
        var store = new FakeStore(row);
        var target = new FakeTarget { Action = JobEnqueueAction.Deduplicated };
        var svc = new OutboxRelayService(store, target);

        var summary = await svc.RunTickAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Contains(row.OutboxId, store.Deleted);
        Assert.Empty(store.Quarantined);
        // The target already held the handoff: nothing relayed, the whole group counts as deduplicated.
        Assert.Equal(new OutboxTickSummary(1, 0, 1, 0, 0), summary);
    }

    [Fact]
    public async Task A_deterministic_rejection_retries_each_group_individually_isolating_the_bad_group()
    {
        // Three distinct (namespace, dedup) groups; one carries a bad route. The whole-batch attempt is
        // rejected, so the relay retries each group on its own: the two good groups ingest and delete, the
        // bad group reschedules. That is one whole-batch attempt plus three per-group attempts (four target
        // calls), the linear per-group shape - not a binary split, which would make five calls for three.
        var good1 = Row("good1", job: "process");
        var good2 = Row("good2", job: "process");
        var bad = Row("bad", job: "bad");
        var store = new FakeStore(good1, good2, bad);
        // Deterministic rejection whenever the batch contains the "bad" job.
        var target = new FakeTarget { RejectWhen = reqs => reqs.Any(r => r.JobName == "bad") };
        var svc = new OutboxRelayService(store, target);

        var summary = await svc.RunTickAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Contains(good1.OutboxId, store.Deleted);
        Assert.Contains(good2.OutboxId, store.Deleted);
        Assert.DoesNotContain(bad.OutboxId, store.Deleted);
        // The per-group retry arm counts too: two groups relayed, the rejected group counts nothing.
        Assert.Equal(new OutboxTickSummary(3, 2, 0, 0, 0), summary);
        var reschedule = Assert.Single(store.Rescheduled);
        Assert.Equal(bad.OutboxId, reschedule.OutboxId);
        Assert.Equal(1, reschedule.FailureCount);
        Assert.True(reschedule.BackoffSeconds > 0, "backoff pushes the next attempt into the future");
        // One whole-batch attempt plus one retry per group: the per-group linear shape, not a binary split.
        Assert.Equal(4, target.Calls);
    }

    [Fact]
    public async Task A_recoverable_rejection_at_the_threshold_quarantines_and_fails_the_tick_once()
    {
        var bad = Row("bad", job: "bad", failureCount: 4);
        var store = new FakeStore(bad);
        var target = new FakeTarget { RejectWhen = _ => true };
        var svc = new OutboxRelayService(store, target);

        var ex = await Assert.ThrowsAsync<OutboxQuarantineTickException>(() =>
            svc.RunTickAsync(Options(threshold: 5), TestContext.Current.CancellationToken)
        );

        Assert.Equal("orders", ex.SourceName);
        Assert.Equal(1, ex.QuarantinedCount);
        Assert.Contains(bad.OutboxId, store.Quarantined);
        Assert.Empty(store.Rescheduled);
    }

    [Fact]
    public async Task A_malformed_meta_row_quarantines_immediately_without_calling_the_target()
    {
        var row = Row("k1", meta: "{ not json");
        var store = new FakeStore(row);
        var target = new FakeTarget();
        var svc = new OutboxRelayService(store, target);

        await Assert.ThrowsAsync<OutboxQuarantineTickException>(() => svc.RunTickAsync(Options(), TestContext.Current.CancellationToken));

        Assert.Contains(row.OutboxId, store.Quarantined);
        Assert.Empty(target.Batches);
    }

    [Fact]
    public async Task A_tick_processes_at_most_twenty_batches_of_two_hundred_fifty_six_rows()
    {
        var store = new FakeStore(Enumerable.Range(0, 20 * 256 + 300).Select(i => Row($"k{i}")).ToArray());
        var target = new FakeTarget();
        var svc = new OutboxRelayService(store, target);

        var summary = await svc.RunTickAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal(20, store.ClaimCalls);
        Assert.Equal(20 * 256, store.Deleted.Count);
        // The summary reports the full envelope relayed and the 300 unclaimed rows as remaining backlog.
        Assert.Equal(new OutboxTickSummary(20 * 256, 20 * 256, 0, 0, 300), summary);
    }

    [Fact]
    public async Task A_group_rejection_increments_each_rows_own_count_and_only_over_threshold_rows_quarantine()
    {
        // One (namespace, dedup) group with two staged duplicates at different failure counts: 0 and 4.
        var fresh = Row("k1", failureCount: 0, created: Now, id: Guid.NewGuid());
        var aged = Row("k1", failureCount: 4, created: Now.AddSeconds(5), id: Guid.NewGuid());
        var store = new FakeStore(fresh, aged);
        var target = new FakeTarget { RejectWhen = _ => true };
        var svc = new OutboxRelayService(store, target);

        // The count-4 row reaches the threshold of five and quarantines; the count-0 row reschedules at 1.
        await Assert.ThrowsAsync<OutboxQuarantineTickException>(() =>
            svc.RunTickAsync(Options(threshold: 5), TestContext.Current.CancellationToken)
        );

        Assert.Equal([aged.OutboxId], store.Quarantined);
        var resched = Assert.Single(store.Rescheduled);
        Assert.Equal(fresh.OutboxId, resched.OutboxId);
        Assert.Equal(1, resched.FailureCount);
    }

    [Fact]
    public async Task Budget_exhaustion_releases_the_unprocessed_remainder_and_ends_the_tick()
    {
        // Every batch fully rejects, so each 256-group claim fans out into one whole-batch attempt plus one
        // retry per group (up to 257 target calls). The shared per-tick target-enqueue budget caps the total
        // fan-out at MaxTargetEnqueues: when it runs out mid-batch the unresolved remainder is RELEASED
        // (not rescheduled), which only happens under budget exhaustion, and the tick then ends.
        var rows = Enumerable.Range(0, 20 * 256).Select(i => Row($"k{i}", job: "bad")).ToArray();
        var store = new FakeStore(rows);
        var target = new FakeTarget { RejectWhen = _ => true };
        var svc = new OutboxRelayService(store, target);

        // A high threshold so the rejections reschedule rather than quarantine (no tick failure).
        await svc.RunTickAsync(Options(threshold: 1000), TestContext.Current.CancellationToken);

        // The budget bounds total target enqueues; a run with unlimited budget would reschedule every row
        // and release none, so a non-empty release set is the signature of mid-batch budget exhaustion.
        Assert.NotEmpty(store.Released);
        Assert.True(store.Rescheduled.Count < rows.Length, "the released remainder was not rescheduled");
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task Cancellation_releases_the_claimed_batch_best_effort()
    {
        var row = Row("k1");
        var store = new FakeStore(row);
        using var cts = new CancellationTokenSource();
        var target = new FakeTarget
        {
            OnEnqueue = () =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        };
        var svc = new OutboxRelayService(store, target);

        await Assert.ThrowsAsync<OperationCanceledException>(() => svc.RunTickAsync(Options(), cts.Token));

        Assert.Contains(row.OutboxId, store.Released);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task A_transient_target_failure_is_infrastructure_releases_the_claim_and_fails_the_tick()
    {
        var row = Row("k1");
        var store = new FakeStore(row);
        // A plain transient exception (not OCE, ArgumentException, or PayloadTooLargeException) is
        // classified as infrastructure: the claim is released best-effort and the tick fails without
        // consuming any quarantine budget.
        var target = new FakeTarget { OnEnqueue = () => throw new InvalidOperationException("target unavailable") };
        var svc = new OutboxRelayService(store, target);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunTickAsync(Options(), TestContext.Current.CancellationToken));

        Assert.Contains(row.OutboxId, store.Released);
        Assert.Empty(store.Quarantined);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task A_transient_store_failure_surfaces_as_an_infrastructure_tick_failure()
    {
        // A plain transient exception surfacing through the store (a source-database fault) fails the
        // tick and is retried on the next tick; it never quarantines a row.
        var store = new ThrowingStore();
        var target = new FakeTarget();
        var svc = new OutboxRelayService(store, target);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunTickAsync(Options(), TestContext.Current.CancellationToken));

        Assert.Empty(target.Batches);
    }

    private sealed class ThrowingStore : IOutboxRelayStore
    {
        public Task<IReadOnlyList<OutboxRow>> ClaimDueAsync(ClaimOutboxCommand command, CancellationToken ct) =>
            throw new InvalidOperationException("source database unavailable");

        public Task DeleteClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct) => Task.CompletedTask;

        public Task RescheduleAsync(RescheduleOutboxCommand command, CancellationToken ct) => Task.CompletedTask;

        public Task QuarantineAsync(QuarantineOutboxCommand command, CancellationToken ct) => Task.CompletedTask;

        public Task ReleaseClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct) => Task.CompletedTask;

        public Task<long> CountBacklogAsync(CancellationToken ct) => Task.FromResult(0L);
    }

    private sealed class FakeTarget : IOutboxTarget
    {
        public JobEnqueueAction Action { get; init; } = JobEnqueueAction.Inserted;
        public Func<IReadOnlyList<JobEnqueueRequest>, bool>? RejectWhen { get; init; }
        public Action? OnEnqueue { get; init; }
        public List<IReadOnlyList<JobEnqueueRequest>> Batches { get; } = [];
        public int Calls { get; private set; }

        public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
            IReadOnlyList<JobEnqueueRequest> requests,
            CancellationToken ct
        )
        {
            Calls++;
            OnEnqueue?.Invoke();
            if (RejectWhen?.Invoke(requests) == true)
            {
                throw new ArgumentException("deterministic target rejection");
            }
            Batches.Add(requests);
            IReadOnlyList<JobEnqueueOutcome> outcomes = requests.Select(_ => new JobEnqueueOutcome(1, JobRef.New(), Action)).ToList();
            return ValueTask.FromResult(outcomes);
        }
    }

    private sealed class FakeStore(params OutboxRow[] rows) : IOutboxRelayStore
    {
        private readonly Queue<OutboxRow> _due = new(rows);

        public int ClaimCalls { get; private set; }
        public List<Guid> Deleted { get; } = [];
        public List<OutboxReschedule> Rescheduled { get; } = [];
        public List<Guid> Quarantined { get; } = [];
        public List<Guid> Released { get; } = [];

        public Task<IReadOnlyList<OutboxRow>> ClaimDueAsync(ClaimOutboxCommand command, CancellationToken ct)
        {
            ClaimCalls++;
            var batch = new List<OutboxRow>();
            while (batch.Count < command.BatchSize && _due.Count > 0)
            {
                batch.Add(_due.Dequeue());
            }
            return Task.FromResult<IReadOnlyList<OutboxRow>>(batch);
        }

        public Task DeleteClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct)
        {
            Deleted.AddRange(command.OutboxIds);
            return Task.CompletedTask;
        }

        public Task RescheduleAsync(RescheduleOutboxCommand command, CancellationToken ct)
        {
            Rescheduled.AddRange(command.Rows);
            return Task.CompletedTask;
        }

        public Task QuarantineAsync(QuarantineOutboxCommand command, CancellationToken ct)
        {
            Quarantined.AddRange(command.Rows.Select(r => r.OutboxId));
            return Task.CompletedTask;
        }

        public Task ReleaseClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct)
        {
            Released.AddRange(command.OutboxIds);
            return Task.CompletedTask;
        }

        // The unclaimed remainder plus everything released back stands in for the Pending backlog.
        public Task<long> CountBacklogAsync(CancellationToken ct) => Task.FromResult((long)(_due.Count + Released.Count));
    }
}
