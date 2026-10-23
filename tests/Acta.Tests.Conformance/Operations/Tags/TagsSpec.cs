using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tags;

[ConformanceSpec(
    "tags.exact-target-metadata",
    "Tags read and mutate all first-class targets and filter typed queries",
    Area = "Tags",
    Contract = "ITags distinguishes missing from empty targets, atomically replaces, idempotently upserts/removes, and typed queries require every exact tag filter."
)]
public sealed class TagsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    [Fact(DisplayName = "All four existing targets read empty while missing targets read null and mutate NotFound")]
    public async Task Missing_and_empty_are_distinct_for_every_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var targets = await CreateTargetsAsync(ct);

        foreach (var target in targets.All)
        {
            var tags = await Operations.Tags.GetAsync(target, ct);
            Assert.NotNull(tags);
            Assert.Empty(tags.Items);
        }

        foreach (var target in MissingTargets())
        {
            Assert.Null(await Operations.Tags.GetAsync(target, ct));
            Assert.Equal(ControlAction.NotFound, (await Operations.Tags.UpsertAsync(target, new TagInput("missing"), ct: ct)).Action);
            Assert.Equal(ControlAction.NotFound, (await Operations.Tags.RemoveAsync(target, "missing", ct: ct)).Action);
            Assert.Equal(ControlAction.NotFound, (await Operations.Tags.ReplaceAsync(target, [], ct: ct)).Action);
        }
    }

    [Fact(DisplayName = "The seeded sys namespace supports tag reads and mutations")]
    public async Task Seeded_sys_namespace_is_a_valid_tag_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = TagTarget.ForNamespace(NameSyntax.ReservedSystemName);
        var name = Unique("sys-tag");

        Assert.NotNull(await Operations.Tags.GetAsync(target, ct));
        try
        {
            Assert.Equal(ControlAction.Applied, (await Operations.Tags.UpsertAsync(target, new TagInput(name, "value"), ct: ct)).Action);
            var tags = Assert.IsType<GetTagsResponse>(await Operations.Tags.GetAsync(target, ct));
            Assert.Contains(new GetTagsItem(name, "value"), tags.Items);
        }
        finally
        {
            await Operations.Tags.RemoveAsync(target, name, ct: CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Replace is atomic and clearable; upsert and remove are idempotent and reads are name ordered")]
    public async Task Mutations_apply_to_every_target_with_ordered_reads()
    {
        var ct = TestContext.Current.CancellationToken;
        var targets = await CreateTargetsAsync(ct);

        foreach (var target in targets.All)
        {
            Assert.Equal(
                ControlAction.Applied,
                (
                    await Operations.Tags.ReplaceAsync(
                        target,
                        [new TagInput("z-last"), new TagInput("a0"), new TagInput("a-a"), new TagInput("a-first", "One")],
                        ct: ct
                    )
                ).Action
            );

            var ordered = Assert.IsType<GetTagsResponse>(await Operations.Tags.GetAsync(target, ct));
            Assert.Equal(
                [new GetTagsItem("a-a"), new GetTagsItem("a-first", "One"), new GetTagsItem("a0"), new GetTagsItem("z-last")],
                ordered.Items
            );

            Assert.Equal(ControlAction.Applied, (await Operations.Tags.RemoveAsync(target, "a-a", ct: ct)).Action);
            Assert.Equal(ControlAction.Applied, (await Operations.Tags.RemoveAsync(target, "a0", ct: ct)).Action);
            Assert.Equal(ControlAction.Applied, (await Operations.Tags.UpsertAsync(target, new TagInput("a-first", "Two"), ct: ct)).Action);
            Assert.Equal(
                ControlAction.Applied,
                (await Operations.Tags.UpsertAsync(target, new TagInput("middle", "Value"), ct: ct)).Action
            );
            Assert.Equal(ControlAction.Applied, (await Operations.Tags.RemoveAsync(target, "does-not-exist", ct: ct)).Action);
            Assert.Equal(ControlAction.Applied, (await Operations.Tags.RemoveAsync(target, "z-last", ct: ct)).Action);

            var converged = Assert.IsType<GetTagsResponse>(await Operations.Tags.GetAsync(target, ct));
            Assert.Equal([new GetTagsItem("a-first", "Two"), new GetTagsItem("middle", "Value")], converged.Items);

            Assert.Equal(ControlAction.Applied, (await Operations.Tags.ReplaceAsync(target, [], ct: ct)).Action);
            Assert.Empty(Assert.IsType<GetTagsResponse>(await Operations.Tags.GetAsync(target, ct)).Items);
        }
    }

    [Fact(DisplayName = "Tenant, namespace, definition, and job lists use AND tag semantics")]
    public async Task Typed_lists_filter_all_targets_with_and_semantics()
    {
        var ct = TestContext.Current.CancellationToken;
        var targets = await CreateTargetsAsync(ct);
        // The facet value is this run's own, so the tenants and namespaces earlier runs left in a long-lived
        // database cannot push this run's off the first page; it is stored upper-case and filtered lower-case.
        var facet = Unique("blue");
        foreach (var target in targets.All)
        {
            await Operations.Tags.ReplaceAsync(
                target,
                [new TagInput("facet", facet.ToUpperInvariant()), new TagInput("release", "stable")],
                ct: ct
            );
        }

        TagFilter[] filters = [new("facet", facet), new("release", "STABLE")];
        TagFilter[] mismatch = [new("facet", facet), new("release", "other")];

        var tenants = await Operations.Tenants.ListAsync(new ListTenantsQuery(Tags: filters), ct);
        Assert.Contains(tenants.Items, x => x.TenantName == targets.TenantName);
        Assert.DoesNotContain(
            (await Operations.Tenants.ListAsync(new ListTenantsQuery(Tags: mismatch), ct)).Items,
            x => x.TenantName == targets.TenantName
        );

        var namespaces = await Operations.Namespaces.ListAsync(new ListNamespacesQuery(Tags: filters), ct);
        Assert.Contains(namespaces.Items, x => x.JobNamespace == TestNamespace);
        Assert.DoesNotContain(
            (await Operations.Namespaces.ListAsync(new ListNamespacesQuery(Tags: mismatch), ct)).Items,
            x => x.JobNamespace == TestNamespace
        );

        var definitions = await Operations.Definitions.ListAsync(new ListDefinitionsQuery(JobNamespace: TestNamespace, Tags: filters), ct);
        Assert.Contains(definitions.Items, x => x.DefinitionId == targets.DefinitionId);
        Assert.DoesNotContain(
            (await Operations.Definitions.ListAsync(new ListDefinitionsQuery(JobNamespace: TestNamespace, Tags: mismatch), ct)).Items,
            x => x.DefinitionId == targets.DefinitionId
        );

        var jobs = await Operations.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, Tags: filters), ct);
        Assert.Contains(jobs.Items, x => x.JobId == targets.JobId);
        Assert.DoesNotContain(
            (await Operations.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, Tags: mismatch), ct)).Items,
            x => x.JobId == targets.JobId
        );
    }

    [Fact(DisplayName = "A tag value filter matches the value trimmed and upper-cased, and otherwise exactly")]
    public async Task Tag_value_filters_compare_exactly()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Unique("v");
        // Full-width letters and two different emoji compare equal under SQL Server's default collation, not bytewise.
        string[] values =
        [
            $"prod{suffix}",
            $" prod{suffix} ",
            $"\uFF50\uFF52\uFF4F\uFF44{suffix}",
            $"\U0001F600{suffix}",
            $"\U0001F601{suffix}",
        ];
        var jobs = new long[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var job = await Jobs.EnqueueAsync(
                new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(i, i))),
                ct
            );
            await Operations.Tags.ReplaceAsync(TagTarget.ForJob(job), [new TagInput("variant", values[i])], ct: ct);
            jobs[i] = job.JobId;
        }

        Assert.Equal([jobs[0], jobs[1]], await Tagged(values[0]));
        Assert.Equal([jobs[2]], await Tagged(values[2]));
        Assert.Equal([jobs[3]], await Tagged(values[3]));

        async Task<long[]> Tagged(string value) =>
            [
                .. (await Operations.Ledger.ListJobsAsync(new ListJobsQuery(TestNamespace, Tags: [new("variant", value)]), ct))
                    .Items.Select(x => x.JobId)
                    .Order(),
            ];
    }

    [Fact(DisplayName = "A deduplicated enqueue preserves the existing job's tags")]
    public async Task Deduplicated_enqueue_does_not_overwrite_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("tag-dedup");
        var first = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(
                TestNamespace,
                "add-numbers",
                JobPayload.Json(new AddNumbers(2, 3)),
                DeduplicationKey: key,
                Tags: [new TagInput("version", "first")]
            ),
            ct
        );
        var second = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(
                TestNamespace,
                "add-numbers",
                JobPayload.Json(new AddNumbers(9, 9)),
                DeduplicationKey: key,
                Tags: [new TagInput("version", "second"), new TagInput("extra")]
            ),
            ct
        );

        Assert.Equal(EnqueueJobAction.Inserted, first.Action);
        Assert.Equal(EnqueueJobAction.Deduplicated, second.Action);
        Assert.Equal(first.JobId, second.JobId);
        var tags = Assert.IsType<GetTagsResponse>(await Operations.Tags.GetAsync(TagTarget.ForJob(first), ct));
        Assert.Equal([new GetTagsItem("version", "first")], tags.Items);
    }

    [Fact(DisplayName = "Manual purge removes the job's tags before deleting the job")]
    public async Task Manual_purge_cleans_the_job_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var lookup = JobLookup.ByDeduplicationKey(TestNamespace, "recurring-ping");
        var jobId = Assert.IsType<long>((await Jobs.GetJobIdAsync(lookup, ct))?.JobId);
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(lookup, ct: ct)).Action);
        Assert.Equal(
            ControlAction.Applied,
            (await Operations.Tags.UpsertAsync(TagTarget.ForJob(lookup), new TagInput("purge-me"), ct: ct)).Action
        );

        Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(lookup, ct: ct)).Action);

        Assert.Null(await Db.From<JobEntity>().Where(x => x.Id == jobId).SingleOrDefaultAsync(ct));
        Assert.Empty(await Db.From<TagEntity>().Where(x => x.EntityCode == EntityCode.Job && x.EntityId == jobId).ToListAsync(ct));

        var purgeAudit = await Db.From<EventEntity>()
            .Where(x => x.NamespaceId == namespaceId && x.EventCode == EventCode.JobPurged)
            .ToListAsync(ct);
        Assert.NotEmpty(purgeAudit);
    }

    [Fact(DisplayName = "Concurrent tag mutation and target deletion converge without orphan tags")]
    public async Task Mutation_delete_race_leaves_no_orphan_tag()
    {
        var ct = TestContext.Current.CancellationToken;
        var lookup = JobLookup.ByDeduplicationKey(TestNamespace, "recurring-ping");
        var jobId = Assert.IsType<long>((await Jobs.GetJobIdAsync(lookup, ct))?.JobId);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(lookup, ct: ct)).Action);

        var mutationTask = Operations.Tags.UpsertAsync(TagTarget.ForJob(lookup), new TagInput("race", "mutation"), ct: ct).AsTask();
        var purgeTask = Jobs.PurgeAsync(lookup, ct: ct).AsTask();
        await Task.WhenAll(mutationTask, purgeTask);

        Assert.Contains((await mutationTask).Action, new[] { ControlAction.Applied, ControlAction.NotFound });
        Assert.Equal(ControlAction.Applied, (await purgeTask).Action);
        Assert.Null(await Db.From<JobEntity>().Where(x => x.Id == jobId).SingleOrDefaultAsync(ct));
        Assert.Empty(await Db.From<TagEntity>().Where(x => x.EntityCode == EntityCode.Job && x.EntityId == jobId).ToListAsync(ct));
    }

    private async Task<TestTargets> CreateTargetsAsync(CancellationToken ct)
    {
        var tenantName = Unique("tenant");
        await Operations.Tenants.RegisterAsync(tenantName, ct: ct);

        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var definition = Assert.Single(
            await Db.From<DefinitionEntity>().Where(x => x.NamespaceId == namespaceId && x.DefinitionName == "add-numbers").ToListAsync(ct)
        );

        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct);

        return new TestTargets(
            tenantName,
            definition.Id,
            job.JobId,
            [
                TagTarget.ForTenant(tenantName),
                TagTarget.ForNamespace(TestNamespace),
                TagTarget.ForDefinition(TestNamespace, definition.DefinitionName),
                TagTarget.ForJob(job),
            ]
        );
    }

    private IReadOnlyList<TagTarget> MissingTargets() =>
        [
            TagTarget.ForTenant(Unique("missing-tenant")),
            TagTarget.ForNamespace($"missing-{TestId}"),
            TagTarget.ForDefinition(TestNamespace, "missing-definition"),
            TagTarget.ForJob(JobLookup.ById(long.MaxValue)),
        ];

    private sealed record TestTargets(string TenantName, int DefinitionId, long JobId, IReadOnlyList<TagTarget> All);
}
