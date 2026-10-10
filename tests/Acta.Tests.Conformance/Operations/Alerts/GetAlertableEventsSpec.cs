using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>Conformance for the <c>sys.alerts</c> generate read: which finished attempts it offers, and the cursor and horizon that bound it.</summary>
[ConformanceSpec(
    "get-alertable-events.cursor-and-horizon",
    "The alertable-event read walks a (stamp, id) cursor up to an inclusive horizon",
    Area = "Alerts",
    Contract = "GetAlertableEvents reads a namespace's alertable finished attempts after the (stamp, id) cursor and at or before the horizon, ordered by stamp then id."
)]
public sealed class GetAlertableEventsSpec : ActaStorageTestBase<ConformanceFixture>
{
    /// <summary>The instant every stamp is staged from, so the cursor and the horizon are exact inputs rather than clock reads.</summary>
    private static readonly DateTime T0 = new(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = T0.AddDays(-1);
    private static readonly DateTime Horizon = T0.AddMinutes(1);

    private int DefId;
    private long JobIdValue;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        var ct = TestContext.Current.CancellationToken;
        DefId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "alertable-def", AlertProfileCode.OnFailure, ct);
        (JobIdValue, _) = await Seeder.SeedJobAsync(TestNamespaceId, DefId, ct: ct);
    }

    [Fact(
        DisplayName = "Only the namespace's finished attempts that alert are read: failures, charged retries, uncharged reclaims, and successes"
    )]
    public async Task Only_alertable_finished_attempts_are_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherNamespaceId = await Seeder.SeedJobNamespaceAsync(TestKey("other-ns"), ct: ct);

        long[] alertable =
        [
            await StageAsync(JobStatusCode.Failed, ExecutionStatusCode.Failed, EventReasonCode.JobNonRetryableException, T0, ct),
            await StageAsync(JobStatusCode.Ready, ExecutionStatusCode.Failed, EventReasonCode.JobUnhandledException, Ms(1), ct),
            await StageAsync(JobStatusCode.Ready, ExecutionStatusCode.Orphaned, EventReasonCode.JobLeaseExpired, Ms(2), ct),
            await StageAsync(JobStatusCode.Ready, ExecutionStatusCode.Failed, EventReasonCode.JobExecutionTimeout, Ms(3), ct),
            await StageAsync(JobStatusCode.Suspended, ExecutionStatusCode.Orphaned, EventReasonCode.JobLeaseExpired, Ms(4), ct),
            await StageAsync(JobStatusCode.Succeeded, ExecutionStatusCode.Succeeded, null, Ms(5), ct),
        ];

        await StageAsync(JobStatusCode.Ready, ExecutionStatusCode.Rescheduled, EventReasonCode.JobHandlerRescheduled, Ms(6), ct);
        await StageAsync(JobStatusCode.Suspended, ExecutionStatusCode.Suspended, EventReasonCode.JobHandlerSuspended, Ms(7), ct);
        await StageAsync(JobStatusCode.Cancelled, ExecutionStatusCode.Cancelled, EventReasonCode.JobControlManual, Ms(8), ct);
        // A terminal failure's triple on rows the read must still pass over: another event code, no job,
        // and another namespace.
        await StageAsync(JobStatusCode.Failed, ExecutionStatusCode.Failed, null, Ms(9), ct, code: EventCode.JobExecutionStarted);
        await StageAsync(JobStatusCode.Failed, ExecutionStatusCode.Failed, null, Ms(10), ct, jobless: true);
        await StageAsync(JobStatusCode.Failed, ExecutionStatusCode.Failed, null, Ms(11), ct, namespaceId: otherNamespaceId);

        var read = await ReadAsync(Start, 0, Horizon, ct);

        Assert.Equal(alertable, read.Select(e => e.EventId));
        Assert.Empty(await ReadAlertsAsync(TestNamespaceId, ct));
    }

    [Fact(DisplayName = "The horizon is inclusive: an event stamped at it is read and one stamped a millisecond later is not")]
    public async Task Horizon_is_inclusive()
    {
        var ct = TestContext.Current.CancellationToken;
        var atHorizon = await StageFailureAsync(T0, ct);
        var pastHorizon = await StageFailureAsync(Ms(1), ct);

        Assert.Equal([atHorizon], (await ReadAsync(Start, 0, T0, ct)).Select(e => e.EventId));
        Assert.Equal([atHorizon, pastHorizon], (await ReadAsync(Start, 0, Ms(1), ct)).Select(e => e.EventId));
    }

    [Fact(
        DisplayName = "The cursor is the (stamp, id) pair: a same-stamp higher id is read, while the cursor's own row and earlier stamps are not"
    )]
    public async Task Cursor_is_the_stamp_and_id_pair()
    {
        var ct = TestContext.Current.CancellationToken;
        var t1 = Ms(10);
        var first = await StageFailureAsync(t1, ct);
        var second = await StageFailureAsync(t1, ct);
        // The slow writer's shape: the highest id under the earliest stamp.
        var inverted = await StageFailureAsync(T0, ct);
        Assert.True(first < second && second < inverted, "the staged ids must ascend in insert order");

        // An id-only cursor at `first` would also take `inverted`; the pair passes over it, because its
        // stamp is behind the cursor's.
        Assert.Equal([second], (await ReadAsync(t1, first, Horizon, ct)).Select(e => e.EventId));
        Assert.Empty(await ReadAsync(t1, second, Horizon, ct));
        Assert.Equal([inverted, first, second], (await ReadAsync(T0, 0, Horizon, ct)).Select(e => e.EventId));
    }

    [Fact(DisplayName = "Rows come back ordered by stamp then id, and the batch size caps one read")]
    public async Task Rows_order_by_stamp_then_id_and_the_batch_caps_the_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var late = await StageFailureAsync(Ms(2), ct);
        var early = await StageFailureAsync(Ms(1), ct);
        var earlyTwin = await StageFailureAsync(Ms(1), ct);

        var batch = await ReadAsync(Start, 0, Horizon, ct, batchSize: 2);
        Assert.Equal([early, earlyTwin], batch.Select(e => e.EventId));

        var last = batch[^1];
        Assert.Equal([late], (await ReadAsync(last.CreatedAtUtc, last.EventId, Horizon, ct, batchSize: 2)).Select(e => e.EventId));
    }

    [Fact(DisplayName = "Each row carries its definition's name, effective alert profile and channel, and the event's outcome and stamp")]
    public async Task Rows_carry_the_definition_policy_and_the_event_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        await Db.From<DefinitionEntity>()
            .Where(d => d.Id == DefId)
            .UpdateOnlyAsync(
                () =>
                    new DefinitionEntity
                    {
                        AlertChannelName = "ops",
                        AlertChannelNameOverride = "pager",
                        AlertProfileOverride = AlertProfileCode.OnTerminal,
                    },
                ct
            );
        var eventId = await StageAsync(JobStatusCode.Failed, ExecutionStatusCode.Failed, EventReasonCode.JobNonRetryableException, T0, ct);

        var row = Assert.Single(await ReadAsync(Start, 0, Horizon, ct));

        Assert.Equal(eventId, row.EventId);
        Assert.Equal(T0, row.CreatedAtUtc);
        Assert.Equal(JobIdValue, row.JobId);
        Assert.Equal(DefId, row.DefinitionId);
        Assert.Equal("alertable-def", row.JobName);
        Assert.Equal(AlertProfileCode.OnTerminal, row.AlertProfile);
        Assert.Equal("pager", row.AlertChannelName);
        Assert.Equal(ExecutionStatusCode.Failed, row.ExecutionStatus);
        Assert.Equal(JobStatusCode.Failed, row.ToStatus);
        Assert.Equal(EventReasonCode.JobNonRetryableException, row.ReasonCode);
        Assert.Equal("staged", row.ReasonMessage);
    }

    private static DateTime Ms(int milliseconds) => T0.AddMilliseconds(milliseconds);

    private Task<IReadOnlyList<AlertableEvent>> ReadAsync(
        DateTime cursorUtc,
        long cursorEventId,
        DateTime horizonUtc,
        CancellationToken ct,
        int batchSize = 50
    ) =>
        Services
            .GetRequiredService<IAlertsOperations>()
            .GetAlertableEventsAsync(TestNamespaceId, cursorUtc, cursorEventId, batchSize, horizonUtc, ct);

    private Task<long> StageFailureAsync(DateTime stampUtc, CancellationToken ct) =>
        StageAsync(JobStatusCode.Failed, ExecutionStatusCode.Failed, EventReasonCode.JobUnhandledException, stampUtc, ct);

    /// <summary>One event row, inserted and then re-stamped, since <c>created_at_utc</c> takes its column default on insert.</summary>
    private async Task<long> StageAsync(
        JobStatusCode? toStatus,
        ExecutionStatusCode? outcome,
        EventReasonCode? reason,
        DateTime stampUtc,
        CancellationToken ct,
        EventCode code = EventCode.JobExecutionFinished,
        bool jobless = false,
        int? namespaceId = null
    )
    {
        var id = await Db.From<EventEntity>()
            .InsertAsync<long>(
                new EventEntity
                {
                    EventCode = code,
                    NamespaceId = namespaceId ?? TestNamespaceId,
                    ActorCode = ActorCode.Worker,
                    JobId = jobless ? null : JobIdValue,
                    DefinitionId = DefId,
                    ExecutionNumber = 1,
                    ToStatus = toStatus,
                    ExecutionStatus = outcome,
                    ReasonCode = reason,
                    ReasonMessage = "staged",
                },
                ct
            );
        Assert.Equal(
            1,
            await Db.From<EventEntity>().Where(e => e.Id == id).UpdateOnlyAsync(() => new EventEntity { CreatedAtUtc = stampUtc }, ct)
        );
        return id;
    }
}
