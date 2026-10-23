using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Ledger;

/// <summary>
/// Conformance for <c>ListJobEvents</c> filter dimensions: each filter partitions the event result
/// set to exactly the matching rows and the opt-in total count applies the same filter as the row
/// query.
/// </summary>
[ConformanceSpec(
    "list-job-events.filter-matrix",
    "ListJobEvents filter-matrix selects exactly matching rows per dimension",
    Area = "Reads",
    Contract = "ListJobEvents filters partition the event rows to exactly the matching ids and exclude all non-matching ids for each filter dimension."
)]
public sealed class ListEventsFilterMatrixSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private JobEnqueueValues AddNumbersRow(string? tenantName = null, long? parentId = null)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        return new JobEnqueueValues(
            NamespaceName: TestNamespace,
            JobName: "add-numbers",
            Input: payload,
            TenantName: tenantName,
            ParentJobId: parentId
        );
    }

    private async Task<IReadOnlyList<EnqueueJobResponse>> EnqueueAsync(IReadOnlyList<JobEnqueueValues> rows, CancellationToken ct)
    {
        _ = Services.GetRequiredService<ISqlDialect>();
        return (await EnqueueTestOps.EnqueueBatchAsync(Services, rows, ct)).Jobs;
    }

    private async Task<HashSet<long>> EventIdsAsync(long jobId, CancellationToken ct)
    {
        var queries = Services.GetRequiredService<IActaOperations>();
        var page = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: jobId, PageSize: 100), ct);
        return [.. page.Items.Select(e => e.EventId)];
    }

    [Fact(DisplayName = "JobId filter returns only that job's events and excludes all other jobs' events")]
    public async Task JobId_filter_returns_exact_event_id_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var outcomes = await EnqueueAsync([AddNumbersRow(), AddNumbersRow()], ct);
        var j1 = outcomes[0].JobId;
        var j2 = outcomes[1].JobId;
        await Runtime.RunOnceAsync(j1, ct);
        await Runtime.RunOnceAsync(j2, ct);

        var j1Events = await EventIdsAsync(j1, ct);
        var j2Events = await EventIdsAsync(j2, ct);
        Assert.NotEmpty(j1Events);
        Assert.NotEmpty(j2Events);

        var j1Page = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, PageSize: 100), ct);
        Assert.Equal(j1Events, [.. j1Page.Items.Select(e => e.EventId)]);

        var j2Page = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j2, PageSize: 100), ct);
        Assert.Equal(j2Events, [.. j2Page.Items.Select(e => e.EventId)]);

        // Cross-exclusion: neither job's page contains the other job's events
        Assert.Empty(j1Page.Items.Select(e => e.EventId).Intersect(j2Events));
        Assert.Empty(j2Page.Items.Select(e => e.EventId).Intersect(j1Events));
    }

    [Fact(DisplayName = "LineageRootJobId filter returns all lineage events and excludes unrelated jobs")]
    public async Task LineageRootJobId_filter_returns_exact_lineage_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        // Root → child, plus an unrelated job
        var roots = await EnqueueAsync([AddNumbersRow()], ct);
        var rootId = roots[0].JobId;

        var children = await EnqueueAsync([AddNumbersRow(parentId: rootId)], ct);
        var childId = children[0].JobId;

        var unrelated = await EnqueueAsync([AddNumbersRow()], ct);
        var unrelatedId = unrelated[0].JobId;

        await Runtime.RunOnceAsync(rootId, ct);
        await Runtime.RunOnceAsync(childId, ct);
        await Runtime.RunOnceAsync(unrelatedId, ct);

        var rootEvents = await EventIdsAsync(rootId, ct);
        var childEvents = await EventIdsAsync(childId, ct);
        var unrelatedEvents = await EventIdsAsync(unrelatedId, ct);
        Assert.NotEmpty(rootEvents);
        Assert.NotEmpty(childEvents);
        Assert.NotEmpty(unrelatedEvents);

        // events.lineage_root_job_id = COALESCE(job.lineage_root_job_id, job.job_id), so both root and child
        // events share lineage_root_job_id = rootId; unrelated events carry their own id.
        var lineagePage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(LineageRootJobId: rootId, JobNamespace: TestNamespace, PageSize: 100),
            ct
        );
        var lineageIds = lineagePage.Items.Select(e => e.EventId).ToHashSet();

        Assert.Equal([.. rootEvents.Union(childEvents)], lineageIds);
    }

    [Fact(DisplayName = "JobNamespace filter scopes events to exactly one namespace")]
    public async Task Namespace_filter_returns_exact_namespace_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var queries = Services.GetRequiredService<IActaOperations>();

        // Register a second namespace with the test definitions
        var ns2Name = Unique("ns2");
        var seeder = new ActaTestSeeder(Db);
        var ns2Id = await seeder.SeedJobNamespaceAsync(ns2Name, "test", ct);
        await DefinitionTestOps.RegisterAsync(Services, ns2Id, DateTime.UtcNow, TestJobsManifest.Descriptors.Descriptors, ct);

        // Run one job in the primary namespace
        var outcomes = await EnqueueAsync([AddNumbersRow()], ct);
        var j1 = outcomes[0].JobId;
        await Runtime.RunOnceAsync(j1, ct);
        var j1Events = await EventIdsAsync(j1, ct);
        Assert.NotEmpty(j1Events);

        // j1 belongs to TestNamespace: filtering by TestNamespace + j1 returns all its events
        var ns1Page = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, JobNamespace: TestNamespace, PageSize: 100), ct);
        Assert.Equal(j1Events, [.. ns1Page.Items.Select(e => e.EventId)]);

        // Filtering by ns2 + j1 returns nothing: j1 is not in ns2
        var ns2Page = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, JobNamespace: ns2Name, PageSize: 100), ct);
        Assert.Equal([], ns2Page.Items.Select(e => e.EventId).ToHashSet());
    }

    [Fact(DisplayName = "EventCode filter returns only events of that code and excludes all other codes")]
    public async Task EventCode_filter_returns_exact_code_partition()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        // A reprioritize before the run gives the job a second event code beside its finished event.
        var outcomes = await EnqueueAsync([AddNumbersRow()], ct);
        var j1 = outcomes[0].JobId;
        Assert.Equal(ControlAction.Applied, (await Jobs.ReprioritizeAsync(outcomes[0], JobPriorityCode.High, ct: ct)).Action);
        await Runtime.RunOnceAsync(j1, ct);
        var allEvents = await EventIdsAsync(j1, ct);
        Assert.NotEmpty(allEvents);

        // Filter to JobReprioritized: only reprioritized events
        var reprioritizedPage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: j1, EventCode: EventCode.JobReprioritized, PageSize: 100),
            ct
        );
        var reprioritizedIds = reprioritizedPage.Items.Select(e => e.EventId).ToHashSet();
        Assert.NotEmpty(reprioritizedIds);
        Assert.All(reprioritizedPage.Items, e => Assert.Equal(EventCode.JobReprioritized, e.EventCode));

        // Filter to JobExecutionSucceeded: only the attempt's own event
        var finishedPage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: j1, EventCode: EventCode.JobExecutionSucceeded, PageSize: 100),
            ct
        );
        var finishedIds = finishedPage.Items.Select(e => e.EventId).ToHashSet();
        Assert.NotEmpty(finishedIds);
        Assert.All(finishedPage.Items, e => Assert.Equal(EventCode.JobExecutionSucceeded, e.EventCode));

        // The two filtered sets are disjoint and together equal the full job event set
        Assert.Empty(reprioritizedIds.Intersect(finishedIds));
        Assert.Equal(allEvents, [.. reprioritizedIds.Union(finishedIds)]);
    }

    [Fact(DisplayName = "DefinitionId filter partitions events and applies uniformly to the row count")]
    public async Task JobDefinitionId_filter_partitions_events_and_count_is_definition_scoped()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        // Two jobs from different definitions (add-numbers vs echo)
        var j1Outcomes = await EnqueueAsync([AddNumbersRow()], ct);
        var j1 = j1Outcomes[0].JobId;

        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var echoPayload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new Echo("hi"));
        var j2Outcomes = await EnqueueAsync([new JobEnqueueValues(NamespaceName: TestNamespace, JobName: "echo", Input: echoPayload)], ct);
        var j2 = j2Outcomes[0].JobId;

        await Runtime.RunOnceAsync(j1, ct);
        await Runtime.RunOnceAsync(j2, ct);

        var j1Events = await EventIdsAsync(j1, ct);
        var j2Events = await EventIdsAsync(j2, ct);
        Assert.NotEmpty(j1Events);
        Assert.NotEmpty(j2Events);

        // Resolve definition ids from the events
        var j1EventPage = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, PageSize: 1), ct);
        var j1DefId = j1EventPage.Items[0].DefinitionId;
        var j2EventPage = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j2, PageSize: 1), ct);
        var j2DefId = j2EventPage.Items[0].DefinitionId;
        Assert.NotNull(j1DefId);
        Assert.NotNull(j2DefId);
        Assert.NotEqual(j1DefId, j2DefId);

        // Definition filter partitions: j1DefId → j1 events; j2DefId → j2 events
        var def1Page = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobNamespace: TestNamespace, DefinitionId: j1DefId, PageSize: 100),
            ct
        );
        Assert.Equal(j1Events, [.. def1Page.Items.Select(e => e.EventId)]);

        var def2Page = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobNamespace: TestNamespace, DefinitionId: j2DefId, PageSize: 100),
            ct
        );
        Assert.Equal(j2Events, [.. def2Page.Items.Select(e => e.EventId)]);

        // Count scope: with jobId + matching definition, TotalCount equals the filtered row count.
        // The COUNT query applies all filters uniformly: definition filter affects the total as it does rows.
        var j1Count = (long)j1Events.Count;
        var withCountPage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: j1, DefinitionId: j1DefId, IncludeTotal: true, PageSize: 100),
            ct
        );
        Assert.Equal(j1Count, withCountPage.TotalCount);

        // Mismatched definition: both rows and count reflect the filter (0), not a job-wide total.
        // This pins the actual SQL behavior where COUNT applies the definition filter like SELECT does.
        var mismatchPage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: j1, DefinitionId: j2DefId, IncludeTotal: true, PageSize: 100),
            ct
        );
        Assert.Equal([], mismatchPage.Items.Select(e => e.EventId).ToHashSet());
        Assert.Equal(0L, mismatchPage.TotalCount);
    }

    [Fact(DisplayName = "TenantId filter returns only events for that tenant and excludes other tenants")]
    public async Task TenantId_filter_returns_exact_tenant_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();
        var dialect = Services.GetRequiredService<ISqlDialect>();

        var t1Key = Unique("t1");
        var t2Key = Unique("t2");
        var t1Id = (await Operations.Tenants.RegisterAsync(t1Key, null, null, ct)).TenantId;
        var t2Id = (await Operations.Tenants.RegisterAsync(t2Key, null, null, ct)).TenantId;

        // Two jobs under t1, one under t2
        var t1Outcomes = (
            await EnqueueTestOps.EnqueueBatchAsync(Services, [AddNumbersRow(tenantName: t1Key), AddNumbersRow(tenantName: t1Key)], ct)
        ).Jobs;
        var t2Outcomes = (await EnqueueTestOps.EnqueueBatchAsync(Services, [AddNumbersRow(tenantName: t2Key)], ct)).Jobs;
        var ta = t1Outcomes[0].JobId;
        var tb = t1Outcomes[1].JobId;
        var tc = t2Outcomes[0].JobId;

        await Runtime.RunOnceAsync(ta, ct);
        await Runtime.RunOnceAsync(tb, ct);
        await Runtime.RunOnceAsync(tc, ct);

        var taEvents = await EventIdsAsync(ta, ct);
        var tbEvents = await EventIdsAsync(tb, ct);
        var tcEvents = await EventIdsAsync(tc, ct);
        Assert.NotEmpty(taEvents);
        Assert.NotEmpty(tbEvents);
        Assert.NotEmpty(tcEvents);

        // t1 filter: ta and tb events; tc excluded
        var t1Page = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(TenantId: t1Id, JobNamespace: TestNamespace, PageSize: 100),
            ct
        );
        Assert.Equal(taEvents.Union(tbEvents).ToHashSet(), [.. t1Page.Items.Select(e => e.EventId)]);

        // t2 filter: tc events only; ta and tb excluded
        var t2Page = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(TenantId: t2Id, JobNamespace: TestNamespace, PageSize: 100),
            ct
        );
        Assert.Equal(tcEvents, [.. t2Page.Items.Select(e => e.EventId)]);

        // The key filter selects the same rows as the id filter.
        var keyPage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(TenantName: t1Key, JobNamespace: TestNamespace, PageSize: 100),
            ct
        );
        Assert.Equal(taEvents.Union(tbEvents).ToHashSet(), [.. keyPage.Items.Select(e => e.EventId)]);
    }

    [Fact(DisplayName = "The subject filters select exactly one subject's events, and a kind alone selects that kind")]
    public async Task Entity_filters_select_one_subject()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var outcomes = await EnqueueAsync([AddNumbersRow(), AddNumbersRow()], ct);
        var j1 = outcomes[0].JobId;
        var j2 = outcomes[1].JobId;
        await Jobs.CancelAsync(JobLookup.ById(j1), "spec cancel", "op", ct: ct);
        await Jobs.CancelAsync(JobLookup.ById(j2), "spec cancel", "op", ct: ct);

        var byJob = (await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, PageSize: 100), ct)).Items;
        Assert.NotEmpty(byJob);
        Assert.All(
            byJob,
            e =>
            {
                Assert.Equal(EntityCode.Job, e.EntityKind);
                Assert.Equal(j1, e.EntityId);
            }
        );

        var bySubject = (
            await queries.Ledger.ListEventsAsync(new ListEventsQuery(EntityKind: EntityCode.Job, EntityId: j1, PageSize: 100), ct)
        ).Items;
        Assert.Equal(byJob.Select(e => e.EventId).ToHashSet(), [.. bySubject.Select(e => e.EventId)]);

        var byKind = (
            await queries.Ledger.ListEventsAsync(
                new ListEventsQuery(JobNamespace: TestNamespace, EntityKind: EntityCode.Job, PageSize: 100),
                ct
            )
        ).Items;
        Assert.All(byKind, e => Assert.Equal(EntityCode.Job, e.EntityKind));
        Assert.Contains(byKind, e => e.EntityId == j1);
        Assert.Contains(byKind, e => e.EntityId == j2);
    }

    [Fact(DisplayName = "An operator verb's event carries the operator's message and the principal in its details")]
    public async Task Operator_event_carries_message_and_actor()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var outcomes = await EnqueueAsync([AddNumbersRow()], ct);
        var j1 = outcomes[0].JobId;
        await Jobs.CancelAsync(JobLookup.ById(j1), "spec cancel", "op", ct: ct);

        var page = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: j1, EventCode: EventCode.JobCancelled, PageSize: 100),
            ct
        );
        var cancelled = Assert.Single(page.Items);
        Assert.Equal("spec cancel", cancelled.Message);
        Assert.Equal("""{"actor":"op"}""", cancelled.DetailsText);
    }

    [Fact(DisplayName = "CreatedFromUtc and CreatedToUtc split the timeline at a boundary instant")]
    public async Task Created_range_filters_split_at_boundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var outcomes = await EnqueueAsync([AddNumbersRow()], ct);
        var j1 = outcomes[0].JobId;
        await Runtime.RunOnceAsync(j1, ct);

        var all = (await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, PageSize: 100), ct)).Items;
        Assert.NotEmpty(all);
        var boundary = all.Max(e => e.CreatedAtUtc);

        var fromIds = (
            await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, CreatedFromUtc: boundary, PageSize: 100), ct)
        ).Items;
        var toIds = (await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, CreatedToUtc: boundary, PageSize: 100), ct)).Items;

        // Inclusive lower bound keeps the boundary instant; exclusive upper bound drops it.
        Assert.All(fromIds, e => Assert.True(e.CreatedAtUtc >= boundary));
        Assert.All(toIds, e => Assert.True(e.CreatedAtUtc < boundary));
        Assert.NotEmpty(fromIds);

        var fromSet = fromIds.Select(e => e.EventId).ToHashSet();
        var toSet = toIds.Select(e => e.EventId).ToHashSet();
        Assert.Empty(fromSet.Intersect(toSet));
        Assert.Equal(all.Select(e => e.EventId).ToHashSet(), [.. fromSet.Union(toSet)]);
    }
}
