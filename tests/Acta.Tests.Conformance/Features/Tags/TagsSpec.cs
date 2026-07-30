using Acta.Modules.Operations.Tags;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Tags;

[ConformanceSpec(
    "tags.exact-target-metadata",
    "Tags read and mutate all first-class targets and filter typed queries",
    Area = "Tags",
    Contract = "ITags distinguishes missing from empty targets, atomically replaces, idempotently upserts/removes, and typed queries require every exact tag filter.",
    Arrange = "A tenant, namespace, definition, job, schedule, worker, alert, and event are created in one isolated namespace.",
    Act = "Every target is read and mutated, then every typed list is filtered by two attached tags.",
    Assert = "Existing targets return ordered sets, missing targets return null/NotFound, mutations converge, and every typed list returns only matches."
)]
[CoversStoreMethod(typeof(ITagStore), nameof(ITagStore.GetAsync))]
[CoversStoreMethod(typeof(ITagStore), nameof(ITagStore.ApplyAsync))]
public abstract class TagsSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "All eight existing targets read empty while missing targets read null and mutate NotFound")]
    public async Task Missing_and_empty_are_distinct_for_every_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var targets = await CreateTargetsAsync(ct);

        foreach (var target in targets.All)
        {
            var tags = await Operations.Tags.GetAsync(target, ct);
            Assert.NotNull(tags);
            Assert.Empty(tags);
        }

        foreach (var target in MissingTargets())
        {
            Assert.Null(await Operations.Tags.GetAsync(target, ct));
            Assert.Equal(TagMutationResult.NotFound, await Operations.Tags.UpsertAsync(target, new TagInput("missing"), ct));
            Assert.Equal(TagMutationResult.NotFound, await Operations.Tags.RemoveAsync(target, "missing", ct));
            Assert.Equal(TagMutationResult.NotFound, await Operations.Tags.ReplaceAsync(target, [], ct));
        }
    }

    [Fact(DisplayName = "The seeded sys namespace supports tag reads and mutations")]
    public async Task Seeded_sys_namespace_is_a_valid_tag_target()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = TagTarget.ForNamespace(IdentifierSyntax.ReservedSystemName);
        var name = TestKey("sys-tag");

        Assert.NotNull(await Operations.Tags.GetAsync(target, ct));
        try
        {
            Assert.Equal(TagMutationResult.Applied, await Operations.Tags.UpsertAsync(target, new TagInput(name, "value"), ct));
            var tags = Assert.IsType<TagSet>(await Operations.Tags.GetAsync(target, ct));
            Assert.Contains(new TagItem(name, "value"), tags.Items);
        }
        finally
        {
            await Operations.Tags.RemoveAsync(target, name, CancellationToken.None);
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
                TagMutationResult.Applied,
                await Operations.Tags.ReplaceAsync(
                    target,
                    [new TagInput("z-last"), new TagInput("a0"), new TagInput("a-a"), new TagInput("a-first", "One")],
                    ct
                )
            );

            var ordered = Assert.IsType<TagSet>(await Operations.Tags.GetAsync(target, ct));
            Assert.Equal([new TagItem("a-a"), new TagItem("a-first", "One"), new TagItem("a0"), new TagItem("z-last")], ordered.Items);

            Assert.Equal(TagMutationResult.Applied, await Operations.Tags.RemoveAsync(target, "a-a", ct));
            Assert.Equal(TagMutationResult.Applied, await Operations.Tags.RemoveAsync(target, "a0", ct));
            Assert.Equal(TagMutationResult.Applied, await Operations.Tags.UpsertAsync(target, new TagInput("a-first", "Two"), ct));
            Assert.Equal(TagMutationResult.Applied, await Operations.Tags.UpsertAsync(target, new TagInput("middle", "Value"), ct));
            Assert.Equal(TagMutationResult.Applied, await Operations.Tags.RemoveAsync(target, "does-not-exist", ct));
            Assert.Equal(TagMutationResult.Applied, await Operations.Tags.RemoveAsync(target, "z-last", ct));

            var converged = Assert.IsType<TagSet>(await Operations.Tags.GetAsync(target, ct));
            Assert.Equal([new TagItem("a-first", "Two"), new TagItem("middle", "Value")], converged.Items);

            Assert.Equal(TagMutationResult.Applied, await Operations.Tags.ReplaceAsync(target, [], ct));
            Assert.Empty(Assert.IsType<TagSet>(await Operations.Tags.GetAsync(target, ct)));
        }
    }

    [Fact(DisplayName = "Tag limits and duplicate normalized names reject before replacing existing state")]
    public async Task Validation_rejects_invalid_replacements_without_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = (await CreateTargetsAsync(ct)).Job;
        await Operations.Tags.ReplaceAsync(target, [new TagInput("kept", "value")], ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Operations.Tags.ReplaceAsync(target, [new TagInput("duplicate"), new TagInput("duplicate", "two")], ct).AsTask()
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Operations
                .Tags.ReplaceAsync(
                    target,
                    Enumerable.Range(0, TagLimits.MaxTagsPerTarget + 1).Select(i => new TagInput($"tag-{i}")).ToArray(),
                    ct
                )
                .AsTask()
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Operations.Tags.ReplaceAsync(target, [new TagInput("name", new string('v', TagLimits.MaxValueLength + 1))], ct).AsTask()
        );

        var unchanged = Assert.IsType<TagSet>(await Operations.Tags.GetAsync(target, ct));
        Assert.Equal([new TagItem("kept", "value")], unchanged.Items);
    }

    [Fact(DisplayName = "Tenant, namespace, definition, job, schedule, worker, alert, and event lists use AND tag semantics")]
    public async Task Typed_lists_filter_all_targets_with_and_semantics()
    {
        var ct = TestContext.Current.CancellationToken;
        var targets = await CreateTargetsAsync(ct);
        foreach (var target in targets.All)
        {
            await Operations.Tags.ReplaceAsync(target, [new TagInput("facet", "Blue"), new TagInput("release", "stable")], ct);
        }

        TagFilter[] filters = [new("facet", "blue"), new("release", "STABLE")];
        TagFilter[] mismatch = [new("facet", "blue"), new("release", "other")];

        var tenants = await Operations.Tenants.ListAsync(new ListTenantsQuery(Tags: filters), ct);
        Assert.Contains(tenants.Items, x => x.TenantKey == targets.TenantKey);
        Assert.DoesNotContain(
            (await Operations.Tenants.ListAsync(new ListTenantsQuery(Tags: mismatch), ct)).Items,
            x => x.TenantKey == targets.TenantKey
        );

        var namespaces = await Operations.Namespaces.ListItemsAsync(new ListNamespacesQuery(Tags: filters), ct);
        Assert.Contains(namespaces.Items, x => x.Name == TestNamespace);
        Assert.DoesNotContain(
            (await Operations.Namespaces.ListItemsAsync(new ListNamespacesQuery(Tags: mismatch), ct)).Items,
            x => x.Name == TestNamespace
        );

        var definitions = await Operations.Definitions.ListAsync(
            new ListJobDefinitionsQuery(JobNamespace: TestNamespace, Tags: filters),
            ct
        );
        Assert.Contains(definitions.Items, x => x.JobDefinitionId == targets.DefinitionId);
        Assert.DoesNotContain(
            (await Operations.Definitions.ListAsync(new ListJobDefinitionsQuery(JobNamespace: TestNamespace, Tags: mismatch), ct)).Items,
            x => x.JobDefinitionId == targets.DefinitionId
        );

        var jobs = await Jobs.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, Tags: filters), ct);
        Assert.Contains(jobs.Items, x => x.JobId == targets.JobId);
        Assert.DoesNotContain(
            (await Jobs.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, Tags: mismatch), ct)).Items,
            x => x.JobId == targets.JobId
        );

        var schedules = await Operations.Schedules.ListAsync(new ListJobSchedulesQuery(JobNamespace: TestNamespace, Tags: filters), ct);
        Assert.Contains(schedules.Items, x => x.JobScheduleId == targets.ScheduleId);
        Assert.DoesNotContain(
            (await Operations.Schedules.ListAsync(new ListJobSchedulesQuery(JobNamespace: TestNamespace, Tags: mismatch), ct)).Items,
            x => x.JobScheduleId == targets.ScheduleId
        );

        var workers = await Operations.Workers.ListAsync(new ListWorkersQuery(JobNamespace: TestNamespace, Tags: filters), ct);
        Assert.Contains(workers.Items, x => x.WorkerId == targets.WorkerId);
        Assert.DoesNotContain(
            (await Operations.Workers.ListAsync(new ListWorkersQuery(JobNamespace: TestNamespace, Tags: mismatch), ct)).Items,
            x => x.WorkerId == targets.WorkerId
        );

        var alerts = await Operations.Alerts.ListAsync(new ListJobAlertsQuery(JobNamespace: TestNamespace, Tags: filters), ct);
        Assert.Contains(alerts.Items, x => x.JobAlertId == targets.AlertId);
        Assert.DoesNotContain(
            (await Operations.Alerts.ListAsync(new ListJobAlertsQuery(JobNamespace: TestNamespace, Tags: mismatch), ct)).Items,
            x => x.JobAlertId == targets.AlertId
        );

        var events = await Jobs.ListJobEventsAsync(new ListJobEventsQuery(JobNamespace: TestNamespace, Tags: filters), ct);
        Assert.Contains(events.Items, x => x.JobEventId == targets.EventId);
        Assert.DoesNotContain(
            (await Jobs.ListJobEventsAsync(new ListJobEventsQuery(JobNamespace: TestNamespace, Tags: mismatch), ct)).Items,
            x => x.JobEventId == targets.EventId
        );
    }

    [Fact(DisplayName = "A deduplicated enqueue preserves the existing job's tags")]
    public async Task Deduplicated_enqueue_does_not_overwrite_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tag-dedup");
        var first = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "add-numbers",
                JobPayload.Json(new AddNumbers(2, 3)),
                DeduplicationKey: key,
                Tags: [new TagInput("version", "first")]
            ),
            ct
        );
        var second = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "add-numbers",
                JobPayload.Json(new AddNumbers(9, 9)),
                DeduplicationKey: key,
                Tags: [new TagInput("version", "second"), new TagInput("extra")]
            ),
            ct
        );

        Assert.Equal(JobEnqueueAction.Inserted, first.Action);
        Assert.Equal(JobEnqueueAction.Deduplicated, second.Action);
        Assert.Equal(first.JobId, second.JobId);
        var tags = Assert.IsType<TagSet>(await Operations.Tags.GetAsync(TagTarget.ForJob(first), ct));
        Assert.Equal([new TagItem("version", "first")], tags.Items);
    }

    [Fact(DisplayName = "Mutating event tags writes no audit event")]
    public async Task Event_tag_mutation_is_an_external_annotation_without_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var targets = await CreateTargetsAsync(ct);
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var before = await Db.From<JobEvent>().Where(x => x.NamespaceId == namespaceId).CountAsync(ct);

        Assert.Equal(
            TagMutationResult.Applied,
            await Operations.Tags.UpsertAsync(TagTarget.ForEvent(targets.EventId), new TagInput("triage", "reviewed"), ct)
        );

        Assert.Equal(before, await Db.From<JobEvent>().Where(x => x.NamespaceId == namespaceId).CountAsync(ct));
    }

    [Fact(DisplayName = "Manual purge removes job, schedule, alert, and event tags before deleting their targets")]
    public async Task Manual_purge_cleans_every_affected_tag_scope()
    {
        var ct = TestContext.Current.CancellationToken;
        var lookup = JobLookup.ByDeduplicationKey(TestNamespace, "recurring-ping");
        var jobId = Assert.IsType<long>(await Jobs.ResolveJobIdAsync(lookup, ct));
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var schedule = Assert.Single(
            await Db.From<JobSchedule>().Where(x => x.JobId == jobId && x.Name == "every-5-minutes").ToListAsync(ct)
        );

        Assert.Equal(JobControlAction.Applied, (await Jobs.CancelAsync(lookup, ct: ct)).Action);
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Info,
            AlertKindCode.Manual,
            "purge tags spec",
            "purge tags spec",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            null,
            ct
        );
        var alert = Assert.Single(await Db.From<JobAlert>().Where(x => x.JobId == jobId).ToListAsync(ct));
        var eventRows = await Db.From<JobEvent>().Where(x => x.JobId == jobId).ToListAsync(ct);
        Assert.NotEmpty(eventRows);
        var eventId = eventRows.Max(x => x.Id);

        TagTarget[] targets =
        [
            TagTarget.ForJob(lookup),
            TagTarget.ForSchedule(new JobScheduleLookup(lookup, schedule.Name)),
            TagTarget.ForAlert(alert.Id),
            TagTarget.ForEvent(eventId),
        ];
        foreach (var target in targets)
        {
            Assert.Equal(TagMutationResult.Applied, await Operations.Tags.UpsertAsync(target, new TagInput("purge-me"), ct));
        }

        Assert.Equal(JobControlAction.Applied, (await Jobs.PurgeAsync(lookup, ct: ct)).Action);

        Assert.Null(await Db.From<Job>().Where(x => x.Id == jobId).SingleOrDefaultAsync(ct));
        Assert.Null(await Db.From<JobSchedule>().Where(x => x.Id == schedule.Id).SingleOrDefaultAsync(ct));
        Assert.Null(await Db.From<JobAlert>().Where(x => x.Id == alert.Id).SingleOrDefaultAsync(ct));
        Assert.Null(await Db.From<JobEvent>().Where(x => x.Id == eventId).SingleOrDefaultAsync(ct));
        Assert.Empty(
            await Db.From<Tag>()
                .Where(x =>
                    (x.ScopeCode == TagScopeCode.Job && x.ScopeId == jobId)
                    || (x.ScopeCode == TagScopeCode.Schedule && x.ScopeId == schedule.Id)
                    || (x.ScopeCode == TagScopeCode.Alert && x.ScopeId == alert.Id)
                    || (x.ScopeCode == TagScopeCode.Event && x.ScopeId == eventId)
                )
                .ToListAsync(ct)
        );

        var purgeAudit = await Db.From<JobEvent>()
            .Where(x => x.NamespaceId == namespaceId && x.EventCode == JobEventCode.JobPurged)
            .ToListAsync(ct);
        Assert.NotEmpty(purgeAudit);
    }

    [Fact(DisplayName = "Concurrent tag mutation and target deletion converge without orphan tags")]
    public async Task Mutation_delete_race_leaves_no_orphan_tag()
    {
        var ct = TestContext.Current.CancellationToken;
        var lookup = JobLookup.ByDeduplicationKey(TestNamespace, "recurring-ping");
        var jobId = Assert.IsType<long>(await Jobs.ResolveJobIdAsync(lookup, ct));
        Assert.Equal(JobControlAction.Applied, (await Jobs.CancelAsync(lookup, ct: ct)).Action);

        var mutationTask = Operations.Tags.UpsertAsync(TagTarget.ForJob(lookup), new TagInput("race", "mutation"), ct).AsTask();
        var purgeTask = Jobs.PurgeAsync(lookup, ct: ct).AsTask();
        await Task.WhenAll(mutationTask, purgeTask);

        Assert.Contains(mutationTask.Result, new[] { TagMutationResult.Applied, TagMutationResult.NotFound });
        Assert.Equal(JobControlAction.Applied, purgeTask.Result.Action);
        Assert.Null(await Db.From<Job>().Where(x => x.Id == jobId).SingleOrDefaultAsync(ct));
        Assert.Empty(await Db.From<Tag>().Where(x => x.ScopeCode == TagScopeCode.Job && x.ScopeId == jobId).ToListAsync(ct));
    }

    private async Task<TestTargets> CreateTargetsAsync(CancellationToken ct)
    {
        var tenantKey = TestKey("tenant");
        await Operations.Tenants.RegisterAsync(tenantKey, ct: ct);

        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var definition = Assert.Single(
            await Db.From<JobDefinition>().Where(x => x.NamespaceId == namespaceId && x.Name == "add-numbers").ToListAsync(ct)
        );
        var worker = Assert.Single(await Db.From<JobWorker>().Where(x => x.NamespaceId == namespaceId).ToListAsync(ct));

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct);

        var schedule = Assert.Single(
            await Db.From<JobSchedule>().Where(x => x.NamespaceId == namespaceId && x.Name == "every-5-minutes").ToListAsync(ct)
        );

        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            job.JobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Info,
            AlertKindCode.Manual,
            "tags spec",
            "tags spec",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            null,
            ct
        );
        var alert = Assert.Single(await Db.From<JobAlert>().Where(x => x.JobId == job.JobId).ToListAsync(ct));
        var events = await Db.From<JobEvent>().Where(x => x.NamespaceId == namespaceId).ToListAsync(ct);
        Assert.NotEmpty(events);
        var eventId = events.Max(x => x.Id);

        return new TestTargets(
            tenantKey,
            definition.Id,
            job.JobId,
            schedule.Id,
            worker.Id,
            alert.Id,
            eventId,
            [
                TagTarget.ForTenant(tenantKey),
                TagTarget.ForNamespace(TestNamespace),
                TagTarget.ForDefinition(definition.Id),
                TagTarget.ForJob(job),
                TagTarget.ForSchedule(new JobScheduleLookup(JobLookup.ById(schedule.JobId), schedule.Name)),
                TagTarget.ForWorker(worker.Id),
                TagTarget.ForAlert(alert.Id),
                TagTarget.ForEvent(eventId),
            ]
        );
    }

    private IReadOnlyList<TagTarget> MissingTargets() =>
        [
            TagTarget.ForTenant(TestKey("missing-tenant")),
            TagTarget.ForNamespace($"missing-{TestId}"),
            TagTarget.ForDefinition(int.MaxValue),
            TagTarget.ForJob(JobLookup.ById(long.MaxValue)),
            TagTarget.ForSchedule(new JobScheduleLookup(JobLookup.ById(long.MaxValue), "missing")),
            TagTarget.ForWorker(int.MaxValue),
            TagTarget.ForAlert(long.MaxValue),
            TagTarget.ForEvent(long.MaxValue),
        ];

    private sealed record TestTargets(
        string TenantKey,
        int DefinitionId,
        long JobId,
        long ScheduleId,
        int WorkerId,
        long AlertId,
        long EventId,
        IReadOnlyList<TagTarget> All
    )
    {
        public TagTarget Job => All[3];
    }
}
