using System.Data.Common;
using Acta.Runtime.Services.Locks;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// The meter after a lock convoy. Every reservation on one key serializes on the bucket row, so a
/// flood of metered claims queues behind whichever call holds it. A call that waited in that queue
/// must judge its turn against the clock as it stands when it finally holds the row, not as it stood
/// when the call began: judged against a stale instant, every call the convoy delayed reads as due
/// and is admitted together the moment the lock frees, several times the contract in one second.
/// The million-job certification on SQL Server read 46 admissions in a second against a budget of 30
/// that way. PostgreSQL reads the transaction start as now, the same stale instant in principle; its
/// million run stayed at 21 and its previous routine stays inside the range here, so the fact
/// discriminates on SQL Server and holds the contract on both.
/// </summary>
[ConformanceSpec(
    "rate-limit.convoy",
    "A meter freed after a lock convoy admits its burst, not every queued call",
    Area = "Concurrency",
    Contract = "Reservations delayed behind a held bucket row are judged at the clock when the row frees, so the release admits the burst and books the rest.",
    Arrange = "A primed meter whose bucket row is held by an outside transaction while reservations arrive every fifty milliseconds.",
    Act = "The outside transaction commits and the queued reservations execute.",
    Assert = "The admissions among them are bounded by the burst and the interval, not by the queue's length."
)]
[CoversStoreMethod(typeof(ILockStore), nameof(ILockStore.ReserveRateAsync))]
public abstract class RateLimitConvoySpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int Burst = 10;
    private const int IntervalMilliseconds = 100;
    private const int GraceSeconds = 5;

    // Fifty calls spaced over the hold: a convoy is only a convoy when its calls began at different
    // instants, and the stale-clock reading admits about ten per second of that spread.
    private const int QueuedCalls = 50;
    private static readonly TimeSpan CallSpacing = TimeSpan.FromMilliseconds(50);

    [Fact(DisplayName = "Reservations queued behind a held bucket row are admitted at the burst when it frees, not all at once")]
    public async Task Queued_reservations_are_judged_at_the_clock_when_the_row_frees()
    {
        Assert.SkipWhen(
            Db.Provider == DbProvider.Sqlite,
            "SQLite serializes the whole batch under its single write lock and reads the clock inside it, so no call can queue with a stale instant."
        );
        var ct = TestContext.Current.CancellationToken;
        var locks = Services.GetRequiredService<ILockStore>();
        var key = $"{TestNamespace}.rate.convoy";

        // One reservation creates the bucket row and sets its instant.
        Assert.True((await locks.ReserveRateAsync(key, 1, IntervalMilliseconds, Burst, GraceSeconds, ct)).Admitted);

        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        await using (var lockRow = holder.CreateCommand())
        {
            lockRow.Transaction = hold;
            lockRow.CommandText =
                Db.Provider == DbProvider.SqlServer
                    ? $"SELECT lock_key FROM {Db.Schema}.locks WITH (UPDLOCK, HOLDLOCK) WHERE lock_key = @k"
                    : $"SELECT lock_key FROM {Db.Schema}.locks WHERE lock_key = @k FOR UPDATE";
            var p = lockRow.CreateParameter();
            p.ParameterName = "@k";
            p.Value = key;
            lockRow.Parameters.Add(p);
            Assert.Equal(key, await lockRow.ExecuteScalarAsync(ct));
        }

        // The convoy: each call blocks on the held row the instant it reaches the store.
        var queued = new List<Task<RateReservation>>(QueuedCalls);
        for (var i = 0; i < QueuedCalls; i++)
        {
            var jobId = 100 + i;
            queued.Add(Task.Run(() => locks.ReserveRateAsync(key, jobId, IntervalMilliseconds, Burst, GraceSeconds, ct), ct));
            await Task.Delay(CallSpacing, ct);
        }

        await hold.CommitAsync(ct);
        var results = await Task.WhenAll(queued);

        // Judged at the release instant, the meter admits its burst and books the rest a turn. The
        // calls execute serially after the release, so the real clock advances a fraction of a second
        // across them and a few more turns come due; the stale reading admits about thirty.
        var admitted = results.Count(r => r.Admitted);
        Assert.InRange(admitted, 1, Burst + 5);
        Assert.All(results.Where(r => !r.Admitted), r => Assert.True(r.WaitMilliseconds > 0));
    }
}
