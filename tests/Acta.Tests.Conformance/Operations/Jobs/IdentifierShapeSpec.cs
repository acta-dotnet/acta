using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance: the two identifier shapes. A name is trimmed and lowercased on input, so any case addresses one
/// stored name; a key keeps its case and is compared exactly. Only the deduplication key reserves <c>sys.</c>,
/// and a lookup still resolves the seeded <c>sys</c> namespace.
/// </summary>
[ConformanceSpec(
    "identifier.shape",
    "Names fold to lowercase while keys keep their case",
    Area = "Enqueue",
    Contract = "A name is folded to lowercase on input, so any case addresses one stored name, while a key keeps its case and compares exactly."
)]
public sealed class IdentifierShapeSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // InitializeAsync registers the namespace and the test job definitions; each test drives its own jobs.
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "A namespace and job name in any case route to the registered definition")]
    public async Task Names_in_any_case_route_to_the_registered_definition()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace.ToUpperInvariant(), "Add-Numbers", Payload()), ct);

        var job = Assert.IsType<GetJobResponse>(await Jobs.GetAsync(JobLookup.ById(enqueued.JobId), ct));
        Assert.Equal(TestNamespace, job.JobNamespace);
        Assert.Equal("add-numbers", job.JobName);
        Assert.NotNull(await Operations.Definitions.GetAsync(TestNamespace.ToUpperInvariant(), "Add-Numbers", ct));
    }

    [Fact(DisplayName = "A tag name Billing.Send registers as billing.send and resolves in any case")]
    public async Task A_dotted_name_folds_at_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = TagTarget.ForNamespace(TestNamespace);

        await Operations.Tags.UpsertAsync(target, new TagInput("Billing.Send", "on"), ct: ct);

        var tags = Assert.IsType<GetTagsResponse>(
            await Operations.Tags.GetAsync(TagTarget.ForNamespace(TestNamespace.ToUpperInvariant()), ct)
        );
        Assert.Equal([new GetTagsItem("billing.send", "on")], tags.Items);

        await Operations.Tags.RemoveAsync(target, "BILLING.SEND", ct: ct);
        Assert.Empty(Assert.IsType<GetTagsResponse>(await Operations.Tags.GetAsync(target, ct)).Items);
    }

    [Fact(DisplayName = "A lane ORDER-1 is the lane order-1")]
    public async Task A_lane_folds()
    {
        var ct = TestContext.Current.CancellationToken;
        var lane = TestKey("ORDER");

        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", Payload()) { Lane = lane }, ct);

        var job = Assert.IsType<GetJobResponse>(await Jobs.GetAsync(JobLookup.ById(enqueued.JobId), ct));
        Assert.Equal(lane.ToLowerInvariant(), job.Lane);
    }

    [Fact(DisplayName = "A tenant ACME registers as acme and resolves in any case")]
    public async Task A_tenant_folds()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = TestKey("ACME");

        var registered = await Operations.Tenants.RegisterAsync(tenant, null, null, ct);

        Assert.Equal(tenant.ToLowerInvariant(), registered.TenantName);
        Assert.Equal(registered.TenantId, (await Operations.Tenants.RegisterAsync(tenant.ToLowerInvariant(), null, null, ct)).TenantId);
    }

    [Fact(DisplayName = "A signal raised as GO releases the wait on go")]
    public async Task A_signal_name_folds()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "GO", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
    }

    [Fact(DisplayName = "Deduplication keys that differ only by case are two jobs, and a lookup by key is exact")]
    public async Task Deduplication_keys_keep_their_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("Order");

        var first = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", Payload(), DeduplicationKey: key), ct);
        var second = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", Payload(), DeduplicationKey: key.ToLowerInvariant()),
            ct
        );

        Assert.Equal(EnqueueJobAction.Inserted, second.Action);
        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(first.JobId, (await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, key), ct))?.JobId);
        Assert.Null(await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, key.ToUpperInvariant()), ct));
    }

    [Fact(DisplayName = "Concurrency keys that differ only by case do not share a lock")]
    public async Task Concurrency_keys_keep_their_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("mutex");
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // A foreign owner holds the only slot of the uppercase key, composed as the runner does ({ns_id}.sem.{key}).
        var locks = Services.GetRequiredService<LocksOperations>();
        var held = (
            await locks.AcquireSlotAsync(
                $"{ns}.sem.{key.ToUpperInvariant()}",
                limit: 1,
                TimeSpan.FromMinutes(5),
                ownerJobId: long.MaxValue,
                ct
            )
        ).Token;
        Assert.NotNull(held);

        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", Payload()) { ConcurrencyKey = key }, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        await locks.ReleaseLockAsync(held.Value, ct);
    }

    [Fact(DisplayName = "A batch keeps deduplication keys that differ only by case apart, from each other and from a stored key")]
    public async Task A_batch_keeps_key_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("Order");
        var stored = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", Payload(), DeduplicationKey: key), ct);

        var batch = await Jobs.EnqueueBatchAsync(
            [
                new EnqueueJobRequest(TestNamespace, "add-numbers", Payload(), DeduplicationKey: key.ToLowerInvariant()),
                new EnqueueJobRequest(TestNamespace, "add-numbers", Payload(), DeduplicationKey: key.ToUpperInvariant()),
                new EnqueueJobRequest(TestNamespace, "add-numbers", Payload(), DeduplicationKey: key),
            ],
            ct
        );

        Assert.Equal(
            [EnqueueJobAction.Inserted, EnqueueJobAction.Inserted, EnqueueJobAction.Deduplicated],
            batch.Jobs.Select(j => j.Action)
        );
        Assert.Equal(3, batch.Jobs.Select(j => j.JobId).Append(stored.JobId).Distinct().Count());
        Assert.Equal(stored.JobId, batch.Jobs[2].JobId);
    }

    [Fact(DisplayName = "Lock keys that differ only by case are two locks, and one heartbeat renews both")]
    public async Task Lock_keys_keep_their_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var key = $"{ns}.lock.{TestKey("Ledger")}";
        var locks = Services.GetRequiredService<LocksOperations>();

        var exact = (await locks.AcquireLockAsync(key, TimeSpan.FromMinutes(5), ownerJobId: long.MaxValue, ct)).Token;
        var folded = (await locks.AcquireLockAsync(key.ToLowerInvariant(), TimeSpan.FromMinutes(5), ownerJobId: long.MaxValue, ct)).Token;
        Assert.NotNull(exact);
        Assert.NotNull(folded);

        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var answer = await Services
            .GetRequiredService<WorkersOperations>()
            .ExtendWorkerLeasesAsync(worker!.Id, leaseTtl, false, [], [exact.Value, folded.Value], ct);

        Assert.Equal([key, key.ToLowerInvariant()], answer.RenewedLockKeys.Order(StringComparer.Ordinal));
        Assert.True((await locks.ReleaseLockAsync(exact.Value, ct)).Released);
        Assert.True((await locks.ReleaseLockAsync(folded.Value, ct)).Released);
    }

    [Fact(DisplayName = "SYS.x is rejected as a deduplication key at enqueue and as a name at registration")]
    public async Task The_sys_reservation_holds_in_any_case()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", Payload(), DeduplicationKey: "SYS.x"), ct)
        );
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Operations.Tenants.RegisterAsync("SYS.x", null, null, ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await Operations.Tags.UpsertAsync(TagTarget.ForNamespace(TestNamespace), new TagInput("SYS.x"), ct: ct)
        );
    }

    [Fact(DisplayName = "A lookup names the seeded sys namespace in any case")]
    public async Task A_lookup_resolves_the_sys_namespace()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.NotNull(await Operations.Tags.GetAsync(TagTarget.ForNamespace("SYS"), ct));
        await Operations.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: "Sys", JobName: "Sys.Recovery"), ct);
    }

    private static JobPayload Payload() => JobPayload.Json(new AddNumbers(1, 2));
}
