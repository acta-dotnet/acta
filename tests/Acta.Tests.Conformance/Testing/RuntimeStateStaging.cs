namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Raw staging of a job's durable runtime state for specs that need a status the public API cannot
/// reach directly (an in-flight job, a terminal one). Writes the lease alongside the status the way a
/// claim does, which <c>ck_runtimes_status_lease</c> and <c>ck_runtimes_inflight_leased</c> require:
/// exactly the in-flight statuses carry a lease.
/// </summary>
internal static class RuntimeStateStaging
{
    public const int StagedWorkerId = 1;

    public static Task SetStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        statusCode is (byte)JobStatusCode.Dispatched or (byte)JobStatusCode.Executing
            ? db.ExecuteRawAsync(
                "UPDATE {schema}.runtimes SET status_code = @p_status, leased_by_worker_id = @p_worker, lease_expires_at_utc = @p_expires "
                    + "WHERE job_id = @p_id",
                ct,
                ("@p_status", statusCode),
                ("@p_worker", StagedWorkerId),
                ("@p_expires", DateTime.UtcNow.AddMinutes(5)),
                ("@p_id", jobId)
            )
            : db.ExecuteRawAsync(
                "UPDATE {schema}.runtimes SET status_code = @p_status, leased_by_worker_id = NULL, lease_expires_at_utc = NULL "
                    + "WHERE job_id = @p_id",
                ct,
                ("@p_status", statusCode),
                ("@p_id", jobId)
            );

    public static Task SetStatusAsync(IDbSession db, long jobId, JobStatusCode status, CancellationToken ct) =>
        SetStatusAsync(db, jobId, (byte)status, ct);
}
