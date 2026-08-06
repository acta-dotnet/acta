using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Tenants;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for <c>ListJobs</c> filter dimensions: each filter scopes the result set to exactly
/// the matching rows. One fact per filter: status, parentJobId, tenantId, namespace, jobName.
/// </summary>
[ConformanceSpec(
    "list-jobs.filter-matrix",
    "ListJobs filter-matrix selects exactly matching rows per dimension",
    Area = "Reads",
    Contract = "ListJobs filters partition the result to exactly the matching rows and exclude all non-matching rows for each filter dimension.",
    Arrange = "Job rows differing only by the filtered field are seeded per-test in isolation.",
    Act = "ListJobs runs once per filter dimension: status, parentJobId, tenantId, namespace, jobName, correlationKey, terminalOnly, and recurringOnly.",
    Assert = "The returned id set equals exactly the matching ids with non-matching ids absent."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.ListJobsAsync))]
public abstract class ListJobsFilterMatrixSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private JobEnqueueRow AddNumbersRow(
        string? tenantKey = null,
        long? parentId = null,
        string? correlationKey = null,
        IReadOnlyList<TagInput>? tags = null
    )
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        return new JobEnqueueRow(
            NamespaceName: TestNamespace,
            JobName: "add-numbers",
            Input: payload,
            TenantKey: tenantKey,
            ParentId: parentId,
            CorrelationKey: correlationKey,
            Tags: tags
        );
    }

    private async Task<IReadOnlyList<JobEnqueueOutcome>> EnqueueAsync(IReadOnlyList<JobEnqueueRow> rows, CancellationToken ct)
    {
        _ = Services.GetRequiredService<ISqlDialect>();
        return await EnqueueTestOps.EnqueueBatchAsync(Services, rows, ct);
    }

    [Fact(DisplayName = "Status filter returns only jobs at the specified status and the total matches the filtered count")]
    public async Task Status_filter_returns_exact_id_set_per_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var dialect = Services.GetRequiredService<ISqlDialect>();
        const int LeaseTtl = 300;

        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);

        // Enqueue 3 jobs; drive j3 to Failed via claim + start + complete
        var outcomes = await EnqueueAsync([AddNumbersRow(), AddNumbersRow(), AddNumbersRow()], ct);
        var j1 = outcomes[0].JobId;
        var j2 = outcomes[1].JobId;
        var j3 = outcomes[2].JobId;

        var c3 = Assert.Single(await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, worker!.Id, LeaseTtl, j3, ct));
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(c3.JobId, worker.Id, c3.ExecutionNumber, c3.Version, LeaseTtl, ct)
        );
        var cr3 = await Services
            .GetRequiredService<IExecutionStore>()
            .CompleteExecutionAsync(
                new CompleteExecutionRequest(
                    c3.JobId,
                    worker.Id,
                    c3.ExecutionNumber,
                    ExecutionOutcome.Failed,
                    0,
                    ReadOnlyMemory<byte>.Empty
                )
                {
                    HandlerStatusCode = (byte)JobStatusCode.Failed,
                },
                ct
            );
        Assert.Equal(CompleteExecutionAction.Completed, cr3.Action);

        var queries = Services.GetRequiredService<IActaOperations>();

        // JobName scopes out the recurring-ping slot job so only seeded rows appear in the Ready set
        var readyPage = await queries.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers", Status: JobStatusCode.Ready, IncludeTotal: true),
            ct
        );
        Assert.Equal([j1, j2], readyPage.Items.Select(static i => i.JobId).ToHashSet());
        Assert.Equal(2L, readyPage.TotalCount);

        var failedPage = await queries.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers", Status: JobStatusCode.Failed),
            ct
        );
        Assert.Equal([j3], failedPage.Items.Select(static i => i.JobId).ToHashSet());
    }

    [Fact(DisplayName = "TerminalOnly restricts to terminal rows and RecurringOnly to jobs with a live schedule attached")]
    public async Task TerminalOnly_and_RecurringOnly_flags_restrict_exactly()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        const int LeaseTtl = 300;

        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);

        // One row stays Ready, one is driven to terminal Failed.
        var outcomes = await EnqueueAsync([AddNumbersRow(), AddNumbersRow()], ct);
        var ready = outcomes[0].JobId;
        var failed = outcomes[1].JobId;

        var execution = Services.GetRequiredService<IExecutionStore>();
        var claim = Assert.Single(await execution.ClaimOneAsync(ns, worker!.Id, LeaseTtl, failed, ct));
        Assert.Equal(
            StartExecutionAction.Started,
            await execution.StartExecutionAsync(claim.JobId, worker.Id, claim.ExecutionNumber, claim.Version, LeaseTtl, ct)
        );
        var completed = await execution.CompleteExecutionAsync(
            new CompleteExecutionRequest(
                claim.JobId,
                worker.Id,
                claim.ExecutionNumber,
                ExecutionOutcome.Failed,
                0,
                ReadOnlyMemory<byte>.Empty
            )
            {
                HandlerStatusCode = (byte)JobStatusCode.Failed,
            },
            ct
        );
        Assert.Equal(CompleteExecutionAction.Completed, completed.Action);

        var queries = Services.GetRequiredService<IActaOperations>();

        // TerminalOnly: only the failed row survives; the Ready sibling is excluded and the total matches.
        var terminalPage = await queries.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers", TerminalOnly: true, IncludeTotal: true),
            ct
        );
        Assert.Equal([failed], terminalPage.Items.Select(static i => i.JobId).ToHashSet());
        Assert.Equal(1L, terminalPage.TotalCount);

        // RecurringOnly: only the namespace's recurring slot jobs carry a live schedule; the plain
        // enqueued add-numbers rows are excluded.
        var recurringPage = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, RecurringOnly: true), ct);
        var recurringNames = recurringPage.Items.Select(static i => i.JobName).ToHashSet();
        Assert.Contains("recurring-ping", recurringNames);
        Assert.DoesNotContain("add-numbers", recurringNames);
        Assert.DoesNotContain(recurringPage.Items, i => i.JobId == ready || i.JobId == failed);

        // False is a no-op, identical to the unfiltered read.
        var falsePage = await queries.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers", TerminalOnly: false, RecurringOnly: false),
            ct
        );
        Assert.Equal([ready, failed], falsePage.Items.Select(static i => i.JobId).ToHashSet());
    }

    [Fact(DisplayName = "ParentJobId filter returns exactly the direct children of that parent and no other children")]
    public async Task ParentJobId_filter_returns_exact_children()
    {
        var ct = TestContext.Current.CancellationToken;

        // Two parent jobs in the same namespace
        var parents = await EnqueueAsync([AddNumbersRow(), AddNumbersRow()], ct);
        var p1 = parents[0].JobId;
        var p2 = parents[1].JobId;

        // Two children under P1, one under P2
        var children1 = await EnqueueAsync([AddNumbersRow(parentId: p1), AddNumbersRow(parentId: p1)], ct);
        var c1 = children1[0].JobId;
        var c2 = children1[1].JobId;

        var children2 = await EnqueueAsync([AddNumbersRow(parentId: p2)], ct);
        var c3 = children2[0].JobId;

        var queries = Services.GetRequiredService<IActaOperations>();

        var p1Page = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, ParentJobId: p1), ct);
        Assert.Equal([c1, c2], p1Page.Items.Select(static i => i.JobId).ToHashSet());
        Assert.DoesNotContain(p1Page.Items, i => i.JobId == c3);

        var p2Page = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, ParentJobId: p2), ct);
        Assert.Equal([c3], p2Page.Items.Select(static i => i.JobId).ToHashSet());
        Assert.DoesNotContain(p2Page.Items, i => i.JobId == c1 || i.JobId == c2);
    }

    [Fact(DisplayName = "TenantId filter returns exactly the jobs for that tenant and excludes all other tenants' jobs")]
    public async Task TenantId_filter_returns_exact_tenant_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();

        var t1Key = TestKey("t1");
        var t2Key = TestKey("t2");
        var t1Id = await Services.GetRequiredService<TenantsService>().RegisterAsync(t1Key, null, null, ct);
        var t2Id = await Services.GetRequiredService<TenantsService>().RegisterAsync(t2Key, null, null, ct);

        // Two jobs under T1, one under T2
        var t1Outcomes = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [AddNumbersRow(tenantKey: t1Key), AddNumbersRow(tenantKey: t1Key)],
            ct
        );
        var t2Outcomes = await EnqueueTestOps.EnqueueBatchAsync(Services, [AddNumbersRow(tenantKey: t2Key)], ct);
        var ta = t1Outcomes[0].JobId;
        var tb = t1Outcomes[1].JobId;
        var tc = t2Outcomes[0].JobId;

        var queries = Services.GetRequiredService<IActaOperations>();

        var t1Page = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, TenantId: t1Id), ct);
        Assert.Equal([ta, tb], t1Page.Items.Select(static i => i.JobId).ToHashSet());
        Assert.DoesNotContain(t1Page.Items, i => i.JobId == tc);

        var t2Page = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, TenantId: t2Id), ct);
        Assert.Equal([tc], t2Page.Items.Select(static i => i.JobId).ToHashSet());

        // The key filter selects the same rows as the id filter, and list rows carry the resolved key.
        var keyPage = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, TenantKey: t1Key), ct);
        Assert.Equal([ta, tb], keyPage.Items.Select(static i => i.JobId).ToHashSet());
        Assert.All(keyPage.Items, i => Assert.Equal(t1Key, i.TenantKey));
    }

    [Fact(DisplayName = "Namespace filter returns only jobs in the requested namespace and the total matches the filtered count")]
    public async Task Namespace_filter_returns_exact_namespace_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        // Register a second namespace with all test job definitions so enqueue can resolve them
        var ns2Name = TestKey("ns2");
        var seeder = new ActaTestSeeder(Db);
        var ns2Id = await seeder.SeedJobNamespaceAsync(ns2Name, "test", ct);
        await DefinitionTestOps.RegisterAsync(Services, ns2Id, DateTime.UtcNow, TestJobsManifest.Descriptors.Descriptors, ct);

        // Enqueue two jobs in the primary namespace, one in the second
        var ns1Outcomes = await EnqueueAsync([AddNumbersRow(), AddNumbersRow()], ct);
        var j1 = ns1Outcomes[0].JobId;
        var j2 = ns1Outcomes[1].JobId;

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var ns2Outcomes = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [new JobEnqueueRow(NamespaceName: ns2Name, JobName: "add-numbers", Input: payload)],
            ct
        );
        var j3 = ns2Outcomes[0].JobId;

        var queries = Services.GetRequiredService<IActaOperations>();

        // Scope the primary namespace to the add-numbers job so the recurring-ping slot is excluded.
        // The first two rows should be present, the second namespace row absent, and the total exact.
        var ns1Page = await queries.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers", IncludeTotal: true),
            ct
        );
        var ns1Ids = ns1Page.Items.Select(static i => i.JobId).ToHashSet();
        Assert.Equal([j1, j2], ns1Ids);
        Assert.Equal(2L, ns1Page.TotalCount);

        // The second namespace contains only its own row and reports the matching total.
        var ns2Page = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: ns2Name, IncludeTotal: true), ct);
        Assert.Equal([j3], ns2Page.Items.Select(static i => i.JobId).ToHashSet());
        Assert.Equal(1L, ns2Page.TotalCount);
    }

    [Fact(DisplayName = "CorrelationKey filter returns exactly the jobs stamped with that id and excludes other correlation ids")]
    public async Task CorrelationKey_filter_returns_exact_matches()
    {
        var ct = TestContext.Current.CancellationToken;

        // Two jobs share correlation id "trace-a", one carries "trace-b", one carries none.
        var outcomes = await EnqueueAsync(
            [
                AddNumbersRow(correlationKey: "trace-a"),
                AddNumbersRow(correlationKey: "trace-a"),
                AddNumbersRow(correlationKey: "trace-b"),
                AddNumbersRow(),
            ],
            ct
        );
        var a1 = outcomes[0].JobId;
        var a2 = outcomes[1].JobId;
        var b1 = outcomes[2].JobId;
        var none = outcomes[3].JobId;

        var queries = Services.GetRequiredService<IActaOperations>();

        var aPage = await queries.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: TestNamespace, CorrelationKey: "trace-a", IncludeTotal: true),
            ct
        );
        Assert.Equal([a1, a2], aPage.Items.Select(static i => i.JobId).ToHashSet());
        Assert.Equal(2L, aPage.TotalCount);
        // The projected value round-trips onto the list item.
        Assert.All(aPage.Items, i => Assert.Equal("trace-a", i.CorrelationKey));

        var bPage = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, CorrelationKey: "trace-b"), ct);
        Assert.Equal([b1], bPage.Items.Select(static i => i.JobId).ToHashSet());
        Assert.DoesNotContain(bPage.Items, i => i.JobId == a1 || i.JobId == a2 || i.JobId == none);
    }

    [Fact(DisplayName = "JobName filter returns exactly the jobs for that definition name and excludes other names")]
    public async Task JobName_filter_returns_exact_definition_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        // Two add-numbers jobs
        var anOutcomes = await EnqueueAsync([AddNumbersRow(), AddNumbersRow()], ct);
        var an1 = anOutcomes[0].JobId;
        var an2 = anOutcomes[1].JobId;

        // Two echo jobs
        var echoPayload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new Echo("hi"));
        var echoOutcomes = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [
                new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "echo", Input: echoPayload),
                new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "echo", Input: echoPayload),
            ],
            ct
        );
        var e1 = echoOutcomes[0].JobId;
        var e2 = echoOutcomes[1].JobId;

        var queries = Services.GetRequiredService<IActaOperations>();

        // add-numbers filter: includes seeded add-numbers rows; excludes echo rows
        var addPage = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers"), ct);
        var addIds = addPage.Items.Select(static i => i.JobId).ToHashSet();
        Assert.Equal([an1, an2], addIds);

        // echo filter: includes echo rows; excludes add-numbers rows
        var echoPage = await queries.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, JobName: "echo"), ct);
        var echoIds = echoPage.Items.Select(static i => i.JobId).ToHashSet();
        Assert.Equal([e1, e2], echoIds);
    }

    [Fact(DisplayName = "Tag filters match by name and case-insensitive exact value")]
    public async Task Tag_filter_returns_exact_tagged_jobs()
    {
        var ct = TestContext.Current.CancellationToken;

        var outcomes = await EnqueueAsync(
            [
                AddNumbersRow(tags: [new TagInput("region", "EU-West"), new TagInput("tier", "Enterprise")]),
                AddNumbersRow(tags: [new TagInput("region", "eu-west")]),
                AddNumbersRow(tags: [new TagInput("region", "US-East"), new TagInput("tier", "Enterprise")]),
                AddNumbersRow(tags: [new TagInput("region")]),
            ],
            ct
        );
        var eu1 = outcomes[0].JobId;
        var eu2 = outcomes[1].JobId;
        var us = outcomes[2].JobId;
        var presenceOnly = outcomes[3].JobId;

        var queries = Services.GetRequiredService<IActaOperations>();

        foreach (var value in new[] { "eu-west", "EU-WEST", "Eu-West" })
        {
            var page = await queries.Ledger.ListJobsAsync(
                new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers", Tags: [new TagFilter("region", value)]),
                ct
            );
            Assert.Equal([eu1, eu2], page.Items.Select(static i => i.JobId).ToHashSet());
            Assert.DoesNotContain(page.Items, i => i.JobId == us || i.JobId == presenceOnly);
        }

        var presencePage = await queries.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers", Tags: [new TagFilter("region")]),
            ct
        );
        Assert.Equal([eu1, eu2, us, presenceOnly], presencePage.Items.Select(static i => i.JobId).ToHashSet());

        var andPage = await queries.Ledger.ListJobsAsync(
            new ListJobsQuery(
                JobNamespace: TestNamespace,
                JobName: "add-numbers",
                Tags: [new TagFilter("region", "eu-west"), new TagFilter("tier", "enterprise")]
            ),
            ct
        );
        Assert.Equal([eu1], andPage.Items.Select(static i => i.JobId).ToHashSet());
    }
}
