using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Services.Time;
using Acta.Tests.Context;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Alerts;

/// <summary>
/// The generate drain's control flow, at the one seam a conformance spec cannot reach: how many reads
/// a pass issues, in what order the cursor is written, and what a pass leaves behind when it dies
/// mid-drain. The conformance family pins the same behavior against real databases; these facts pin
/// the loop's own decisions, where a store double can count calls and fail a chosen one.
/// </summary>
public sealed class AlertsJobDrainTests
{
    // Four per batch keeps a whole backlog legible in a fact; the cap and the budget are moved
    // out of the way except in the facts that are about them.
    private static readonly AlertDrainBudget SmallBatches = new(BatchSize: 4, MaxBatches: 40, TimeBudget: TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Idle_pass_reads_once_and_leaves_no_cursor()
    {
        var store = new BacklogAlertStore(backlog: 0);
        var ctx = new CursorRecordingContext();
        var ct = TestContext.Current.CancellationToken;

        await CreateJob(store, SmallBatches).Handle(ctx, ct);

        // One read that came back empty, and nothing written: no checkpoint of an unmoved cursor, and
        // no second read to confirm what the first already said.
        Assert.Single(store.Reads);
        Assert.Empty(ctx.CursorWrites);
        Assert.False(await ctx.ExistsVariableAsync(AlertsJob.CursorVariableName, ct));
    }

    [Fact]
    public async Task Backlog_spanning_batches_drains_in_one_pass_and_checkpoints_after_each()
    {
        var store = new BacklogAlertStore(backlog: 10);
        var ctx = new CursorRecordingContext();
        var ct = TestContext.Current.CancellationToken;

        await CreateJob(store, SmallBatches).Handle(ctx, ct);

        // Three reads: two full batches and a short one that ends the pass without a fourth read.
        // Each read starts from the batch before it, and each batch checkpointed on its own.
        Assert.Equal([(0L, 4), (4L, 4), (8L, 4)], store.Reads);
        Assert.Equal([4L, 8L, 10L], ctx.CursorWrites);
        Assert.Equal(10, store.Raises);
    }

    [Fact]
    public async Task Batch_cap_ends_the_pass_with_the_backlog_still_above_the_cursor()
    {
        var store = new BacklogAlertStore(backlog: 20);
        var ctx = new CursorRecordingContext();
        var ct = TestContext.Current.CancellationToken;
        var capped = SmallBatches with { MaxBatches = 3 };

        await CreateJob(store, capped).Handle(ctx, ct);

        Assert.Equal(3, store.Reads.Count);
        Assert.Equal([4L, 8L, 12L], ctx.CursorWrites);
        Assert.Equal(12, store.Raises);
    }

    [Fact]
    public async Task Spent_time_budget_ends_the_pass_after_the_batch_it_was_checked_behind()
    {
        var store = new BacklogAlertStore(backlog: 20);
        var ctx = new CursorRecordingContext();
        var ct = TestContext.Current.CancellationToken;

        await CreateJob(store, SmallBatches with { TimeBudget = TimeSpan.Zero }).Handle(ctx, ct);

        // The budget is spent before the pass begins, and the batch in flight still ran to completion
        // and still checkpointed: the check is between batches, never inside one.
        Assert.Single(store.Reads);
        Assert.Equal([4L], ctx.CursorWrites);
        Assert.Equal(4, store.Raises);
    }

    [Fact]
    public async Task Crash_mid_drain_keeps_every_completed_batch_and_the_next_pass_resumes_behind_it()
    {
        var ctx = new CursorRecordingContext();
        var ct = TestContext.Current.CancellationToken;

        // The third read throws, so two batches committed and checkpointed before the pass died. This
        // is the failure a once-per-pass checkpoint would have thrown away entirely.
        var crashing = new BacklogAlertStore(backlog: 20) { ThrowOnRead = 3 };
        await Assert.ThrowsAsync<TimeoutException>(() => CreateJob(crashing, SmallBatches).Handle(ctx, ct));

        Assert.Equal([4L, 8L], ctx.CursorWrites);
        Assert.Equal(8L, await ctx.GetRequiredVariableAsync<long>(AlertsJob.CursorVariableName, ct));

        // The next invocation resumes from that cursor and re-offers nothing below it.
        var recovering = new BacklogAlertStore(backlog: 20);
        await CreateJob(recovering, SmallBatches).Handle(ctx, ct);

        Assert.Equal(8L, recovering.Reads[0].Cursor);
        Assert.Equal(12, recovering.Raises);
        Assert.Equal(20L, await ctx.GetRequiredVariableAsync<long>(AlertsJob.CursorVariableName, ct));
    }

    private static AlertsJob CreateJob(IAlertStore store, AlertDrainBudget drain) =>
        new(store, new FixedClock(), channels: null!, transports: null!, Options.Create(new JobsOptions())) { Drain = drain };

    /// <summary>
    /// Records the cursor values written, in order, which is the only way to tell a per-batch
    /// checkpoint from one write at the end that happens to carry the same final number.
    /// </summary>
    private sealed class CursorRecordingContext : RecordingJobContext
    {
        public List<long> CursorWrites { get; } = [];

        protected override Task SetVariableCoreAsync<T>(string name, T value, CancellationToken ct)
        {
            if (string.Equals(name, AlertsJob.CursorVariableName, StringComparison.Ordinal) && value is long cursor)
            {
                CursorWrites.Add(cursor);
            }

            return base.SetVariableCoreAsync(name, value, ct);
        }
    }

    /// <summary>
    /// A backlog of event ids <c>1..backlog</c> served the way the real query serves them: everything
    /// above the cursor, up to the batch size, in id order. <c>ThrowOnRead</c> fails the read at that
    /// 1-based ordinal, standing in for the pass dying between batches.
    /// </summary>
    private sealed class BacklogAlertStore(int backlog) : IAlertStore
    {
        public List<(long Cursor, int BatchSize)> Reads { get; } = [];

        public int Raises { get; private set; }

        public int ThrowOnRead { get; init; }

        public Task<IReadOnlyList<AlertableEvent>> GetAlertableEventsAsync(
            int namespaceId,
            long cursorEventId,
            int batchSize,
            CancellationToken ct
        )
        {
            Reads.Add((cursorEventId, batchSize));
            if (Reads.Count == ThrowOnRead)
            {
                return Task.FromException<IReadOnlyList<AlertableEvent>>(new TimeoutException("provider timeout"));
            }

            var rows = Enumerable
                .Range(1, backlog)
                .Select(i => (long)i)
                .Where(id => id > cursorEventId)
                .Take(batchSize)
                .Select(Event)
                .ToArray();
            return Task.FromResult<IReadOnlyList<AlertableEvent>>(rows);
        }

        public Task<AlertRaiseOutcome> RaiseJobAlertAsync(RaiseJobAlertCommand command, CancellationToken ct)
        {
            Raises++;
            return Task.FromResult(new AlertRaiseOutcome(Raises, command.SourceEventId));
        }

        public Task<IReadOnlyList<DeliverableAlert>> GetDeliverableAlertsAsync(int namespaceId, int batchSize, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeliverableAlert>>([]);

        public Task<bool> UpdateAlertDeliveryAsync(
            long alertId,
            int expectedVersion,
            AlertDeliveryStatusCode status,
            byte retryCount,
            DateTime? retryAfterUtc,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<int> ResolveJobAlertsAsync(int namespaceId, long jobId, long sourceEventId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AlertControlOutcome> AcknowledgeJobAlertAsync(AlertControlCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AlertControlOutcome> ResolveJobAlertManualAsync(AlertControlCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AlertPage> ListJobAlertsAsync(AlertPageRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<AlertListItem?> GetJobAlertAsync(Guid alertRef, CancellationToken ct) => throw new NotSupportedException();

        // OnTerminal plus a terminal transition: one FinalFailure raise per event, with no escalation
        // arm and no resolution arm in play, so the raise count is the projected-event count.
        private static AlertableEvent Event(long eventId) =>
            new(
                eventId,
                JobId: 101,
                DefinitionId: 7,
                JobName: "probe",
                AlertProfile: AlertProfileCode.OnTerminal,
                AlertChannelName: null,
                ExecutionStatus: ExecutionStatusCode.Failed,
                ToStatus: JobStatusCode.Failed,
                ReasonCode: JobEventReasonCode.JobUnhandledException,
                ReasonMessage: "boom"
            );
    }

    private sealed class FixedClock : IActaClock
    {
        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(DateTime.UnixEpoch);
    }
}
