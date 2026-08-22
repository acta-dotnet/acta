using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Modules.Operations.Events;
using Acta.Runtime.Querying;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Api;

/// <summary>
/// Validation behavior of the facade read methods with no database: filter dependencies, identifier
/// shape, page-size bounds, the events total guard, and cursor envelope rejection. Each read is
/// exercised on its owner: the job list on <see cref="JobsService"/>, the event list on
/// <see cref="EventsService"/>, schedules on <see cref="SchedulesApi"/>, alerts on
/// <see cref="AlertsApi"/>. The store stub returns empty pages so cursor checks run before any data
/// access matters; unused collaborators are null because the read path only touches the store.
/// </summary>
public sealed class FacadeReadValidationTests
{
    private static JobsService Jobs() => JobsReadService();

    private static EventsService Events() => new(new EmptyEventStore());

    private static JobsService JobsReadService() =>
        new(new EmptyJobStore(), null!, null!, null!, new EmptyScheduleStore(), null!, null!, Options.Create(new JobsOptions()));

    private sealed class EmptyJobStore : IJobStore
    {
        public ValueTask<JobDetail?> GetJobAsync(long jobId, CancellationToken ct) => ValueTask.FromResult<JobDetail?>(null);

        public ValueTask<JobStatusCode?> GetJobStatusAsync(long jobId, CancellationToken ct) => ValueTask.FromResult<JobStatusCode?>(null);

        public Task<JobInputRecord?> GetJobInputAsync(long jobId, CancellationToken ct) => Task.FromResult<JobInputRecord?>(null);

        public Task<JobResultRecord?> GetJobResultAsync(long jobId, int? executionNumber, CancellationToken ct) =>
            Task.FromResult<JobResultRecord?>(null);

