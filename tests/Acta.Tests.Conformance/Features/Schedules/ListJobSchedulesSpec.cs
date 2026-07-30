using Acta.Modules.Execution.Schedules;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>
/// Conformance for the schedules list read: live schedules ordered next run first with keyset
/// pagination, an effective expression projection, and an opt-in total.
/// </summary>
[ConformanceSpec(
    "list-job-schedules.keyset-page",
    "ListJobSchedules pages live schedules next-run first without duplicates",
    Area = "Reads",
    Contract = "ListJobSchedules pages live schedules next-run first by cursor without duplicates and reads the page plus an opt-in filter-wide count in one command.",
    Arrange = "A namespace holds live schedules, including the system recurring definitions.",
    Act = "The schedules are walked one per page via NextCursor, then read again with IncludeTotal.",
    Assert = "The walk visits every live schedule once in ascending next-run order and the page plus filter-wide total arrive from one command."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.ListJobSchedulesAsync))]
public abstract class ListJobSchedulesSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    protected override bool RegisterFrameworkJobs => true;

    [Fact(
        DisplayName = "Walking NextCursor visits every live schedule once in ascending next-run order, excluding rows without a next run, with a matching total"
    )]
    public async Task Walks_live_schedules_in_next_run_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IJobs>();

        var seen = new List<JobScheduleListItem>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await queries.Schedules.ListAsync(
                new ListJobSchedulesQuery(JobNamespace: TestNamespace, PageSize: 1, Cursor: cursor),
                ct
            );
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
            Assert.True(pages < 200, "pagination did not terminate");
        } while (cursor is not null);

        Assert.NotEmpty(seen);
        Assert.All(seen, static s => Assert.NotNull(s.NextRunAtUtc));
        Assert.All(seen, static s => Assert.Null(s.OrphanedAtUtc));
        Assert.All(seen, static s => Assert.False(string.IsNullOrEmpty(s.Expression)));
        Assert.All(seen, static s => Assert.True(s.Version >= 0, "the list row must carry the schedule's optimistic-concurrency version"));
        Assert.Equal(seen.Count, seen.Select(static s => s.JobScheduleId).Distinct().Count());
        for (var i = 1; i < seen.Count; i++)
        {
            var ordered =
                seen[i - 1].NextRunAtUtc!.Value < seen[i].NextRunAtUtc!.Value
                || (seen[i - 1].NextRunAtUtc == seen[i].NextRunAtUtc && seen[i - 1].JobScheduleId < seen[i].JobScheduleId);
            Assert.True(ordered, "rows are not in next_run ASC, id ASC order");
        }

        var withTotal = await queries.Schedules.ListAsync(
            new ListJobSchedulesQuery(JobNamespace: TestNamespace, PageSize: 1, IncludeTotal: true),
            ct
        );
        Assert.Equal(seen.Count, withTotal.TotalCount);
    }

    [Fact(DisplayName = "ListJobSchedules returns the keyset page and the filter-wide total from one command")]
    public async Task Combined_read_returns_page_and_filter_wide_total()
    {
        var ct = TestContext.Current.CancellationToken;

        var store = Services.GetRequiredService<IScheduleStore>();
        var fullPage = await store.ListJobSchedulesAsync(
            new SchedulePageRequest(TestNamespace, null, null, null, null, null, Take: 1000, IncludeTotal: true),
            ct
        );

        Assert.NotEmpty(fullPage.Rows);
        Assert.Equal(fullPage.Rows.Count, fullPage.Total);

        var page = await store.ListJobSchedulesAsync(
            new SchedulePageRequest(TestNamespace, null, null, null, null, null, Take: 1, IncludeTotal: true),
            ct
        );

        Assert.Single(page.Rows);
        Assert.Equal(fullPage.Total, page.Total);
    }
}
