using System.Data;
using Acta.Tests.Conformance.Postgres.Testing;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Postgres.Features.Jobs;

/// <summary>
/// PostgreSQL answers a statement from the snapshot it began with, and a row lock it waited for does not refresh the
/// other rows it joined. An enqueue that waited must not act on what it read before the wait.
/// </summary>
[Deploys("add-numbers")]
public sealed class PgEnqueueSnapshotTests : ActaRuntimeTestBase<PgConformanceFixture, JobsOf<PgEnqueueSnapshotTests>>
{
    private static readonly JobPayload Payload = JobPayload.Json(new AddNumbers(1, 2));

    [Fact(DisplayName = "A child enqueued behind its parent's cancel is refused once the cancel commits")]
    public async Task A_child_enqueue_that_waited_on_a_cancel_reads_the_cancelled_parent()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", Payload), ct);

        // The locks and the write a cancel makes, held open: the job's two rows, then its status.
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var cancel = await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = cancel;
            cmd.CommandText =
                $"SELECT 1 FROM {Db.Schema}.jobs j JOIN {Db.Schema}.jobs_state r ON r.job_id = j.job_id WHERE j.job_id = {parent.JobId} FOR UPDATE";
            Assert.NotNull(await cmd.ExecuteScalarAsync(ct));
            cmd.CommandText = $"UPDATE {Db.Schema}.jobs_state SET job_status_code = 220 WHERE job_id = {parent.JobId}";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct));
        }

        var child = Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", Payload, ParentJobId: parent.JobId), ct).AsTask();
        await LockWaitProbe.WaitAsync(Db, "enqueue_job", LockWait.Row, child, cancel, ct);
        await cancel.CommitAsync(ct);

        // The cancel's walk of the parent's children ran, or will run, without this child: it must not exist.
        var refused = await Assert.ThrowsAsync<EnqueueRejectedException>(() => child);
        Assert.Equal(EnqueueRejectionReason.ParentUnavailable, refused.Reason);
        Assert.Empty(await Db.From<JobEntity>().Where(j => j.ParentJobId == parent.JobId).ToListAsync(ct));
    }

    [Fact(
        DisplayName = "A deduplicated enqueue under REPEATABLE READ that meets a key committed since its snapshot fails as a serialization error"
    )]
    public async Task A_repeatable_read_enqueue_cannot_answer_a_key_it_cannot_see()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("rr-key");
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using (var cmd = connection.CreateCommand())
        {
            // The transaction's first statement takes its snapshot.
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
        }

        await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", Payload, DeduplicationKey: key), ct);

        // The key exists and this transaction cannot see it: neither a second job nor the first one's id is an honest
        // answer, so the caller retries its transaction, as for any serialization failure.
        var failed = await Assert.ThrowsAnyAsync<Exception>(() =>
            Jobs.EnqueueAsync(transaction, new EnqueueJobRequest(TestNamespace, "add-numbers", Payload, DeduplicationKey: key), ct).AsTask()
        );
        Assert.Equal("40001", Assert.IsType<Npgsql.PostgresException>(failed).SqlState);
    }

    [Fact(
        DisplayName = "A batch that passed a definition's tenant policy keeps its named tenant when a deploy forbids one before it stages"
    )]
    public async Task A_batch_keeps_its_named_tenant_when_the_policy_turns_forbidden_mid_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = Unique("tenant");
        await Operations.Tenants.RegisterAsync(tenant, null, null, ct);
        var namespaceId = await ChaosSpecHelpers.NamespaceIdAsync(Db, TestNamespace, ct);
        EnqueueJobRequest[] named =
        [
            new(TestNamespace, "add-numbers", Payload, TenantName: tenant),
            new(TestNamespace, "add-numbers", Payload, TenantName: tenant),
        ];

        // A first batch creates the routine's staging table in this transaction; the trigger then holds the next batch
        // where the routine empties that table, after its policy checks and before the statement that stages the rows.
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Jobs.EnqueueJobsAsync(transaction, [named[0] with { TenantName = null }, named[1] with { TenantName = null }], ct);
        var barrier = $"hashtext('{Db.Schema}.enqueue-barrier'), {namespaceId}";
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText =
                $"CREATE FUNCTION pg_temp.hold_staging() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_advisory_xact_lock({barrier}); RETURN NULL; END $$; "
                + "CREATE TRIGGER hold_staging BEFORE TRUNCATE ON _enq_batch FOR EACH STATEMENT EXECUTE FUNCTION pg_temp.hold_staging()";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var holder = await Db.OpenConnectionAsync(ct);
        await using var hold = await holder.BeginTransactionAsync(ct);
        await using (var cmd = holder.CreateCommand())
        {
            cmd.Transaction = hold;
            cmd.CommandText = $"SELECT pg_advisory_xact_lock({barrier})";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var batch = Jobs.EnqueueJobsAsync(transaction, named, ct).AsTask();
        await LockWaitProbe.WaitAsync(Db, "enqueue_jobs", LockWait.ApplicationLock, batch, hold, ct);

        // What a deploy of the definition with a Forbidden requirement writes, committed while the batch is held.
        Assert.Equal(
            1,
            await Db.From<DefinitionEntity>()
                .Where(d => d.NamespaceId == namespaceId && d.DefinitionName == "add-numbers")
                .UpdateOnlyAsync(() => new DefinitionEntity { TenantRequirement = DefinitionTenantRequirementCode.Forbidden }, ct)
        );
        await hold.CommitAsync(ct);

        // The batch was valid when it was judged, so it lands as it was judged: before the deploy, tenant and all.
        var landed = await batch;
        await transaction.CommitAsync(ct);
        var tenantId = (await Db.From<TenantEntity>().Where(t => t.TenantName == tenant).SingleOrDefaultAsync(ct))!.Id;
        Assert.All(landed.Jobs, job => Assert.Equal(EnqueueJobAction.Inserted, job.Action));
        foreach (var job in landed.Jobs)
        {
            Assert.Equal(tenantId, (await ReadJobAsync(job.JobId, ct)).TenantId);
        }
    }
}
