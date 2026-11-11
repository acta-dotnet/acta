using System.Data.Common;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Per-definition concurrency-limit spec. The key owns slots <c>{ns}.sem.{key}.0</c> ..
/// <c>.{limit-1}</c>, taken after the start CAS and released when the handler finishes. Capacity on a
/// shared key is the largest limit among the participants, and a smaller-limit definition competes for
/// the first N slots only, so lowering one definition never takes slots away from another. A limit of
/// 1 is slot 0 alone, exactly the pre-limit mutex. Most facts drive the lock store directly; two run real
/// handlers through the runtime, one against a slot held elsewhere, which pins the arithmetic end to end, and
/// one making every admission race for the same free slot behind another admission's uncommitted hold.
/// </summary>
[ConformanceSpec(
    "concurrency-limit.slots",
    "A definition's concurrency limit is how many of its key's lock slots exist",
    Area = "Concurrency",
    Contract = "A concurrency key admits at most its definition's limit at once, one lock slot each, taken lowest free first, a lost race retried up to the limit."
)]
[Deploys("concurrency-limit-probe", "concurrency-slot-gate")]
public sealed class ConcurrencyLimitSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ConcurrencyLimitSpec>>
{
    private static readonly TimeSpan HeldTtl = TimeSpan.FromMinutes(5);

    // A lease that ran out a second ago. Not zero: a provider that stores the instant at coarser
    // precision than it reads the clock can round a zero-length lease a fraction into the future.
    private static readonly TimeSpan ExpiredTtl = TimeSpan.FromSeconds(-1);

    [Fact(DisplayName = "Limit 1 is the single lock slot the exclusive key always was")]
    public async Task Limit_one_is_the_single_slot_mutex()
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = SlotPrefix(Unique("cl-mutex"));
        var locks = Locks;

        var first = (await locks.AcquireLockSlotAsync(prefix, limit: 1, HeldTtl, ownerJobId: 1, ct)).Token;
        var second = (await locks.AcquireLockSlotAsync(prefix, limit: 1, HeldTtl, ownerJobId: 2, ct)).Token;

        Assert.NotNull(first);
        Assert.Equal($"{prefix}.0", first!.Value.Key);
        Assert.Null(second);

