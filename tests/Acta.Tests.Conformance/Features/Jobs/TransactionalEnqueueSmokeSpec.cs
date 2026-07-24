using System.Data.Common;
using System.Globalization;
using Acta.Features.Jobs;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Minimal cross-provider proof that direct transactional enqueue shares one commit boundary with a
/// same-database business mutation. The caller opens its own connection, begins a transaction, inserts
/// a business probe row and enqueues a job through the <c>DbTransaction</c>-first
/// <c>IJobs</c> overloads, and commits or rolls back the single transaction: on commit both rows are
/// durable, on rollback neither is. The single and batch transactional store methods are both
/// exercised. Fuller routing/failure/no-wake/provider-specific coverage is a separate spec family.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.transactional-commit-rollback",
    "Transactional enqueue commits or rolls back with the business write",
    Area = "Enqueue",
    Contract = "A caller-transaction enqueue joins the supplied DbTransaction, so a business write and the enqueue persist together on commit and vanish together on rollback.",
    Arrange = "The test namespace is registered and a one-column business probe table exists in the Acta schema.",
    Act = "A business row is inserted and a job is enqueued on one caller-owned transaction through the transactional IJobs overloads, then committed or rolled back.",
    Assert = "After commit both the business row and the job row are durable, and after rollback neither the business row nor the provisional job row exists."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneInTransactionAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchInTransactionAsync))]
public abstract class TransactionalEnqueueSmokeSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string ProbeTable = "acta_txn_probe";

    // Enqueue-only: register the namespace via InitializeAsync but never run the claim/execute loop.
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "Commit persists both the business row and the single transactional enqueue")]
    public async Task Commit_persists_business_row_and_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = TestKey("commit");

        await using var conn = await Db.OpenConnectionAsync(ct);
        var probe = await Fixture.EnsureBusinessProbeTableAsync(conn, Schema.SchemaName, ProbeTable);

        long jobId;
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await InsertBusinessRowAsync(conn, tx, probe, marker, ct);
            var outcome = await Jobs.EnqueueAsync(tx, Request("add-numbers", new AddNumbers(2, 3)), ct);
            Assert.Equal(JobEnqueueAction.Inserted, outcome.Action);
            jobId = outcome.JobId;
            await tx.CommitAsync(ct);
        }

        Assert.Equal(1, await CountBusinessRowsAsync(conn, probe, marker, ct));
        var job = await Db.From<Job>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(job);
    }

    [Fact(DisplayName = "Rollback discards both the business row and the provisional transactional enqueue")]
    public async Task Rollback_discards_business_row_and_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var marker = TestKey("rollback");

        await using var conn = await Db.OpenConnectionAsync(ct);
        var probe = await Fixture.EnsureBusinessProbeTableAsync(conn, Schema.SchemaName, ProbeTable);

        long provisionalJobId;
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await InsertBusinessRowAsync(conn, tx, probe, marker, ct);
            var outcome = await Jobs.EnqueueAsync(tx, Request("add-numbers", new AddNumbers(4, 5)), ct);
            provisionalJobId = outcome.JobId;
            await tx.RollbackAsync(ct);
        }

        Assert.Equal(0, await CountBusinessRowsAsync(conn, probe, marker, ct));
        var job = await Db.From<Job>().Where(j => j.Id == provisionalJobId).SingleOrDefaultAsync(ct);
        Assert.Null(job);
    }

    [Fact(DisplayName = "A batch transactional enqueue commits and rolls back atomically with the business insert")]
    public async Task Batch_enqueue_is_atomic_with_business_insert()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var conn = await Db.OpenConnectionAsync(ct);
        var probe = await Fixture.EnsureBusinessProbeTableAsync(conn, Schema.SchemaName, ProbeTable);

        // Commit: two-row batch plus a business row all persist.
        var committedMarker = TestKey("batch-commit");
        IReadOnlyList<long> committedIds;
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await InsertBusinessRowAsync(conn, tx, probe, committedMarker, ct);
            var outcomes = await Jobs.EnqueueBatchAsync(
                tx,
                [Request("add-numbers", new AddNumbers(1, 1)), Request("add-numbers", new AddNumbers(2, 2))],
                ct
            );
            Assert.Equal(2, outcomes.Count);
            committedIds = outcomes.Select(o => o.JobId).ToList();
            await tx.CommitAsync(ct);
        }

        Assert.Equal(1, await CountBusinessRowsAsync(conn, probe, committedMarker, ct));
        foreach (var id in committedIds)
        {
            Assert.NotNull(await Db.From<Job>().Where(j => j.Id == id).SingleOrDefaultAsync(ct));
        }

        // Rollback: two-row batch plus a business row all vanish.
        var rolledBackMarker = TestKey("batch-rollback");
        IReadOnlyList<long> rolledBackIds;
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await InsertBusinessRowAsync(conn, tx, probe, rolledBackMarker, ct);
            var outcomes = await Jobs.EnqueueBatchAsync(
                tx,
                [Request("add-numbers", new AddNumbers(3, 3)), Request("add-numbers", new AddNumbers(4, 4))],
                ct
            );
            rolledBackIds = outcomes.Select(o => o.JobId).ToList();
            await tx.RollbackAsync(ct);
        }

        Assert.Equal(0, await CountBusinessRowsAsync(conn, probe, rolledBackMarker, ct));
        foreach (var id in rolledBackIds)
        {
            Assert.Null(await Db.From<Job>().Where(j => j.Id == id).SingleOrDefaultAsync(ct));
        }
    }

    private JobEnqueueRequest Request(string jobName, object input)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(input);
        return new JobEnqueueRequest(TestNamespace, jobName, payload);
    }

    private static async Task InsertBusinessRowAsync(DbConnection conn, DbTransaction tx, string probe, string marker, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {probe} (marker) VALUES (@marker)";
        var p = cmd.CreateParameter();
        p.ParameterName = "@marker";
        p.Value = marker;
        cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> CountBusinessRowsAsync(DbConnection conn, string probe, string marker, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {probe} WHERE marker = @marker";
        var p = cmd.CreateParameter();
        p.ParameterName = "@marker";
        p.Value = marker;
        cmd.Parameters.Add(p);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }
}
