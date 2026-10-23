using Acta.Tests.Conformance.Sqlite.Testing;
using Acta.Tests.Conformance.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Sqlite.Features.Execution;

/// <summary>
/// SQLite's start_step runs as several statements, and each reads the clock afresh, so a step's retry instant can come
/// due between the UPDATE that spends an attempt and the SELECT that answers. The provider's own script runs here
/// statement by statement on a connection whose <c>unixepoch</c> is overridden, so the clock crosses the instant
/// exactly between those two statements, which no real timing does on demand.
/// </summary>
public sealed class SqliteStartStepClockTests : ActaRuntimeTestBase<SqliteConformanceFixture, TestJobsManifest>
{
    private const string StepName = "charge";

    [Fact(
        DisplayName = "A retry instant that comes due between start_step's statements suspends, so two allowed attempts run the body twice"
    )]
    public async Task Retry_coming_due_between_statements_suspends_and_spends_no_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var execution = Services.GetRequiredService<ExecutionOperations>();
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var fence = await ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct);
        var invocations = 0;

        // Attempt 1 runs and fails inside a two-attempt budget, leaving a retry instant a minute out.
        var first = await execution.StartStepAsync(fence, StepName, atMostOnce: false, ct);
        Assert.Equal(StartStepOutcomeCode.Invoke, first.Outcome);
        invocations++;
        var failed = await FailAsync(execution, fence, first.Version, ct);
        Assert.Equal(CompleteStepOutcomeCode.RetryScheduled, failed.Outcome);
        var spent = await ReadStepAsync(enqueued.JobId, ct);

        var raced = await StartAcrossTheRetryInstantAsync(fence, ct);

        Assert.Equal(StartStepOutcomeCode.Suspend, raced);
        var unchanged = await ReadStepAsync(enqueued.JobId, ct);
        Assert.Equal(spent.AttemptNumber, unchanged.AttemptNumber);
        Assert.Equal(spent.Version, unchanged.Version);

        // Once the instant has passed for every statement, attempt 2 runs, and its failure spends the budget.
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.steps SET next_retry_at_utc = @p_due WHERE job_id = @p_id AND step_name = @p_name",
            ct,
            ("@p_due", DateTime.UtcNow.AddMinutes(-1)),
            ("@p_id", enqueued.JobId),
            ("@p_name", StepName)
        );
        var second = await execution.StartStepAsync(fence, StepName, atMostOnce: false, ct);
        Assert.Equal(StartStepOutcomeCode.Invoke, second.Outcome);
        Assert.Equal(2, second.AttemptNumber);
        invocations++;
        Assert.Equal(CompleteStepOutcomeCode.Exhausted, (await FailAsync(execution, fence, second.Version, ct)).Outcome);
        Assert.Equal(StartStepOutcomeCode.Exhausted, (await execution.StartStepAsync(fence, StepName, atMostOnce: false, ct)).Outcome);

        Assert.Equal(2, invocations);
    }

    // Runs start_step's statements in order with the clock 5 ms before the step's retry instant, then moves it 5 ms
    // past the instant for the final SELECT, and answers that SELECT's outcome. The margin is wider than the
    // millisecond the script's seconds-to-milliseconds cast can lose. The connection is unpooled, so no other
    // connection ever inherits the overridden clock.
    private static async Task<StartStepOutcomeCode> StartAcrossTheRetryInstantAsync(ExecutionFence fence, CancellationToken ct)
    {
        var unpooled = new SqliteConnectionStringBuilder(SqliteIntegrationSchema.BootstrappedConnectionString) { Pooling = false };
        await using var conn = new SqliteConnection(unpooled.ConnectionString);
        await conn.OpenAsync(ct);
        conn.CreateFunction<string, long>("acta_error", static message => throw new SqliteException(message, 1), isDeterministic: true);

        long retryAtMs;
        await using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT next_retry_at_utc FROM main.steps WHERE job_id = @p_job_id AND step_name = @p_step_name";
            read.Parameters.AddWithValue("@p_job_id", fence.JobId);
            read.Parameters.AddWithValue("@p_step_name", StepName);
            retryAtMs = (long)(await read.ExecuteScalarAsync(ct))!;
        }

        var nowMs = retryAtMs - 5;
        conn.CreateFunction<string, string, double>("unixepoch", (_, _) => nowMs / 1000.0, isDeterministic: false);

        var statements = Statements(new SqlResourceCatalog("sqlite", "main").Load("StartStep"));
        for (var i = 0; i < statements.Count - 1; i++)
        {
            await using var run = Command(conn, statements[i], fence);
            await run.ExecuteNonQueryAsync(ct);
        }

        nowMs = retryAtMs + 5;
        await using var answer = Command(conn, statements[^1], fence);
        await using var reader = await answer.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        return (StartStepOutcomeCode)reader.GetInt32(0);
    }

    // A statement ends at a semicolon closing a line that is not a comment.
    private static List<string> Statements(string sql)
    {
        var statements = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var line in sql.Split('\n'))
        {
            current.Append(line).Append('\n');
            var text = line.TrimEnd();
            if (!text.TrimStart().StartsWith("--", StringComparison.Ordinal) && text.EndsWith(';'))
            {
                statements.Add(current.ToString());
                current.Clear();
            }
        }
        return statements;
    }

    private static SqliteCommand Command(SqliteConnection conn, string sql, ExecutionFence fence)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@p_job_id", fence.JobId);
        cmd.Parameters.AddWithValue("@p_execution_number", fence.ExecutionNumber);
        cmd.Parameters.AddWithValue("@p_leased_by_worker_id", fence.WorkerId);
        cmd.Parameters.AddWithValue("@p_step_name", StepName);
        cmd.Parameters.AddWithValue("@p_at_most_once", 0);
        return cmd;
    }

    private static Task<CompleteStepResponse> FailAsync(
        ExecutionOperations execution,
        ExecutionFence fence,
        int version,
        CancellationToken ct
    ) =>
        execution.CompleteStepAsync(
            new CompleteStepCommand(
                fence,
                StepName,
                Succeeded: false,
                ResultFormatId: 0,
                Result: null,
                StepFailureCode.UnhandledException,
                "card declined",
                DelaySeconds: 60,
                MaxAttempts: 2,
                RetryWindowSeconds: null,
                version
            ),
            ct
        );

    private async Task<StepEntity> ReadStepAsync(long jobId, CancellationToken ct) =>
        Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.StepName == StepName).ToListAsync(ct));
}
