using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Stages a job Executing at an execution number under a worker, as a claim leaves it, and answers the fence that
/// execution's writes carry. Specs that drive a handler-only write directly, with no worker running the job, write
/// through this fence; staging again with a higher number or another worker is the claim of the next execution. An
/// executing row holds no result (<c>ck_jobs_state_result_idle</c>), so a finished job's stored result is dropped. A
/// job seeded as a bare <c>jobs</c> row gets its <c>jobs_state</c> row here.
/// </summary>
internal static class ExecutionFenceStaging
{
    public static async Task<ExecutionFence> ExecutingAsync(
        IDbSession db,
        long jobId,
        CancellationToken ct,
        int executionNumber = 1,
        int workerId = JobStateStaging.StagedWorkerId
    )
    {
        if (await db.From<JobStateEntity>().Where(r => r.Id == jobId).CountAsync(ct) == 0)
        {
            var job = await db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct);
            Assert.NotNull(job);
            await db.From<JobStateEntity>()
                .InsertAsync<long>(
                    new JobStateEntity
                    {
                        Id = jobId,
                        NamespaceId = job.NamespaceId,
                        Status = JobStatusCode.Ready,
                        Priority = JobPriorityCode.Normal,
                        NextRunAtUtc = DateTime.UtcNow,
                        ExecutionNumber = executionNumber,
                    },
                    ct
                );
        }

        var staged = await db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET job_status_code = @p_status, execution_number = @p_execution, "
                + "leased_by_worker_id = @p_worker, lease_expires_at_utc = @p_expires, result_format_id = 0, result = NULL "
                + "WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Executing),
            ("@p_execution", executionNumber),
            ("@p_worker", workerId),
            ("@p_expires", DateTime.UtcNow.AddMinutes(5)),
            ("@p_id", jobId)
        );
        Assert.Equal(1, staged);
        return new ExecutionFence(jobId, executionNumber, workerId);
    }
}
