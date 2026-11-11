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
public sealed class PgEnqueueSnapshotTests : ActaRuntimeTestBase<PgConformanceFixture, TestJobsManifest>
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
        var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => child);
        Assert.Contains("terminal parent", refused.MessageText, StringComparison.Ordinal);
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
}
