using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Operations.Events;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Events;

/// <summary>
/// Conformance for the events list read: the audit timeline newest first with keyset pagination
/// and a job-scoped opt-in total.
/// </summary>
[ConformanceSpec(
    "list-job-events.keyset-page",
    "ListJobEvents pages a job timeline newest first and scopes totals to a job",
    Area = "Reads",
    Contract = "ListJobEvents pages a job timeline newest first by cursor and reads the page plus an opt-in job-scoped count in one command.",
    Arrange = "A job is enqueued and run once so it owns a multi-event timeline.",
    Act = "The timeline is walked one event per page by job id, then a page plus the job-scoped total are read in one trip.",
    Assert = "Pages return newest first containing only that job's events and the job-scoped total matches the walk."
)]
[CoversStoreMethod(typeof(IEventStore), nameof(IEventStore.ListEventsAsync))]
public abstract class ListJobEventsSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A job timeline pages newest first with only that job's events and a job-scoped TotalCount matching the walk")]
    public async Task Lists_a_job_timeline_newest_first_with_job_scoped_total()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var queries = Services.GetRequiredService<IActaOperations>();

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var enqueued = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload)],
            ct
        );
        var jobId = enqueued[0].JobId;
        await Runtime.RunOnceAsync(jobId, ct);

        var seen = new List<EventListItem>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: jobId, PageSize: 1, Cursor: cursor), ct);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
            Assert.True(pages < 50, "pagination did not terminate");
        } while (cursor is not null);

        Assert.NotEmpty(seen);
        Assert.All(seen, e => Assert.Equal(jobId, e.JobId));
        for (var i = 1; i < seen.Count; i++)
        {
            var ordered =
                seen[i].CreatedAtUtc < seen[i - 1].CreatedAtUtc
                || (seen[i].CreatedAtUtc == seen[i - 1].CreatedAtUtc && seen[i].JobEventId < seen[i - 1].JobEventId);
            Assert.True(ordered, "rows are not in created_at DESC, id DESC order");
        }

        var withTotal = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: jobId, PageSize: 1, IncludeTotal: true), ct);
        Assert.Equal(seen.Count, withTotal.TotalCount);
    }

    [Fact(DisplayName = "ListJobEvents returns the keyset page and the job-scoped total from one command")]
    public async Task Combined_read_returns_page_and_filter_wide_total()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var enqueued = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload)],
            ct
        );
        var jobId = enqueued[0].JobId;
        await Runtime.RunOnceAsync(jobId, ct);

        var page = await Services
            .GetRequiredService<IEventStore>()
            .ListEventsAsync(
                new EventPageRequest(
                    jobId,
                    LineageRootId: null,
                    JobNamespace: null,
                    EventCode: null,
                    DefinitionId: null,
                    TenantId: null,
                    TenantKey: null,
                    WorkerId: null,
                    ActorCode: null,
                    ReasonCode: null,
                    CreatedFromUtc: null,
                    CreatedToUtc: null,
                    CursorCreatedAtUtc: null,
                    CursorId: null,
                    Take: 2,
                    IncludeTotal: true
                ),
                ct
            );
        var (rows, total) = (page.Rows, page.Total);

        Assert.NotEmpty(rows);
        Assert.True(rows.Count <= 2);
        Assert.All(rows, row => Assert.Equal(jobId, row.JobId));
        Assert.NotNull(total);
        Assert.True(total >= rows.Count);
        for (var i = 1; i < rows.Count; i++)
        {
            var ordered =
                rows[i].CreatedAtUtc < rows[i - 1].CreatedAtUtc
                || (rows[i].CreatedAtUtc == rows[i - 1].CreatedAtUtc && rows[i].JobEventId < rows[i - 1].JobEventId);
            Assert.True(ordered, "combined page rows are not in created_at DESC, id DESC order");
        }
    }
}
