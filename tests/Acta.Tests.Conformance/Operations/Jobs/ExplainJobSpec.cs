using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>ExplainJob</c>: the composite explain read of a job's header (snapshot, leasing worker,
/// latest reason), newest steps, and checkpoints in one statement, so from one snapshot, for a known job id, and
/// <c>null</c> for an unknown id.
/// </summary>
[ConformanceSpec(
    "explain-job.point-read",
    "ExplainJob returns explain sets for a known id and null otherwise",
    Area = "Reads",
    Contract = "ExplainJob returns the header, the newest steps up to its fetch limit, and the checkpoints of a matching job id from one snapshot, or null when none matches."
)]
public sealed class ExplainJobSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A known job id returns a populated header (Ready) with no steps or checkpoints")]
    public async Task Returns_explain_data_for_known_job_id()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var results = (
            await EnqueueTestOps.EnqueueJobsAsync(
                Services,
                [new JobEnqueueValues(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload)],
                ct
            )
        ).Jobs;
        var jobId = results[0].JobId;

        var data = await Services.GetRequiredService<ExplainJobHandler>().ReadAsync(jobId, ct);

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

    [Fact(DisplayName = "Past the step limit the read keeps the newest steps and the explanation says older ones exist")]
    public async Task Steps_past_the_limit_keep_the_newest()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);
        // One statement stamps every row with the same created_at_utc, so the name orders them, the highest the newest.
        var rows = Enumerable.Range(0, ExplainJobHandler.StepLimit + 2).Select(i => $"({job.JobId}, 's{i:D3}', 10, 0)");
        await WriteAsync(
            [$"INSERT INTO {Db.Schema}.steps (job_id, step_name, step_status_code, result_format_id) VALUES {string.Join(", ", rows)}"],
            ct
        );

        var data = Assert.IsType<ExplainJobRows>(await Services.GetRequiredService<ExplainJobHandler>().ReadAsync(job.JobId, ct));
        var explanation = Assert.IsType<ExplainJobResponse>(await Jobs.ExplainAsync(JobLookup.ById(job.JobId), ct));

        Assert.Equal(ExplainJobHandler.StepLimit + 1, data.Steps.Count);
        Assert.Equal("s001", data.Steps[0].StepName);
        Assert.Equal($"s{ExplainJobHandler.StepLimit + 1:D3}", data.Steps[^1].StepName);
        Assert.True(explanation.StepsHasMore);
        Assert.Equal(ExplainJobHandler.StepLimit, explanation.Steps.Count);
        Assert.Equal("s002", explanation.Steps[0].Name);
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
                $"INSERT INTO {Db.Schema}.steps (job_id, step_name, step_status_code, result_format_id) VALUES ({job.JobId}, 'charge', 10, 0)",
                $"INSERT INTO {Db.Schema}.checkpoints (job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code) VALUES ({job.JobId}, 20, 'approval', 10)",
            ],
            ct
        );
        var handler = Services.GetRequiredService<ExplainJobHandler>();

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
                var data = Assert.IsType<ExplainJobRows>(await handler.ReadAsync(job.JobId, ct));
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

        var data = await Services.GetRequiredService<ExplainJobHandler>().ReadAsync(long.MaxValue, ct);

        Assert.Null(data);
    }
}