        public Task<IReadOnlyList<JobCheckpointItem>> GetJobCheckpointsAsync(long jobId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JobCheckpointItem>>([]);

        public ValueTask<JobExplainData?> GetJobExplanationAsync(long jobId, CancellationToken ct) =>
            ValueTask.FromResult<JobExplainData?>(null);

        public ValueTask<JobLineageData?> GetJobLineageMapAsync(long jobId, int childFetchLimit, CancellationToken ct) =>
            ValueTask.FromResult<JobLineageData?>(null);

        public Task<JobPage> ListJobsAsync(JobPageRequest request, CancellationToken ct) =>
            Task.FromResult(new JobPage([], request.IncludeTotal ? 0L : null));

        public ValueTask<long?> ResolveJobIdByRefAsync(Guid jobRef, CancellationToken ct) => ValueTask.FromResult<long?>(null);

        public ValueTask<long?> ResolveJobIdByDeduplicationKeyAsync(string jobNamespace, string deduplicationKey, CancellationToken ct) =>
            ValueTask.FromResult<long?>(null);

        public Task<IReadOnlyList<EnqueueOutcomeRow>> EnqueueOneAsync(JobEnqueueRow row, Guid jobRef, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EnqueueOutcomeRow>> EnqueueBatchAsync(
            IReadOnlyList<JobEnqueueRow> rows,
            IReadOnlyList<Guid> jobRefs,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<EnqueueOutcomeRow>> EnqueueOneInTransactionAsync(
            System.Data.Common.DbTransaction transaction,
            JobEnqueueRow row,
            Guid jobRef,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<EnqueueOutcomeRow>> EnqueueBatchInTransactionAsync(
            System.Data.Common.DbTransaction transaction,
            IReadOnlyList<JobEnqueueRow> rows,
            IReadOnlyList<Guid> jobRefs,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<CancelJobOutcome> CancelJobAsync(long jobId, JobControlInput input, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<JobControlOutcome> PauseJobAsync(long jobId, JobControlInput input, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<JobControlOutcome> ResumeJobAsync(long jobId, JobControlInput input, DateTime? nextRunAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<JobControlOutcome> RestartJobAsync(long jobId, JobControlInput input, DateTime? nextRunAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<JobControlOutcome> RescheduleJobAsync(long jobId, DateTime nextRunAtUtc, JobControlInput input, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<JobControlOutcome> ReprioritizeJobAsync(
            long jobId,
            JobPriorityCode priority,
            JobControlInput input,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<JobControlOutcome> UpdateJobInputAsync(
            long jobId,
            JobPayload input,
            JobControlInput controlInput,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<JobControlOutcome> PurgeJobAsync(long jobId, JobControlInput input, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ResetJobStateAsync(long jobId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class EmptyEventStore : IEventStore
    {
        public Task<EventPage> ListEventsAsync(EventPageRequest request, CancellationToken ct) =>
            Task.FromResult(new EventPage([], request.IncludeTotal ? 0L : null));
    }

    private sealed class EmptyScheduleStore : IScheduleStore
    {
        public Task<IReadOnlyList<LiveSchedule>> GetLiveSchedulesAsync(long jobId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LiveSchedule>>([]);

        public Task<IReadOnlyList<StoredScheduleState>> GetScheduleStateAsync(int namespaceId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredScheduleState>>([]);

        public Task<SchedulePage> ListJobSchedulesAsync(SchedulePageRequest request, CancellationToken ct) =>
            Task.FromResult(new SchedulePage([], request.IncludeTotal ? 0L : null));

        public Task<IReadOnlyList<RegisteredScheduleSlot>> RegisterScheduledJobsAsync(
            RegisterScheduledJobsCommand command,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<ScheduleControlOutcome> PauseScheduleAsync(PauseScheduleCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ScheduleControlOutcome> ResumeScheduleAsync(ResumeScheduleCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ScheduleControlOutcome> SetScheduleOverridesAsync(SetScheduleOverridesCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ScheduleControlOutcome> TriggerScheduleNowAsync(TriggerScheduleNowCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyAlertStore : IAlertStore
    {
        public Task<AlertRaiseOutcome> RaiseJobAlertAsync(RaiseJobAlertCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AlertableEvent>> GetAlertableEventsAsync(
            int namespaceId,
            long cursorEventId,
            int batchSize,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeliverableAlert>> GetDeliverableAlertsAsync(int namespaceId, int batchSize, CancellationToken ct) =>
            throw new NotSupportedException();

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

        public Task<AlertPage> ListJobAlertsAsync(AlertPageRequest request, CancellationToken ct) =>
            Task.FromResult(new AlertPage([], request.IncludeTotal ? 0L : null));

        public Task<AlertListItem?> GetJobAlertAsync(Guid alertRef, CancellationToken ct) => Task.FromResult<AlertListItem?>(null);
    }

    private static AlertsApi Alerts() => new(new EmptyAlertStore());

    private static SchedulesApi Schedules() => new(new EmptyScheduleStore(), null!, null!, JobsReadService());

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Job_name_requires_namespace()
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await Jobs().ListJobsAsync(new ListJobsQuery(JobName: "send-invoice"), Ct)
        );

        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await Schedules().ListAsync(new ListSchedulesQuery(JobName: "send-invoice"), Ct)
        );
    }

    [Fact]
    public async Task Invalid_namespace_shape_throws()
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await Jobs().ListJobsAsync(new ListJobsQuery(JobNamespace: "Not Kebab"), Ct)
        );
    }

    [Fact]
    public async Task Framework_job_name_is_accepted()
    {
        var result = await Jobs().ListJobsAsync(new ListJobsQuery(JobNamespace: "billing", JobName: "sys.recovery"), Ct);

        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Page_size_below_one_throws(int pageSize)
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () => await Jobs().ListJobsAsync(new ListJobsQuery(PageSize: pageSize), Ct));
    }

    [Fact]
    public async Task Events_total_without_job_id_throws()
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await Events().ListJobEventsAsync(new ListEventsQuery(IncludeTotal: true), Ct)
        );
    }

    [Fact]
    public async Task Events_total_with_job_id_is_allowed()
    {
        var result = await Events().ListJobEventsAsync(new ListEventsQuery(JobId: 1, IncludeTotal: true), Ct);

        Assert.Equal(0L, result.TotalCount);
    }

    [Fact]
    public async Task Cursor_from_another_operation_is_rejected()
    {
        var jobsCursor = PageCursorCodec.Encode(
            "ListJobs",
            "created_at_utc desc, id desc",
            QueryFilterHash.Compute([]),
            [DateTime.UnixEpoch, 1L]
        );

        await Assert.ThrowsAsync<InvalidPageCursorException>(async () =>
            await Alerts().ListAsync(new ListAlertsQuery(Cursor: jobsCursor), Ct)
        );
    }

    [Fact]
    public async Task Cursor_with_changed_filters_is_rejected()
    {
        var unfiltered = PageCursorCodec.Encode(
            "ListJobs",
            "created_at_utc desc, id desc",
            QueryFilterHash.Compute([]),
            [DateTime.UnixEpoch, 1L]
        );

        await Assert.ThrowsAsync<InvalidPageCursorException>(async () =>
            await Jobs().ListJobsAsync(new ListJobsQuery(JobNamespace: "billing", Cursor: unfiltered), Ct)
        );
    }

    [Fact]
    public async Task Undefined_enum_filter_values_throw()
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await Jobs().ListJobsAsync(new ListJobsQuery(Status: (JobStatusCode)99), Ct)
        );

        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await Alerts().ListAsync(new ListAlertsQuery(SeverityAtLeast: (AlertSeverityCode)200), Ct)
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task Non_positive_id_filters_throw(long jobId)
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await Events().ListJobEventsAsync(new ListEventsQuery(JobId: jobId), Ct)
        );

        await Assert.ThrowsAsync<InvalidQueryException>(async () => await Alerts().ListAsync(new ListAlertsQuery(JobId: jobId), Ct));
    }

    [Fact]
    public async Task Total_is_null_unless_requested()
    {
        var result = await Jobs().ListJobsAsync(new ListJobsQuery(), Ct);

        Assert.Null(result.TotalCount);
        Assert.False(result.HasMore);
        Assert.Null(result.NextCursor);
        Assert.Equal(50, result.PageSize);
    }
}
