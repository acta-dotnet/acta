using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>GetJobExplanation</c>: the composite explain read of a job's header (snapshot, leasing worker,
/// latest reason), steps, and checkpoints in one statement, so from one snapshot, for a known job id, and <c>null</c>
/// for an unknown id.
/// </summary>
[ConformanceSpec(
    "get-job-explanation.point-read",
    "GetJobExplanation returns explain sets for a known id and null otherwise",
    Area = "Reads",
    Contract = "GetJobExplanation returns the header, steps, and checkpoints of a matching job id from one snapshot, and null when no row matches."
)]
public sealed class GetJobExplanationSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A known job id returns a populated header (Ready) with no steps or checkpoints")]
    public async Task Returns_explain_data_for_known_job_id()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var results = (
            await EnqueueTestOps.EnqueueBatchAsync(
                Services,
                [new JobEnqueueValues(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload)],
                ct
            )
        ).Jobs;
        var jobId = results[0].JobId;

        var data = await Services.GetRequiredService<GetJobExplanationHandler>().ReadAsync(jobId, ct);

        Assert.NotNull(data);
        Assert.Equal(jobId, data!.Header.JobId);
        Assert.Equal(JobStatusCode.Ready, data.Header.Status);
        Assert.Equal(TestNamespace, data.Header.JobNamespace);
        Assert.Equal("add-numbers", data.Header.JobName);
        Assert.Null(data.Header.LeasedByWorkerId);
        Assert.Null(data.Header.LatestFailureMessage);
        Assert.Null(data.Header.LatestFailureDetails);
        Assert.Empty(data.Steps);
        Assert.Empty(data.Checkpoints);
    }

    [Fact(DisplayName = "A step and a checkpoint changed in one transaction are never read half-changed")]
    public async Task A_step_and_a_checkpoint_written_together_are_read_together()
    {
        Assert.SkipWhen(
            Db.Provider == DbProvider.SqlServer && IntegrationConfig.SqlServerRcsiOff,
            "Without read-committed snapshot a SQL Server statement reads committed rows one at a time, not one snapshot."
        );
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);
        await WriteAsync(
            [
                $"INSERT INTO {Db.Schema}.steps (job_id, step_name, step_status_code, attempt_number, result_format_id) VALUES ({job.JobId}, 'charge', 10, 1, 0)",
                $"INSERT INTO {Db.Schema}.checkpoints (job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code) VALUES ({job.JobId}, 20, 'approval', 10)",
            ],
            ct
        );
        var handler = Services.GetRequiredService<GetJobExplanationHandler>();

        // The writer flips both rows in one transaction, Pending/Pending to Succeeded/Set and back, with the job's
        // jobs_state row untouched, while the reader explains the job over and over until it has read 200 times and
        // seen 20 flips commit. A loaded server reads slower, so it reads until the gate and needs twenty of each.
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flips = 0;
        var writer = Task.Run(
            async () =>
            {
                while (!done.IsCancellationRequested)
                {
                    var (step, checkpoint) = Volatile.Read(ref flips) % 2 == 0 ? (100, 20) : (10, 10);
                    await WriteAsync(
                        [
                            $"UPDATE {Db.Schema}.steps SET step_status_code = {step} WHERE job_id = {job.JobId}",
                            $"UPDATE {Db.Schema}.checkpoints SET checkpoint_status_code = {checkpoint} WHERE job_id = {job.JobId}",
                        ],
                        ct
                    );
                    Interlocked.Increment(ref flips);
                }
            },
            ct
        );
        try
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            var reads = 0;
            for (; (reads < 200 || Volatile.Read(ref flips) < 20) && waited.Elapsed < SpecWaits.Gate; reads++)
            {
                var data = Assert.IsType<GetJobExplanationRows>(await handler.ReadAsync(job.JobId, ct));
                var step = Assert.Single(data.Steps).StepStatus;
                var checkpoint = Assert.Single(data.Checkpoints).CheckpointStatus;
                Assert.Equal(step == StepStatusCode.Succeeded, checkpoint == CheckpointStatusCode.Set);
            }
            Assert.True(reads >= 20 && Volatile.Read(ref flips) >= 20, $"{reads} reads and {Volatile.Read(ref flips)} flips in the gate");
        }
        finally
        {
            await done.CancelAsync();
            await writer;
        }
    }

    /// <summary>The statements in one transaction on a connection of its own.</summary>
    private async Task WriteAsync(IReadOnlyList<string> statements, CancellationToken ct)
    {
        await using var connection = await Db.OpenConnectionAsync(ct);
        await InCallerTransactionAsync(
            connection,
            async transaction =>
            {
                foreach (var sql in statements)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
            },
            ct
        );
    }

    [Fact(DisplayName = "An unknown job id returns null")]
    public async Task Returns_null_for_unknown_job_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var data = await Services.GetRequiredService<GetJobExplanationHandler>().ReadAsync(long.MaxValue, ct);

        Assert.Null(data);
    }
}
