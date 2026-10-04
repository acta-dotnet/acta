using System.Data;
using System.Data.Common;
using Acta.Runtime;
using Acta.Tests.Runtime;
using Xunit;

namespace Acta.Tests.Api;

/// <summary>
/// Validation behavior of the facade read methods with no database: filter dependencies, identifier
/// shape, page-size bounds, the events total guard, and cursor envelope rejection. Each read is
/// exercised on its owner, the list's handler. The stub session returns empty pages so cursor
/// checks run before any data access matters; unused collaborators are null because the read path only
/// touches the store or the session.
/// </summary>
public sealed class FacadeReadValidationTests
{
    private static ListJobsHandler ListJobs() => new(new EmptyPageSession(), null!);

    private static ListEventsHandler ListEvents() => new(new EmptyPageSession(), null!);

    // Runs the handler's own row reader over an empty result: no rows, and no total.
    private sealed class EmptyPageSession : DbSessionStub
    {
        public override Task<T> QueryAsync<T>(
            string stem,
            Action<DbCommand> bind,
            Func<DbDataReader, CancellationToken, Task<T>> read,
            CancellationToken ct
        ) => read(new DataTable().CreateDataReader(), ct);
    }

    private static ListAlertsHandler ListAlerts() => new(new EmptyPageSession(), null!);

    private static ListSchedulesHandler ListSchedules() => new(new EmptyPageSession(), null!);

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Job_name_requires_namespace()
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await ListJobs().HandleAsync(new ListJobsQuery(JobName: "send-invoice"), Ct)
        );

        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await ListSchedules().HandleAsync(new ListSchedulesQuery(JobName: "send-invoice"), Ct)
        );
    }

    [Fact]
    public async Task Invalid_namespace_shape_throws()
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await ListJobs().HandleAsync(new ListJobsQuery(JobNamespace: "Not Kebab"), Ct)
        );
    }

    [Fact]
    public async Task Framework_job_name_is_accepted()
    {
        var result = await ListJobs().HandleAsync(new ListJobsQuery(JobNamespace: "billing", JobName: "sys.recovery"), Ct);

        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Page_size_below_one_throws(int pageSize)
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await ListJobs().HandleAsync(new ListJobsQuery(PageSize: pageSize), Ct)
        );
    }

    [Fact]
    public async Task Events_total_without_job_id_throws()
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await ListEvents().HandleAsync(new ListEventsQuery(IncludeTotal: true), Ct)
        );
    }

    [Fact]
    public async Task Events_total_with_job_id_is_allowed()
    {
        var result = await ListEvents().HandleAsync(new ListEventsQuery(JobId: 1, IncludeTotal: true), Ct);

        Assert.Empty(result.Items);
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
            await ListAlerts().HandleAsync(new ListAlertsQuery(Cursor: jobsCursor), Ct)
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
            await ListJobs().HandleAsync(new ListJobsQuery(JobNamespace: "billing", Cursor: unfiltered), Ct)
        );
    }

    [Fact]
    public async Task Undefined_enum_filter_values_throw()
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await ListJobs().HandleAsync(new ListJobsQuery(Status: (JobStatusCode)99), Ct)
        );

        await Assert.ThrowsAsync<InvalidQueryException>(async () =>
            await ListAlerts().HandleAsync(new ListAlertsQuery(SeverityAtLeast: (AlertSeverityCode)200), Ct)
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task Non_positive_id_filters_throw(long jobId)
    {
        await Assert.ThrowsAsync<InvalidQueryException>(async () => await ListEvents().HandleAsync(new ListEventsQuery(JobId: jobId), Ct));

        await Assert.ThrowsAsync<InvalidQueryException>(async () => await ListAlerts().HandleAsync(new ListAlertsQuery(JobId: jobId), Ct));
    }

    [Fact]
    public async Task Total_is_null_unless_requested()
    {
        var result = await ListJobs().HandleAsync(new ListJobsQuery(), Ct);

        Assert.Null(result.TotalCount);
        Assert.False(result.HasMore);
        Assert.Null(result.NextCursor);
        Assert.Equal(50, result.PageSize);
    }
}
