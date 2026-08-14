using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Tenants;
using Acta.Runtime.Modules.Operations.Events;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Events;

/// <summary>
/// Conformance for <c>ListJobEvents</c> filter dimensions: each filter partitions the event result
/// set to exactly the matching rows and the opt-in total count applies the same filter as the row
/// query.
/// </summary>
[ConformanceSpec(
    "list-job-events.filter-matrix",
    "ListJobEvents filter-matrix selects exactly matching rows per dimension",
    Area = "Reads",
    Contract = "ListJobEvents filters partition the event rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.",
    Arrange = "Event rows are seeded per-test in isolation along the filtered dimension.",
    Act = "ListJobEvents runs once per filter dimension with the opt-in total.",
    Assert = "The returned event-id set equals exactly the matching ids with non-matching ids absent, and the total applies the same filter."
)]
[CoversStoreMethod(typeof(IEventStore), nameof(IEventStore.ListEventsAsync))]
public abstract class ListJobEventsFilterMatrixSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private JobEnqueueRow AddNumbersRow(string? tenantKey = null, long? parentId = null)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        return new JobEnqueueRow(
            NamespaceName: TestNamespace,
            JobName: "add-numbers",
            Input: payload,
            TenantKey: tenantKey,
            ParentId: parentId
        );
    }

    private async Task<IReadOnlyList<JobEnqueueOutcome>> EnqueueAsync(IReadOnlyList<JobEnqueueRow> rows, CancellationToken ct)
    {
        _ = Services.GetRequiredService<ISqlDialect>();
        return await EnqueueTestOps.EnqueueBatchAsync(Services, rows, ct);
    }

    private async Task<HashSet<long>> EventIdsAsync(long jobId, CancellationToken ct)
    {
        var queries = Services.GetRequiredService<IActaOperations>();
        var page = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: jobId, PageSize: 100), ct);
        return [.. page.Items.Select(e => e.JobEventId)];
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
        Assert.Equal(j1Events, [.. j1Page.Items.Select(e => e.JobEventId)]);

        var j2Page = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j2, PageSize: 100), ct);
        Assert.Equal(j2Events, [.. j2Page.Items.Select(e => e.JobEventId)]);

        // Cross-exclusion: neither job's page contains the other job's events
        Assert.Empty(j1Page.Items.Select(e => e.JobEventId).Intersect(j2Events));
        Assert.Empty(j2Page.Items.Select(e => e.JobEventId).Intersect(j1Events));
    }

    [Fact(DisplayName = "LineageRootId filter returns all lineage events and excludes unrelated jobs")]
    public async Task LineageRootId_filter_returns_exact_lineage_events()
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

        // events.lineage_root_id = COALESCE(job.lineage_root_id, job.id), so both root and child
        // events share lineage_root_id = rootId; unrelated events carry their own id.
        var lineagePage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(LineageRootId: rootId, JobNamespace: TestNamespace, PageSize: 100),
            ct
        );
        var lineageIds = lineagePage.Items.Select(e => e.JobEventId).ToHashSet();

        Assert.Equal([.. rootEvents.Union(childEvents)], lineageIds);
    }

    [Fact(DisplayName = "JobNamespace filter scopes events to exactly one namespace")]
    public async Task Namespace_filter_returns_exact_namespace_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var queries = Services.GetRequiredService<IActaOperations>();

        // Register a second namespace with the test definitions
        var ns2Name = TestKey("ns2");
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
        Assert.Equal(j1Events, [.. ns1Page.Items.Select(e => e.JobEventId)]);

        // Filtering by ns2 + j1 returns nothing: j1 is not in ns2
        var ns2Page = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, JobNamespace: ns2Name, PageSize: 100), ct);
        Assert.Equal([], ns2Page.Items.Select(e => e.JobEventId).ToHashSet());
    }

    [Fact(DisplayName = "EventCode filter returns only events of that code and excludes all other codes")]
    public async Task EventCode_filter_returns_exact_code_partition()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var outcomes = await EnqueueAsync([AddNumbersRow()], ct);
        var j1 = outcomes[0].JobId;
        await Runtime.RunOnceAsync(j1, ct);
        var allEvents = await EventIdsAsync(j1, ct);
        Assert.NotEmpty(allEvents);

        // Filter to JobExecutionStarted: only started events
        var startedPage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: j1, EventCode: EventCode.JobExecutionStarted, PageSize: 100),
            ct
        );
        var startedIds = startedPage.Items.Select(e => e.JobEventId).ToHashSet();
        Assert.NotEmpty(startedIds);
        Assert.All(startedPage.Items, e => Assert.Equal(EventCode.JobExecutionStarted, e.EventCode));

        // Filter to JobExecutionFinished: only finished events
        var finishedPage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: j1, EventCode: EventCode.JobExecutionFinished, PageSize: 100),
            ct
        );
        var finishedIds = finishedPage.Items.Select(e => e.JobEventId).ToHashSet();
        Assert.NotEmpty(finishedIds);
        Assert.All(finishedPage.Items, e => Assert.Equal(EventCode.JobExecutionFinished, e.EventCode));

        // The two filtered sets are disjoint and together equal the full job event set
        Assert.Empty(startedIds.Intersect(finishedIds));
        Assert.Equal(allEvents, [.. startedIds.Union(finishedIds)]);
    }

    [Fact(DisplayName = "JobDefinitionId filter partitions events and applies uniformly to the row count")]
    public async Task JobDefinitionId_filter_partitions_events_and_count_is_definition_scoped()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        // Two jobs from different definitions (add-numbers vs echo)
        var j1Outcomes = await EnqueueAsync([AddNumbersRow()], ct);
        var j1 = j1Outcomes[0].JobId;

        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var echoPayload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new Echo("hi"));
        var j2Outcomes = await EnqueueAsync([new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "echo", Input: echoPayload)], ct);
        var j2 = j2Outcomes[0].JobId;

        await Runtime.RunOnceAsync(j1, ct);
        await Runtime.RunOnceAsync(j2, ct);

        var j1Events = await EventIdsAsync(j1, ct);
        var j2Events = await EventIdsAsync(j2, ct);
        Assert.NotEmpty(j1Events);
        Assert.NotEmpty(j2Events);

        // Resolve definition ids from the events
        var j1EventPage = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, PageSize: 1), ct);
        var j1DefId = j1EventPage.Items[0].JobDefinitionId;
        var j2EventPage = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j2, PageSize: 1), ct);
        var j2DefId = j2EventPage.Items[0].JobDefinitionId;
        Assert.NotNull(j1DefId);
        Assert.NotNull(j2DefId);
        Assert.NotEqual(j1DefId, j2DefId);

        // Definition filter partitions: j1DefId → j1 events; j2DefId → j2 events
        var def1Page = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobNamespace: TestNamespace, JobDefinitionId: j1DefId, PageSize: 100),
            ct
        );
        Assert.Equal(j1Events, [.. def1Page.Items.Select(e => e.JobEventId)]);

        var def2Page = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobNamespace: TestNamespace, JobDefinitionId: j2DefId, PageSize: 100),
            ct
        );
        Assert.Equal(j2Events, [.. def2Page.Items.Select(e => e.JobEventId)]);

        // Count scope: with jobId + matching definition, TotalCount equals the filtered row count.
        // The COUNT query applies all filters uniformly: definition filter affects the total as it does rows.
        var j1Count = (long)j1Events.Count;
        var withCountPage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: j1, JobDefinitionId: j1DefId, IncludeTotal: true, PageSize: 100),
            ct
        );
        Assert.Equal(j1Count, withCountPage.TotalCount);

        // Mismatched definition: both rows and count reflect the filter (0), not a job-wide total.
        // This pins the actual SQL behavior where COUNT applies the definition filter like SELECT does.
        var mismatchPage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: j1, JobDefinitionId: j2DefId, IncludeTotal: true, PageSize: 100),
            ct
        );
        Assert.Equal([], mismatchPage.Items.Select(e => e.JobEventId).ToHashSet());
        Assert.Equal(0L, mismatchPage.TotalCount);
    }

    [Fact(DisplayName = "TenantId filter returns only events for that tenant and excludes other tenants")]
    public async Task TenantId_filter_returns_exact_tenant_events()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();
        var dialect = Services.GetRequiredService<ISqlDialect>();

        var t1Key = TestKey("t1");
        var t2Key = TestKey("t2");
        var t1Id = await Services.GetRequiredService<TenantsService>().RegisterAsync(t1Key, null, null, ct);
        var t2Id = await Services.GetRequiredService<TenantsService>().RegisterAsync(t2Key, null, null, ct);

        // Two jobs under t1, one under t2
        var t1Outcomes = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [AddNumbersRow(tenantKey: t1Key), AddNumbersRow(tenantKey: t1Key)],
            ct
        );
        var t2Outcomes = await EnqueueTestOps.EnqueueBatchAsync(Services, [AddNumbersRow(tenantKey: t2Key)], ct);
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
        Assert.Equal(taEvents.Union(tbEvents).ToHashSet(), [.. t1Page.Items.Select(e => e.JobEventId)]);

        // t2 filter: tc events only; ta and tb excluded
        var t2Page = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(TenantId: t2Id, JobNamespace: TestNamespace, PageSize: 100),
            ct
        );
        Assert.Equal(tcEvents, [.. t2Page.Items.Select(e => e.JobEventId)]);

        // The key filter selects the same rows as the id filter.
        var keyPage = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(TenantKey: t1Key, JobNamespace: TestNamespace, PageSize: 100),
            ct
        );
        Assert.Equal(taEvents.Union(tbEvents).ToHashSet(), [.. keyPage.Items.Select(e => e.JobEventId)]);
    }

    [Fact(DisplayName = "ActorCode filter partitions the timeline by each actor present on it")]
    public async Task ActorCode_filter_partitions_by_actor()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        // Cancelling a Ready job stamps an Operator-actor event alongside the enqueue-time actor.
        var outcomes = await EnqueueAsync([AddNumbersRow()], ct);
        var j1 = outcomes[0].JobId;
        await Jobs.CancelAsync(JobLookup.ById(j1), "spec cancel", "op", ct);

        var all = (await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, PageSize: 100), ct)).Items;
        Assert.NotEmpty(all);
        Assert.Contains(all, e => e.ActorCode == ActorCode.Operator);

        var union = new HashSet<long>();
        foreach (var actor in all.Select(e => e.ActorCode).Distinct())
        {
            var expected = all.Where(e => e.ActorCode == actor).Select(e => e.JobEventId).ToHashSet();
            var page = await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, ActorCode: actor, PageSize: 100), ct);
            var got = page.Items.Select(e => e.JobEventId).ToHashSet();
            Assert.Equal(expected, got);
            Assert.All(page.Items, e => Assert.Equal(actor, e.ActorCode));
            union.UnionWith(got);
        }

        Assert.Equal([.. all.Select(e => e.JobEventId)], union);
    }

    [Fact(DisplayName = "ReasonCode filter returns only events carrying that reason and excludes reasonless ones")]
    public async Task ReasonCode_filter_returns_exact_reason_partition()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var outcomes = await EnqueueAsync([AddNumbersRow()], ct);
        var j1 = outcomes[0].JobId;
        await Jobs.CancelAsync(JobLookup.ById(j1), "spec cancel", "op", ct);

        var all = (await queries.Ledger.ListEventsAsync(new ListEventsQuery(JobId: j1, PageSize: 100), ct)).Items;
        Assert.Contains(all, e => e.ReasonCode == JobEventReasonCode.JobControlManual);

        var expected = all.Where(e => e.ReasonCode == JobEventReasonCode.JobControlManual).Select(e => e.JobEventId).ToHashSet();
        var page = await queries.Ledger.ListEventsAsync(
            new ListEventsQuery(JobId: j1, ReasonCode: JobEventReasonCode.JobControlManual, PageSize: 100),
            ct
        );
        Assert.Equal(expected, [.. page.Items.Select(e => e.JobEventId)]);
        Assert.All(page.Items, e => Assert.Equal(JobEventReasonCode.JobControlManual, e.ReasonCode));
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

        var fromSet = fromIds.Select(e => e.JobEventId).ToHashSet();
        var toSet = toIds.Select(e => e.JobEventId).ToHashSet();
        Assert.Empty(fromSet.Intersect(toSet));
        Assert.Equal(all.Select(e => e.JobEventId).ToHashSet(), [.. fromSet.Union(toSet)]);
    }
}
