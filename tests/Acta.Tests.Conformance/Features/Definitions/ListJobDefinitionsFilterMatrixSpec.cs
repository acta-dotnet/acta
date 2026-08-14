using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Definitions;

/// <summary>
/// Conformance for <c>ListJobDefinitions</c> filter dimensions: each filter partitions the definition
/// result set to exactly the matching rows and the opt-in total count applies the same filter as the
/// row query.
/// </summary>
[ConformanceSpec(
    "list-job-definitions.filter-matrix",
    "ListJobDefinitions filter-matrix selects exactly matching rows per dimension",
    Area = "Reads",
    Contract = "ListJobDefinitions filters partition the definition rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.",
    Arrange = "Definition rows are seeded per-test in isolation along the filtered dimension.",
    Act = "ListJobDefinitions runs once per filter dimension with the opt-in total.",
    Assert = "The returned definition-id set equals exactly the matching ids with non-matching ids absent, and the total applies the same filter."
)]
[CoversStoreMethod(typeof(IDefinitionStore), nameof(IDefinitionStore.ListDefinitionsAsync))]
public abstract class ListJobDefinitionsFilterMatrixSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime Gen = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Creates a minimal synthetic job descriptor for a given job name.</summary>
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

    [Fact(
        DisplayName = "Status filter partitions definitions by status and each partition excludes all definitions with different statuses"
    )]
    public async Task Status_filter_partitions_definitions_by_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var queries = Services.GetRequiredService<IActaOperations>();

        // Fresh namespace so we control exactly which definitions exist and no manifest defs interfere
        var nsName = TestKey("defs");
        var nsId = await new ActaTestSeeder(Db).SeedJobNamespaceAsync(nsName, "test", ct);

        // Register def-a and def-b → both Active
        var defAName = TestKey("def-a");
        var defBName = TestKey("def-b");
        var firstMap = await DefinitionTestOps.RegisterAsync(Services, nsId, Gen, [Def(defAName), Def(defBName)], ct);
        var defAId = firstMap[defAName];
        var defBId = firstMap[defBName];

        // Re-register with only def-b: def-a is absent from manifest → def-a gets Retired
        await DefinitionTestOps.RegisterAsync(Services, nsId, Gen, [Def(defBName)], ct);

        var activeIds = new HashSet<int> { defBId };
        var retiredIds = new HashSet<int> { defAId };

        // Active filter: only def-b, exact set + total, def-a excluded
        var activePage = await queries.Definitions.ListAsync(
            new ListDefinitionsQuery(JobNamespace: nsName, Status: JobDefinitionStatusCode.Active, IncludeTotal: true),
            ct
        );
        Assert.Equal(activeIds, [.. activePage.Items.Select(d => d.JobDefinitionId)]);
        Assert.Equal(1L, activePage.TotalCount);
        Assert.Empty(activePage.Items.Select(d => d.JobDefinitionId).Intersect(retiredIds));

        // Retired filter: only def-a, def-b excluded
        var retiredPage = await queries.Definitions.ListAsync(
            new ListDefinitionsQuery(JobNamespace: nsName, Status: JobDefinitionStatusCode.Retired),
            ct
        );
        Assert.Equal(retiredIds, [.. retiredPage.Items.Select(d => d.JobDefinitionId)]);
        Assert.Empty(retiredPage.Items.Select(d => d.JobDefinitionId).Intersect(activeIds));
    }

    [Fact(DisplayName = "NameContains filter selects definitions whose name carries the term anywhere, not only as a prefix")]
    public async Task Name_contains_filter_matches_anywhere_in_the_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var nsName = TestKey("defs-contains");
        var nsId = await new ActaTestSeeder(Db).SeedJobNamespaceAsync(nsName, "test", ct);

        // Two names share an interior term, one does not, so the filter has to partition.
        var invoiceSend = TestKey("invoice-send");
        var invoiceVoid = TestKey("invoice-void");
        var receiptSend = TestKey("receipt-send");
        var map = await DefinitionTestOps.RegisterAsync(Services, nsId, Gen, [Def(invoiceSend), Def(invoiceVoid), Def(receiptSend)], ct);

        // TestKey prefixes every name with a run token, so the bare term is deliberately NOT a
        // prefix of any name: a starts-with implementation returns nothing here.
        Assert.DoesNotContain("invoice", invoiceSend[..1]);
        var matching = new HashSet<int> { map[invoiceSend], map[invoiceVoid] };

        var page = await queries.Definitions.ListAsync(
            new ListDefinitionsQuery(JobNamespace: nsName, NameContains: "invoice", IncludeTotal: true),
            ct
        );

        Assert.Equal(matching, [.. page.Items.Select(d => d.JobDefinitionId)]);
        Assert.DoesNotContain(map[receiptSend], page.Items.Select(d => d.JobDefinitionId));
        // The opt-in total must apply the same predicate as the row query.
        Assert.Equal((long)matching.Count, page.TotalCount);
    }

    [Fact(DisplayName = "JobNamespace filter scopes definitions to exactly one namespace and excludes all other namespaces")]
    public async Task Namespace_filter_isolates_to_one_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var queries = Services.GetRequiredService<IActaOperations>();

        // Two fresh namespaces so counts are exact and manifest defs do not interfere
        var ns1Name = TestKey("ns1");
        var ns2Name = TestKey("ns2");
        var seeder = new ActaTestSeeder(Db);
        var ns1Id = await seeder.SeedJobNamespaceAsync(ns1Name, "test", ct);
        var ns2Id = await seeder.SeedJobNamespaceAsync(ns2Name, "test", ct);

        // ns1: 2 definitions
        var defA1Name = TestKey("def-ns1-a");
        var defB1Name = TestKey("def-ns1-b");
        var ns1Map = await DefinitionTestOps.RegisterAsync(Services, ns1Id, Gen, [Def(defA1Name), Def(defB1Name)], ct);
        var defA1Id = ns1Map[defA1Name];
        var defB1Id = ns1Map[defB1Name];

        // ns2: 1 definition
        var defNs2Name = TestKey("def-ns2");
        var ns2Map = await DefinitionTestOps.RegisterAsync(Services, ns2Id, Gen, [Def(defNs2Name)], ct);
        var defNs2Id = ns2Map[defNs2Name];

        // Read each namespace independently
        var ns1Page = await queries.Definitions.ListAsync(
            new ListDefinitionsQuery(JobNamespace: ns1Name, PageSize: 100, IncludeTotal: true),
            ct
        );
        var ns2Page = await queries.Definitions.ListAsync(
            new ListDefinitionsQuery(JobNamespace: ns2Name, PageSize: 100, IncludeTotal: true),
            ct
        );

        var ns1Ids = ns1Page.Items.Select(d => d.JobDefinitionId).ToHashSet();
        var ns2Ids = ns2Page.Items.Select(d => d.JobDefinitionId).ToHashSet();

        // ns1 has exactly the 2 we seeded
        Assert.Equal([defA1Id, defB1Id], ns1Ids);
        Assert.Equal(2L, ns1Page.TotalCount);

        // ns2 has exactly the 1 we seeded
        Assert.Equal([defNs2Id], ns2Ids);
        Assert.Equal(1L, ns2Page.TotalCount);

        // Cross-exclusion: neither namespace bleeds into the other
        Assert.Empty(ns1Ids.Intersect(ns2Ids));
    }
}
