using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Outbox;

/// <summary>
/// A staged record's ref is its job's ref from the moment it is staged. The relay enqueues the job under that ref, so a
/// record the relay enqueued and then stopped before removing is recognised by its held ref on the next pass, with or
/// without a deduplication key.
/// </summary>
[ConformanceSpec(
    "outbox.relay-job-ref",
    "A staged record's ref is its job's ref, and a redelivery settles onto that job",
    Area = "Outbox",
    Contract = "The relay enqueues a staged record under its ref, and a record whose ref a job holds is removed as delivered or, for another job, quarantined."
)]
public sealed class OutboxRelayJobRefSpec : OutboxRelayIntegrationBase
{
    private const string RecordTags = """{"tags":[{"name":"color","value":"red"},{"name":"size","value":"large"}]}""";

    [Fact(DisplayName = "The staging call returns the ref before the commit, and the relay enqueues the job under it")]
    public async Task Staging_returns_the_ref_the_job_takes()
    {
        var ct = TestContext.Current.CancellationToken;
        JobRef staged;
        await using (var producer = await Fixture.BeginOutboxProducerAsync(SourceTable))
        {
            staged = await producer.StageAsync(EchoRequest(deduplicationKey: null));
            await producer.CommitAsync();
        }

        Assert.Equal(7, staged.Value.Version);
        Assert.Equal(
            new OutboxTickSummary(1, 1, 0, 0, 0),
            (await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct)).Summary
        );
        var job = await Jobs.GetAsync(staged, ct);
        Assert.NotNull(job);
        Assert.Equal((TestNamespace, "echo"), (job.JobNamespace, job.JobName));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Theory(DisplayName = "A record delivered before the relay stopped short of removing it is delivered again as the same job")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_redelivered_record_settles_onto_its_job(bool keyed)
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = EchoRow(keyed ? Unique("redeliver") : null);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);
        var before = await CountNamespaceJobsAsync(ct);

        var first = await DeliverAndStopAsync(ct);
        Assert.Equal(1, first);

        var (summary, quarantined) = await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        // A keyless record settles through the relay's read of its held ref, a keyed one at the enqueue by its key.
        Assert.Equal(new OutboxTickSummary(1, 0, 1, 0, 0), summary);
        Assert.Empty(quarantined);
        Assert.Equal(before + 1, await CountNamespaceJobsAsync(ct));
        Assert.NotNull(await Jobs.GetAsync(new JobRef(seed.JobRef), ct));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(DisplayName = "A batch that mixes a delivered record with new ones settles the first and delivers the rest")]
    public async Task A_mixed_batch_delivers_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var delivered = EchoRow(null);
        await Fixture.SeedOutboxRowAsync(SourceTable, delivered);
        await DeliverAndStopAsync(ct);
        var fresh = EchoRow(null);
        var keyed = EchoRow(Unique("mixed"));
        await Fixture.SeedOutboxRowAsync(SourceTable, fresh);
        await Fixture.SeedOutboxRowAsync(SourceTable, keyed);

        var (summary, _) = await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.Equal(new OutboxTickSummary(3, 2, 1, 0, 0), summary);
        Assert.NotNull(await Jobs.GetAsync(new JobRef(fresh.JobRef), ct));
        Assert.NotNull(await Jobs.GetAsync(new JobRef(keyed.JobRef), ct));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(DisplayName = "A record whose ref another job holds is quarantined with the reason, and the others are delivered")]
    public async Task A_ref_another_job_holds_quarantines_its_record()
    {
        var ct = TestContext.Current.CancellationToken;
        var foreign = EchoRow(null, jobName: "lane-step");
        var holder = (await OwnedSubmission.EnqueueJobsAsync([EchoRequest(deduplicationKey: null)], [foreign.JobRef], ct)).Jobs[0];
        var good = EchoRow(null);
        await Fixture.SeedOutboxRowAsync(SourceTable, foreign);
        await Fixture.SeedOutboxRowAsync(SourceTable, good);

        var (summary, quarantined) = await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.Equal([foreign.JobRef], quarantined);
        Assert.Equal(1, summary.Relayed);
        var row = await Fixture.ReadOutboxRowAsync(SourceTable, foreign.JobRef);
        Assert.Equal((byte)OutboxStatusCode.Quarantined, row.StatusCode);
        Assert.Equal(0, row.FailureCount);
        Assert.Contains($"already names a job of {TestNamespace}/echo", row.LastError, StringComparison.Ordinal);
        Assert.Equal(holder.JobId, (await Jobs.GetAsync(holder.JobRef, ct))!.JobId);
        Assert.NotNull(await Jobs.GetAsync(new JobRef(good.JobRef), ct));
        Assert.False((await Fixture.ReadOutboxRowAsync(SourceTable, good.JobRef)).Exists);
    }

    [Fact(DisplayName = "A quarantined record keeps its ref through a requeue and is delivered under it")]
    public async Task A_requeued_record_keeps_its_ref()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = EchoRow(null);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);
        var target = new HookedJobSubmission(Services) { FailInstead = () => new ArgumentException("rejected for now") };

        var (_, quarantined) = await Relay(SourceStore, target.Jobs).RunTickAsync(TickOptions(quarantineThreshold: 1), ct);
        Assert.Equal([seed.JobRef], quarantined);
        Assert.Equal(
            [seed.JobRef],
            (await SourceStore.RequeueQuarantinedAsync(new RequeueQuarantinedOutboxCommand([seed.JobRef]), ct)).JobRefs
        );

        await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.NotNull(await Jobs.GetAsync(new JobRef(seed.JobRef), ct));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(DisplayName = "A producer's own uuid of any version is accepted as the job's ref")]
    public async Task A_foreign_uuid_becomes_the_jobs_ref()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = EchoRow(null) with { JobRef = Guid.NewGuid() };
        Assert.Equal(4, seed.JobRef.Version);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);

        await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.NotNull(await Jobs.GetAsync(new JobRef(seed.JobRef), ct));
    }

    [Fact(DisplayName = "Records sharing a deduplication key become one job, and a later record's ref names no job")]
    public async Task Records_sharing_a_key_become_one_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("shared");
        var first = EchoRow(key);
        var second = EchoRow(key);
        await Fixture.SeedOutboxRowAsync(SourceTable, first);
        await Fixture.SeedOutboxRowAsync(SourceTable, second);
        var direct = await Jobs.EnqueueAsync(EchoRequest(Unique("direct")), ct);
        var onto = EchoRow(Unique("direct"));
        await Fixture.SeedOutboxRowAsync(SourceTable, onto);

        var (summary, _) = await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.Equal(new OutboxTickSummary(3, 1, 2, 0, 0), summary);
        Assert.NotNull(await Jobs.GetAsync(new JobRef(first.JobRef), ct));
        Assert.Null(await Jobs.GetAsync(new JobRef(second.JobRef), ct));
        Assert.Null(await Jobs.GetAsync(new JobRef(onto.JobRef), ct));
        Assert.Equal(1, await CountLedgerJobsAsync(Unique("direct"), ct));
        Assert.Equal(direct.JobRef, (await Jobs.GetAsync(direct.JobRef, ct))!.JobRef);
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(DisplayName = "A record whose first job was purged is enqueued again as a new job under the same ref")]
    public async Task A_record_whose_job_was_purged_is_enqueued_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = EchoRow(null);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);
        await DeliverAndStopAsync(ct);
        var jobRef = new JobRef(seed.JobRef);
        var purgedId = (await Jobs.GetAsync(jobRef, ct))!.JobId;
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(jobRef, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(jobRef, ct: ct)).Action);

        var (summary, _) = await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.Equal(new OutboxTickSummary(1, 1, 0, 0, 0), summary);
        Assert.NotEqual(purgedId, (await Jobs.GetAsync(jobRef, ct))!.JobId);
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Theory(DisplayName = "A keyed record with tags delivered before a stop is answered deduplicated by its key, alone or in a batch")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_keyed_tagged_redelivery_settles_by_its_key(bool inBatch)
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = EchoRow(Unique("tagged"), meta: RecordTags);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);
        Assert.Equal(1, await DeliverAndStopAsync(ct));
        var jobId = (await Jobs.GetAsync(new JobRef(seed.JobRef), ct))!.JobId;
        var tags = await TagsAsync(jobId, ct);
        var companions = await SeedCompanionsAsync(inBatch);
        var before = await CountNamespaceJobsAsync(ct);
        var target = new HookedJobSubmission(Services);

        var (summary, quarantined) = await Relay(SourceStore, target.Jobs).RunTickAsync(TickOptions(), ct);

        Assert.Equal(new OutboxTickSummary(1 + companions, companions, 1, 0, 0), summary);
        Assert.Equal(0, target.HeldRefReads);
        Assert.Empty(quarantined);
        Assert.Equal(before + companions, await CountNamespaceJobsAsync(ct));
        Assert.Equal(2, tags.Count);
        Assert.Equal(tags, await TagsAsync(jobId, ct));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Theory(
        DisplayName = "A record carrying one job's key and another job's ref is deduplicated onto the first and leaves the second as it was"
    )]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_record_with_one_jobs_key_and_anothers_ref_settles_on_the_key(bool inBatch, bool otherNamespace)
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("x-key");
        var x = await Jobs.EnqueueAsync(EchoRequest(key) with { Tags = [new TagInput("owner", "x")] }, ct);
        var yRef = JobRef.New();
        await using var other = otherNamespace ? BuildRuntimeFor(Unique("other")) : null;
        if (other is not null)
        {
            await CatalogTestOps.DeployAsync(other, ct);
        }
        var yRequest = other is null
            ? EchoRequest(null) with
            {
                JobName = "lane-step",
            }
            : EchoRequest(null) with
            {
                JobNamespace = Unique("other"),
            };
        await (other?.GetRequiredService<JobsOperations>() ?? OwnedSubmission).EnqueueJobsAsync(
            [yRequest with { Tags = [new TagInput("owner", "y")] }],
            [yRef.Value],
            ct
        );
        var yId = (await Jobs.GetAsync(yRef, ct))!.JobId;
        var (xTags, yTags, yState) = (await TagsAsync(x.JobId, ct), await TagsAsync(yId, ct), await StateAsync(yId, ct));
        var seed = EchoRow(key, meta: RecordTags) with { JobRef = yRef.Value };
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);
        var companions = await SeedCompanionsAsync(inBatch);
        var namespaceIds = other is null
            ? [NamespaceId]
            : new[] { NamespaceId, await ChaosSpecHelpers.NamespaceIdAsync(Db, Unique("other"), ct) };
        var before = await CountJobsAsync(namespaceIds, ct);
        var target = new HookedJobSubmission(Services);

        var (summary, quarantined) = await Relay(SourceStore, target.Jobs).RunTickAsync(TickOptions(), ct);

        Assert.Equal(yTags, await TagsAsync(yId, ct));
        Assert.Equal(yState, await StateAsync(yId, ct));
        Assert.Equal(xTags, await TagsAsync(x.JobId, ct));
        Assert.Equal(new OutboxTickSummary(1 + companions, companions, 1, 0, 0), summary);
        Assert.Equal(0, target.HeldRefReads);
        Assert.Empty(quarantined);
        Assert.Equal(before + companions, await CountJobsAsync(namespaceIds, ct));
        Assert.False((await Fixture.ReadOutboxRowAsync(SourceTable, seed.JobRef)).Exists);
    }

    [Fact(DisplayName = "Three keyed records delivered before a stop are answered deduplicated by their keys in one batch")]
    public async Task Keyed_records_redelivered_in_a_batch_settle_by_their_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        OutboxSeed[] seeds = [EchoRow(Unique("batch-a"), meta: RecordTags), EchoRow(Unique("batch-b")), EchoRow(Unique("batch-c"))];
        foreach (var seed in seeds)
        {
            await Fixture.SeedOutboxRowAsync(SourceTable, seed);
        }
        Assert.Equal(3, await DeliverAndStopAsync(ct));
        var tagged = (await Jobs.GetAsync(new JobRef(seeds[0].JobRef), ct))!.JobId;
        var tags = await TagsAsync(tagged, ct);
        var before = await CountNamespaceJobsAsync(ct);
        var target = new HookedJobSubmission(Services);

        var (summary, quarantined) = await Relay(SourceStore, target.Jobs).RunTickAsync(TickOptions(), ct);

        Assert.Equal(new OutboxTickSummary(3, 0, 3, 0, 0), summary);
        Assert.Equal(0, target.HeldRefReads);
        Assert.Empty(quarantined);
        Assert.Equal(before, await CountNamespaceJobsAsync(ct));
        Assert.Equal(2, tags.Count);
        Assert.Equal(tags, await TagsAsync(tagged, ct));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(
        DisplayName = "A batch of delivered and new records, keyed and keyless, settles the delivered and enqueues the new under their refs"
    )]
    public async Task A_mixed_redelivery_settles_two_and_enqueues_two()
    {
        var ct = TestContext.Current.CancellationToken;
        OutboxSeed[] delivered = [EchoRow(Unique("mixed-old")), EchoRow(null)];
        foreach (var seed in delivered)
        {
            await Fixture.SeedOutboxRowAsync(SourceTable, seed);
        }
        Assert.Equal(2, await DeliverAndStopAsync(ct));
        OutboxSeed[] fresh = [EchoRow(Unique("mixed-new")), EchoRow(null)];
        foreach (var seed in fresh)
        {
            await Fixture.SeedOutboxRowAsync(SourceTable, seed);
        }
        var deliveredIds = await JobIdsAsync(delivered, ct);
        var before = await CountNamespaceJobsAsync(ct);

        var (summary, quarantined) = await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.Equal(new OutboxTickSummary(4, 2, 2, 0, 0), summary);
        Assert.Empty(quarantined);
        Assert.Equal(before + 2, await CountNamespaceJobsAsync(ct));
        Assert.Equal(deliveredIds, await JobIdsAsync(delivered, ct));
        Assert.Equal(2, (await JobIdsAsync(fresh, ct)).Count);
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    // One tick enqueues the claimed rows and then stops before it removes them, under a lease that has already run out,
    // so the next tick claims the same rows at once. Answers how many jobs the tick inserted.
    private async Task<int> DeliverAndStopAsync(CancellationToken ct)
    {
        var before = await CountNamespaceJobsAsync(ct);
        var store = new HookedOutboxStore(SourceConnection) { BeforeRemove = () => throw new InvalidOperationException("stopped") };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Relay(store.Source, OwnedSubmission).RunTickAsync(TickOptions(leaseTtlSeconds: 0), ct)
        );
        return await CountNamespaceJobsAsync(ct) - before;
    }

    private EnqueueJobRequest EchoRequest(string? deduplicationKey) =>
        new(TestNamespace, "echo", JobPayload.FromBytes(JobPayloadFormat.Json, EchoJson()), DeduplicationKey: deduplicationKey);

    private Task<int> CountNamespaceJobsAsync(CancellationToken ct) =>
        Db.From<JobEntity>().Where(j => j.NamespaceId == NamespaceId).CountAsync(ct);

    private async Task<int> CountJobsAsync(IEnumerable<int> namespaceIds, CancellationToken ct)
    {
        var count = 0;
        foreach (var namespaceId in namespaceIds)
        {
            count += await Db.From<JobEntity>().Where(j => j.NamespaceId == namespaceId).CountAsync(ct);
        }
        return count;
    }

    // Two new keyed records beside the one a fact is about, which puts it on the multi-row enqueue.
    private async Task<int> SeedCompanionsAsync(bool inBatch)
    {
        if (!inBatch)
        {
            return 0;
        }

        await Fixture.SeedOutboxRowAsync(SourceTable, EchoRow(Unique("companion-a")));
        await Fixture.SeedOutboxRowAsync(SourceTable, EchoRow(Unique("companion-b")));
        return 2;
    }

    // The job id under each seed's ref; a ref no job holds is left out.
    private async Task<List<long>> JobIdsAsync(IEnumerable<OutboxSeed> seeds, CancellationToken ct)
    {
        var ids = new List<long>();
        foreach (var seed in seeds)
        {
            if (await Jobs.GetAsync(new JobRef(seed.JobRef), ct) is { } job)
            {
                ids.Add(job.JobId);
            }
        }
        return ids;
    }

    private async Task<List<(string Name, string? Value)>> TagsAsync(long jobId, CancellationToken ct) =>
        [
            .. (await Db.From<TagEntity>().Where(t => t.EntityCode == EntityCode.Job && t.EntityId == jobId).ToListAsync(ct))
                .Select(static t => (t.TagName, t.TagValue))
                .OrderBy(static t => t.TagName, StringComparer.Ordinal),
        ];

    private async Task<(JobStatusCode Status, long Version, DateTime ModifiedAtUtc)> StateAsync(long jobId, CancellationToken ct)
    {
        var state = Assert.Single(await Db.From<JobStateEntity>().Where(s => s.Id == jobId).ToListAsync(ct));
        return (state.Status, state.Version, state.ModifiedAtUtc);
    }
}