        await locks.ReleaseLockAsync(first.Value, ct);
    }

    [Fact(DisplayName = "Admission fills the lowest free lock slot and stops at the limit")]
    public async Task Admission_fills_the_lowest_free_slot_and_stops_at_the_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = SlotPrefix(Unique("cl-fill"));
        var locks = Locks;

        var held = new List<LockToken>();
        for (var i = 0; i < 2; i++)
        {
            var token = (await locks.AcquireLockSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: 100 + i, ct)).Token;
            Assert.NotNull(token);
            Assert.Equal($"{prefix}.{i}", token!.Value.Key);
            held.Add(token.Value);
        }

        Assert.Null((await locks.AcquireLockSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: 999, ct)).Token);

        // Releasing the lowest slot hands it back first: the scan is by slot number, not by age.
        await locks.ReleaseLockAsync(held[0], ct);
        var reused = (await locks.AcquireLockSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: 1000, ct)).Token;
        Assert.Equal($"{prefix}.0", reused?.Key);

        await locks.ReleaseLockAsync(reused!.Value, ct);
        await locks.ReleaseLockAsync(held[1], ct);
    }

    [Fact(DisplayName = "A shared key's capacity is the largest limit among its participants")]
    public async Task A_shared_keys_capacity_is_the_largest_limit_among_participants()
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = SlotPrefix(Unique("cl-shared"));
        var locks = Locks;

        // The smaller-limit participant competes for slots 0 and 1 only, whatever else is free.
        var small = new List<LockToken>();
        for (var i = 0; i < 2; i++)
        {
            var token = (await locks.AcquireLockSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: 200 + i, ct)).Token;
            Assert.NotNull(token);
            small.Add(token!.Value);
        }
        Assert.Null((await locks.AcquireLockSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: 209, ct)).Token);

        // The larger-limit participant still reaches slots 2 and 3: the small limit took nothing away.
        var large = new List<LockToken>();
        for (var i = 2; i < 4; i++)
        {
            var token = (await locks.AcquireLockSlotAsync(prefix, limit: 4, HeldTtl, ownerJobId: 300 + i, ct)).Token;
            Assert.Equal($"{prefix}.{i}", token?.Key);
            large.Add(token!.Value);
        }

        Assert.Null((await locks.AcquireLockSlotAsync(prefix, limit: 4, HeldTtl, ownerJobId: 399, ct)).Token);

        foreach (var token in small.Concat(large))
        {
            await locks.ReleaseLockAsync(token, ct);
        }
    }

    [Fact(DisplayName = "A lowered limit admits into the low lock slots while the high holders run on")]
    public async Task A_lowered_limit_admits_into_the_low_slots_while_high_holders_run_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = SlotPrefix(Unique("cl-lowered"));
        var locks = Locks;

        // The state a decrease from 4 to 2 leaves behind: slots 2 and 3 held by attempts admitted
        // under the old limit, which run to completion.
        var high = new List<LockToken>();
        for (var i = 0; i < 4; i++)
        {
            var token = (await locks.AcquireLockSlotAsync(prefix, limit: 4, HeldTtl, ownerJobId: 400 + i, ct)).Token;
            high.Add(token!.Value);
        }
        await locks.ReleaseLockAsync(high[0], ct);
        await locks.ReleaseLockAsync(high[1], ct);

        // A worker that has observed the lowered limit sees exactly two slots and fills both.
        Assert.Equal($"{prefix}.0", (await locks.AcquireLockSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: 500, ct)).Token?.Key);
        Assert.Equal($"{prefix}.1", (await locks.AcquireLockSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: 501, ct)).Token?.Key);
        Assert.Null((await locks.AcquireLockSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: 502, ct)).Token);
    }

    [Fact(DisplayName = "A lock slot whose lease expired is stolen by the next admission")]
    public async Task A_slot_whose_lease_expired_is_stolen_by_the_next_admission()
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = SlotPrefix(Unique("cl-expiry"));
        var locks = Locks;

        // Slot 0 is held by an owner whose lease has already run out (the crashed-worker shape), so
        // the next admission steals it in place rather than moving on to slot 1.
        var stale = (await locks.AcquireLockSlotAsync(prefix, limit: 2, ExpiredTtl, ownerJobId: 600, ct)).Token;
        Assert.Equal($"{prefix}.0", stale?.Key);

        var stolen = (await locks.AcquireLockSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: 601, ct)).Token;
        Assert.Equal($"{prefix}.0", stolen?.Key);

        // The steal re-minted the hold token, so the stale owner can no longer free its successor.
        Assert.False((await locks.ReleaseLockAsync(stale!.Value, ct)).Released);

        await locks.ReleaseLockAsync(stolen!.Value, ct);
    }

    [Fact(DisplayName = "Real same-key handlers run at the limit minus the lock slots held elsewhere")]
    public async Task Real_same_key_handlers_run_at_the_limit_minus_the_slots_held_elsewhere()
    {
        const int jobs = 4;
        const int workers = 4;
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("cl-runtime");
        var locks = Locks;

        // One of the definition's two slots is held on behalf of a foreign owner, as another worker's
        // live execution would hold it, leaving exactly one for this namespace's executors.
        var foreign = (await locks.AcquireLockSlotAsync(SlotPrefix(key), ConcurrencyLimitProbe.Limit, HeldTtl, long.MaxValue, ct)).Token;
        Assert.NotNull(foreign);

        ConcurrencyLimitProbe.Reset(TestNamespace);
        for (var i = 0; i < jobs; i++)
        {
            await Jobs.EnqueueAsync(
                new EnqueueJobRequest(TestNamespace, "concurrency-limit-probe", JobPayload.None) { ConcurrencyKey = key },
                ct
            );
        }

        var completed = 0;
        var deadline = DateTime.UtcNow + SpecWaits.Converge;
        await Task.WhenAll(
            Enumerable
                .Range(0, workers)
                .Select(async _ =>
                {
                    while (Volatile.Read(ref completed) < jobs && DateTime.UtcNow < deadline)
                    {
                        if (await Runtime.RunOnceAsync(TestNamespace, ct) == RunOnceStatus.Completed)
                        {
                            Interlocked.Increment(ref completed);
                        }
                        else
                        {
                            await Task.Delay(25, ct);
                        }
                    }
                })
        );

        Assert.Equal(jobs, completed);
        Assert.Equal(1, ConcurrencyLimitProbe.MaxObserved(TestNamespace));

        await locks.ReleaseLockAsync(foreign!.Value, ct);
    }

    [Fact(DisplayName = "Admissions racing for the same free lock slot fill every slot, and only a full key bounces")]
    public async Task Admissions_racing_for_the_same_free_slot_fill_every_slot()
    {
        const int racers = 2 * ConcurrencySlotGate.Limit;
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("cl-race");
        ConcurrencySlotGate.Reset(TestNamespace);
        var jobs = new List<EnqueueJobResponse>();
        for (var i = 0; i < racers; i++)
        {
            jobs.Add(
                await Jobs.EnqueueAsync(
                    new EnqueueJobRequest(TestNamespace, "concurrency-slot-gate", JobPayload.None) { ConcurrencyKey = key },
                    ct
                )
            );
        }

        // On a server every racer waits on slot 0, which another admission holds uncommitted, so all of them
        // contend for the free slots at once when it commits. SQLite runs one writer at a time, so nothing races there.
        await using var connection = Db.Provider == DbProvider.Sqlite ? null : await Db.OpenConnectionAsync(ct);
        await using var holder = connection is null ? null : await connection.BeginTransactionAsync(ct);
        var held = holder is null ? (LockToken?)null : await TakeSlotAsync(holder, SlotPrefix(key), ct);
        var runs = jobs.Select(job => Runtime.RunOnceAsync(job, ct)).ToArray();
        if (holder is not null)
        {
            await LockWaitProbe.WaitAsync(Db, SlotAcquireSql, LockWait.Row, racers, Task.WhenAny(runs), ct);
            await holder.CommitAsync(ct);
        }

        // An admitted attempt holds its slot inside the handler until released, so every racer is settled once each
        // one has entered or bounced.
        await SpecWaits.UntilAsync(
            () => ConcurrencySlotGate.Entered(TestNamespace) + runs.Count(run => run.IsCompleted) == racers,
            "Every racing admission either entered the handler or bounced.",
            ct
        );
        var free = ConcurrencySlotGate.Limit - (held is null ? 0 : 1);
        var bounced = await Task.WhenAll(runs.Where(run => run.IsCompleted));
        Assert.Equal(free, ConcurrencySlotGate.Entered(TestNamespace));
        Assert.Equal(racers - free, bounced.Length);
        Assert.All(bounced, outcome => Assert.Equal(RunOnceStatus.Rearmed, outcome));
        Assert.Equal(ConcurrencySlotGate.Limit, await CountSlotRowsAsync(SlotPrefix(key), ct));

        ConcurrencySlotGate.Release(TestNamespace);
        Assert.Equal(free, (await Task.WhenAll(runs)).Count(outcome => outcome == RunOnceStatus.Completed));
        if (held is { } token)
        {
            await Locks.ReleaseLockAsync(token, ct);
        }
    }

    private LocksOperations Locks => Services.GetRequiredService<LocksOperations>();

    // Text only the slot acquire runs, near its start, since PostgreSQL keeps only the first kilobyte of a running query.
    private string SlotAcquireSql => Db.Provider == DbProvider.SqlServer ? "@p_slot_count" : "generate_series";

    // The slot acquire an admission runs, inside a transaction the caller has not committed.
    private async Task<LockToken> TakeSlotAsync(DbTransaction transaction, string prefix, CancellationToken ct)
    {
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var holdToken = Guid.NewGuid();
        var taken = await Db.ExecuteInTransactionAsync(
            transaction,
            new StoreCommand("AcquireLockSlot"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LockKeyPrefix, prefix));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LockSlotCount, ConcurrencySlotGate.Limit));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.JobId, long.MaxValue));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, (int)HeldTtl.TotalSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, holdToken));
            },
            static reader => reader.GetString(0),
            ct
        );
        return new LockToken(Assert.Single(taken), holdToken);
    }

    private async Task<int> CountSlotRowsAsync(string prefix, CancellationToken ct)
    {
        var rows = 0;
        for (var slot = 0; slot <= ConcurrencySlotGate.Limit; slot++)
        {
            var slotKey = $"{prefix}.{slot}";
            rows += await Db.From<LockEntity>().Where(l => l.LockKey == slotKey).CountAsync(ct);
        }
        return rows;
    }

    // The composition the runner uses: namespace id, the sem discriminator, and the exact key.
    private string SlotPrefix(string key) => $"{Runtime.RegisteredNamespaceIds[TestNamespace]}.sem.{key}";
}
