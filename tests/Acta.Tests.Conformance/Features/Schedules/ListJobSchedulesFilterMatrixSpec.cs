using System.Collections.Immutable;
using Acta.Features.Definitions;
using Acta.Features.Schedules;
using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>
/// Conformance for <c>ListJobSchedules</c> filter dimensions: each filter partitions the schedule result
/// set to exactly the matching rows and the opt-in total count applies the same filter as the row query.
/// </summary>
[ConformanceSpec(
    "list-job-schedules.filter-matrix",
    "ListJobSchedules filter-matrix selects exactly matching rows per dimension",
    Area = "Reads",
    Contract = "ListJobSchedules filters partition the schedule rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.",
    Arrange = "Schedule rows are seeded per-test in isolation along the filtered dimension.",
    Act = "ListJobSchedules runs once per filter dimension with the opt-in total.",
    Assert = "The returned schedule-id set equals exactly the matching ids with non-matching ids absent, and the total applies the same filter."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.ListJobSchedulesAsync))]
public abstract class ListJobSchedulesFilterMatrixSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string Cron5 = "*/5 * * * *";
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private short NsId => (short)Runtime.RegisteredNamespaceIds[TestNamespace];
    private IDbSession DbSession => Db;
    private ISqlDialect Dialect => Services.GetRequiredService<ISqlDialect>();
    private IJobs Queries => Services.GetRequiredService<IJobs>();

    /// <summary>Creates a minimal synthetic job definition in the given namespace and returns its id.</summary>
    private async Task<int> CreateDefinitionAsync(short nsId, string jobName, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, nsId, Generation, ImmutableArray.Create(Def(jobName)), ct);
        return map[jobName];
    }

    /// <summary>Registers a slot job and its schedules in the given namespace.</summary>
    private Task RegisterSchedulesAsync(
        short nsId,
        int defId,
        string jobName,
        DateTime? slotMin,
        IReadOnlyList<SlotSchedule> schedules,
        JobStatusCode slotStatus,
        CancellationToken ct
    ) =>
        ScheduleTestOps.RegisterAsync(
            Services,
            [
                new DefinitionSchedules(
                    nsId,
                    defId,
                    jobName,
                    0,
                    ReadOnlyMemory<byte>.Empty,
                    JobAuditLevelCode.Audit,
                    slotStatus,
                    slotMin,
                    schedules
                ),
            ],
            ct
        );

    private static SlotSchedule Slot(string name, DateTime cursor) =>
        new(name, Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor);

    private static JobDescriptor Def(string name) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            InvocationKind: default,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: default,
            MaxAttempts: 1,
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );

    [Fact(DisplayName = "JobName filter returns only that job's schedules and excludes all other jobs' schedules")]
    public async Task JobName_filter_returns_exact_schedule_id_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = NsId;
        var soon = DateTime.UtcNow.AddMinutes(10);

        // Job B: synthetic job alongside the manifest-seeded recurring-ping (job A)
        var jobBName = TestKey("sched-b");
        var defBId = await CreateDefinitionAsync(nsId, jobBName, ct);
        await RegisterSchedulesAsync(nsId, defBId, jobBName, soon, [Slot("tick", soon)], JobStatusCode.Ready, ct);

        // Capture IDs via namespace-only read (independent of jobName filter)
        var all = (await Queries.Schedules.ListAsync(new ListJobSchedulesQuery(JobNamespace: TestNamespace, PageSize: 100), ct)).Items;
        var aIds = all.Where(s => s.JobName == "recurring-ping").Select(s => s.JobScheduleId).ToHashSet();
        var bIds = all.Where(s => s.JobName == jobBName).Select(s => s.JobScheduleId).ToHashSet();
        Assert.NotEmpty(aIds);
        Assert.Equal(1, bIds.Count);

        // Filter by recurring-ping: exact set + total, job B excluded
        var aPage = await Queries.Schedules.ListAsync(
            new ListJobSchedulesQuery(JobNamespace: TestNamespace, JobName: "recurring-ping", IncludeTotal: true),
            ct
        );
        Assert.Equal(aIds, aPage.Items.Select(s => s.JobScheduleId).ToHashSet());
        Assert.Equal((long)aIds.Count, aPage.TotalCount);
        Assert.Empty(aPage.Items.Select(s => s.JobScheduleId).Intersect(bIds));

        // Filter by job B: exact set, recurring-ping excluded
        var bPage = await Queries.Schedules.ListAsync(new ListJobSchedulesQuery(JobNamespace: TestNamespace, JobName: jobBName), ct);
        Assert.Equal(bIds, bPage.Items.Select(s => s.JobScheduleId).ToHashSet());
        Assert.Empty(bPage.Items.Select(s => s.JobScheduleId).Intersect(aIds));
    }

    [Fact(DisplayName = "Origin filter returns only definition-sourced schedules with the filter-wide total matching")]
    public async Task Source_filter_returns_definition_sourced_schedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = NsId;
        var soon = DateTime.UtcNow.AddMinutes(10);

        // Add a second schedule so the namespace has a non-trivial set (all with Origin=Definition)
        var jobBName = TestKey("sched-src");
        var defBId = await CreateDefinitionAsync(nsId, jobBName, ct);
        await RegisterSchedulesAsync(nsId, defBId, jobBName, soon, [Slot("tick", soon)], JobStatusCode.Ready, ct);

        // Capture all schedule IDs via namespace read (independent of origin filter)
        var all = (await Queries.Schedules.ListAsync(new ListJobSchedulesQuery(JobNamespace: TestNamespace, PageSize: 100), ct)).Items;
        var defIds = all.Select(s => s.JobScheduleId).ToHashSet();
        Assert.True(defIds.Count >= 2);
        Assert.All(all, s => Assert.Equal(ScheduleOriginCode.Definition, s.Origin));

        // Origin=Definition returns exactly all namespace schedules + total
        var page = await Queries.Schedules.ListAsync(
            new ListJobSchedulesQuery(
                JobNamespace: TestNamespace,
                Origin: ScheduleOriginCode.Definition,
                PageSize: 100,
                IncludeTotal: true
            ),
            ct
        );
        Assert.Equal(defIds, page.Items.Select(s => s.JobScheduleId).ToHashSet());
        Assert.Equal((long)defIds.Count, page.TotalCount);
    }

    [Fact(DisplayName = "LiveOnly excludes orphaned schedules and liveOnly=false includes them")]
    public async Task LiveOnly_excludes_orphaned_schedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = NsId;
        var soon = DateTime.UtcNow.AddMinutes(10);

        // Job A: stays live
        var jobAName = TestKey("live");
        var defAId = await CreateDefinitionAsync(nsId, jobAName, ct);
        await RegisterSchedulesAsync(nsId, defAId, jobAName, soon, [Slot("tick", soon)], JobStatusCode.Ready, ct);

        // Job B: register then orphan by re-registering with no schedules
        var jobBName = TestKey("orphan");
        var defBId = await CreateDefinitionAsync(nsId, jobBName, ct);
        await RegisterSchedulesAsync(nsId, defBId, jobBName, soon, [Slot("gone", soon)], JobStatusCode.Ready, ct);
        await RegisterSchedulesAsync(nsId, defBId, jobBName, null, [], JobStatusCode.Paused, ct);

        // Capture all (including orphaned) via LiveOnly=false: independent of the liveOnly filter
        var allItems = (
            await Queries.Schedules.ListAsync(new ListJobSchedulesQuery(JobNamespace: TestNamespace, PageSize: 100, LiveOnly: false), ct)
        ).Items;
        var liveIds = allItems.Where(s => s.OrphanedAtUtc is null).Select(s => s.JobScheduleId).ToHashSet();
        var orphanedIds = allItems.Where(s => s.OrphanedAtUtc is not null).Select(s => s.JobScheduleId).ToHashSet();

        // Verify our seeded schedules landed in the right partitions
        Assert.Contains(allItems.Single(s => s.JobName == jobAName).JobScheduleId, liveIds);
        Assert.Contains(allItems.Single(s => s.JobName == jobBName).JobScheduleId, orphanedIds);

        // LiveOnly=true (default): exact liveIds, no orphaned, total = liveIds count
        var livePage = await Queries.Schedules.ListAsync(
            new ListJobSchedulesQuery(JobNamespace: TestNamespace, LiveOnly: true, PageSize: 100, IncludeTotal: true),
            ct
        );
        Assert.Equal(liveIds, livePage.Items.Select(s => s.JobScheduleId).ToHashSet());
        Assert.Equal((long)liveIds.Count, livePage.TotalCount);
        Assert.Empty(livePage.Items.Select(s => s.JobScheduleId).Intersect(orphanedIds));

        // LiveOnly=false: exact full set (live + orphaned)
        var allPage = await Queries.Schedules.ListAsync(
            new ListJobSchedulesQuery(JobNamespace: TestNamespace, LiveOnly: false, PageSize: 100),
            ct
        );
        Assert.Equal(allItems.Select(s => s.JobScheduleId).ToHashSet(), allPage.Items.Select(s => s.JobScheduleId).ToHashSet());
        Assert.Empty(liveIds.Intersect(orphanedIds));
    }

    [Fact(DisplayName = "JobNamespace filter scopes schedules to exactly one namespace and excludes all other namespaces")]
    public async Task Namespace_filter_isolates_to_one_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = NsId;
        var soon = DateTime.UtcNow.AddMinutes(10);

        // Add a synthetic schedule to the primary namespace so TotalCount > 0 is asserted against a pinned count
        var ns1JobName = TestKey("sched-ns1");
        var ns1DefId = await CreateDefinitionAsync(nsId, ns1JobName, ct);
        await RegisterSchedulesAsync(nsId, ns1DefId, ns1JobName, soon, [Slot("tick", soon)], JobStatusCode.Ready, ct);

        // Second namespace: one synthetic definition + schedule
        var ns2 = TestKey("ns2");
        var ns2Id = await new ActaTestSeeder(Db).SeedJobNamespaceAsync(ns2, "test", ct);
        var ns2JobName = TestKey("sched-ns2");
        var ns2DefId = await CreateDefinitionAsync(ns2Id, ns2JobName, ct);
        await RegisterSchedulesAsync(ns2Id, ns2DefId, ns2JobName, soon, [Slot("tick", soon)], JobStatusCode.Ready, ct);

        // Read each namespace independently (the reads are the independent origin for each partition)
        var ns1Page = await Queries.Schedules.ListAsync(
            new ListJobSchedulesQuery(JobNamespace: TestNamespace, PageSize: 100, IncludeTotal: true),
            ct
        );
        var ns2Page = await Queries.Schedules.ListAsync(
            new ListJobSchedulesQuery(JobNamespace: ns2, PageSize: 100, IncludeTotal: true),
            ct
        );

        var ns1Ids = ns1Page.Items.Select(s => s.JobScheduleId).ToHashSet();
        var ns2Ids = ns2Page.Items.Select(s => s.JobScheduleId).ToHashSet();

        // ns2 has exactly 1 (the one we seeded)
        Assert.Equal(1, ns2Ids.Count);
        Assert.Equal(1L, ns2Page.TotalCount);

        // ns1 has at least the manifest schedule plus our synthetic one
        Assert.True(ns1Ids.Count >= 2);
        Assert.Equal((long)ns1Ids.Count, ns1Page.TotalCount);

        // Cross-exclusion: neither namespace bleeds into the other
        Assert.Empty(ns1Ids.Intersect(ns2Ids));
    }
}
