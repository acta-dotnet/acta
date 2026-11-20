using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Two first starts of one step: the one that waited behind the other's open transaction did not open the step, so it is
/// told to invoke again. The first start's transaction holds the step until another session waits on it, with deadlock
/// retry off, so the spec runs on a schema of its own. SQLite runs one writer at a time, so no start runs while another
/// holds the step there.
/// </summary>
[ConformanceSpec(
    "start-job-step.race",
    "A first start that waited for another to open the step invokes it again",
    Area = "Execution",
    Contract = "Of two first starts of one step, only the one whose call opened it is told to invoke, and the other is told to invoke again."
)]
[Deploys("add-numbers")]
public sealed class StartJobStepRaceSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<StartJobStepRaceSpec>>
{
    private const string StepName = "charge";

    protected override bool OwnSchema => true;

    [Fact(DisplayName = "A first start that waited for a concurrent one to open the step is invoked again")]
    public async Task A_first_start_that_waited_for_another_is_invoked_again()
    {
        Assert.SkipWhen(Db.Provider == DbProvider.Sqlite, "SQLite has one writer: no start runs while another holds the step.");
        var ct = TestContext.Current.CancellationToken;
        var job = await ExecutionFenceStaging.ExecutingAsync(
            Db,
            (await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct)).JobId,
            ct
        );

        // The first start opens the step and holds it to commit. On SQL Server the second waits on the step's own lock
        // before it reads; on PostgreSQL it reads no row, and its insert waits on the first one's.
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var holder = await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = holder;
            cmd.CommandText =
                Db.Provider == DbProvider.SqlServer
                    ? $"EXEC {Db.Qualify("start_job_step")} @p_job_id = {job.JobId}, @p_execution_number = {job.ExecutionNumber}, "
                        + $"@p_leased_by_worker_id = {job.WorkerId}, @p_step_name = '{StepName}', @p_at_most_once = 0"
                    : $"SELECT outcome_code FROM {Db.Qualify("start_job_step")}({job.JobId}::bigint, {job.ExecutionNumber}::bigint, "
                        + $"{job.WorkerId}, '{StepName}'::varchar, false)";
            Assert.Equal((short)StartJobStepOutcomeCode.Invoke, Convert.ToInt16(await cmd.ExecuteScalarAsync(ct)));
        }

        var second = Services.GetRequiredService<ExecutionOperations>().StartJobStepAsync(job, StepName, atMostOnce: false, ct);
        var kind = Db.Provider == DbProvider.SqlServer ? LockWait.ApplicationLock : LockWait.Row;
        await LockWaitProbe.WaitAsync(Db, "start_job_step", kind, second, holder, ct);
        await holder.CommitAsync(ct);

        Assert.Equal(StartJobStepOutcomeCode.InvokeAgain, (await second).Outcome);
        var jobId = job.JobId;
        var step = Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.StepName == StepName).ToListAsync(ct));
        Assert.Equal(StepStatusCode.Pending, step.Status);
    }
}
