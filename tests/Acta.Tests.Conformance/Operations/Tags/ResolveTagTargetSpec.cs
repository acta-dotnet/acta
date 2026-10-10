using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tags;

/// <summary>
/// Conformance for <c>ResolveTagTarget</c>, the lookup every tag operation starts from: a target named by a ref or a
/// natural key resolves to the row id the tags table joins on, a tenant, namespace, or event target passes through,
/// and a target whose entity does not exist resolves to <c>null</c>.
/// </summary>
[ConformanceSpec(
    "resolve-tag-target.lookup",
    "ResolveTagTarget finds the row a tag target names, null for a missing entity",
    Area = "Tags",
    Contract = "ResolveTagTarget resolves a ref or natural-key target to the row the tags table joins on, passes the rest through, and returns null when the entity is missing."
)]
public sealed class ResolveTagTargetSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    [Fact(DisplayName = "Each existing target resolves to its scope and the id or name the tags table joins on")]
    public async Task Existing_targets_resolve_to_their_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var definition = Assert.Single(
            await Db.From<DefinitionEntity>().Where(x => x.NamespaceId == namespaceId && x.Name == "add-numbers").ToListAsync(ct)
        );
        var worker = Assert.Single(await Db.From<WorkerEntity>().Where(x => x.NamespaceId == namespaceId).ToListAsync(ct));
        var job = await Jobs.EnqueueAsync(new AddNumbers(2, 3), ct: ct);
        var alert = await RaiseAlertAsync(job.JobId, ct);
        var slot = JobLookup.ByDeduplicationKey(TestNamespace, "recurring-ping");
        var slotId = (await Jobs.GetJobIdAsync(slot, ct))?.JobId;

        Assert.Equal(
            new ResolveTagTargetResponse(TagScopeCode.Definition, definition.Id, null),
            await ResolveAsync(TagTarget.ForDefinition(TestNamespace, "add-numbers"), ct)
        );
        Assert.Equal(new ResolveTagTargetResponse(TagScopeCode.Job, job.JobId, null), await ResolveAsync(TagTarget.ForJob(job.JobRef), ct));
        Assert.Equal(
            new ResolveTagTargetResponse(TagScopeCode.Schedule, slotId, "every-5-minutes"),
            await ResolveAsync(TagTarget.ForSchedule(new ScheduleLookup(slot, "every-5-minutes")), ct)
        );
        Assert.Equal(
            new ResolveTagTargetResponse(TagScopeCode.Worker, worker.Id, null),
            await ResolveAsync(TagTarget.ForWorker(new WorkerRef(worker.WorkerRef)), ct)
        );
        Assert.Equal(
            new ResolveTagTargetResponse(TagScopeCode.Alert, alert.Id, null),
            await ResolveAsync(TagTarget.ForAlert(new AlertRef(alert.AlertRef)), ct)
        );
        Assert.Equal(
            new ResolveTagTargetResponse(TagScopeCode.Namespace, null, TestNamespace),
            await ResolveAsync(TagTarget.ForNamespace(TestNamespace), ct)
        );
        Assert.Equal(new ResolveTagTargetResponse(TagScopeCode.Event, 42, null), await ResolveAsync(TagTarget.ForEvent(42), ct));
    }

    [Fact(DisplayName = "A target whose entity does not exist resolves to null")]
    public async Task Missing_entities_resolve_to_null()
    {
        var ct = TestContext.Current.CancellationToken;
        TagTarget[] missing =
        [
            TagTarget.ForDefinition(TestNamespace, "missing-definition"),
            TagTarget.ForJob(JobRef.New()),
            TagTarget.ForSchedule(new ScheduleLookup(JobLookup.ByRef(JobRef.New()), "missing")),
            TagTarget.ForWorker(WorkerRef.New()),
            TagTarget.ForAlert(AlertRef.New()),
        ];

        foreach (var target in missing)
        {
            Assert.Null(await ResolveAsync(target, ct));
        }
    }

    private ValueTask<ResolveTagTargetResponse?> ResolveAsync(TagTarget target, CancellationToken ct) =>
        Services.GetRequiredService<ResolveTagTargetHandler>().HandleAsync(new ResolveTagTargetQuery(target), ct);

    private async Task<AlertEntity> RaiseAlertAsync(long jobId, CancellationToken ct)
    {
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Info,
            AlertKindCode.Manual,
            "resolve tag target spec",
            "resolve tag target spec",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            ct
        );
        return Assert.Single(await Db.From<AlertEntity>().Where(x => x.JobId == jobId).ToListAsync(ct));
    }
}
