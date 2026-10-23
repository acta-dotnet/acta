namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Raw staging of a job's durable <c>jobs_state</c> row for specs that need a status the public API cannot
/// reach directly (an in-flight job, a terminal one). Writes the lease alongside the status the way a
/// claim does, which <c>ck_jobs_state_status_lease</c> and <c>ck_jobs_state_inflight_leased</c> require:
/// exactly the in-flight statuses carry a lease.
/// </summary>
internal static class JobStateStaging
{
    // No worker row ever gets this id, so no live worker's lease renewal reaches a staged row and pushes its
    // lease past a reclaim the spec is waiting for. Nothing references workers by key, so no row is needed.
    public const int StagedWorkerId = int.MaxValue;

    public static Task SetStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        statusCode is (byte)JobStatusCode.Executing
            ? db.ExecuteRawAsync(
                "UPDATE {schema}.jobs_state SET job_status_code = @p_status, leased_by_worker_id = @p_worker, lease_expires_at_utc = @p_expires "
                    + "WHERE job_id = @p_id",
                ct,
                ("@p_status", statusCode),
                ("@p_worker", StagedWorkerId),
                ("@p_expires", DateTime.UtcNow.AddMinutes(5)),
                ("@p_id", jobId)
            )
            : db.ExecuteRawAsync(
                "UPDATE {schema}.jobs_state SET job_status_code = @p_status, leased_by_worker_id = NULL, lease_expires_at_utc = NULL "
                    + "WHERE job_id = @p_id",
                ct,
                ("@p_status", statusCode),
                ("@p_id", jobId)
            );

    public static Task SetStatusAsync(IDbSession db, long jobId, JobStatusCode status, CancellationToken ct) =>
        SetStatusAsync(db, jobId, (byte)status, ct);

    /// <summary>
    /// Applies <paramref name="set"/> to the jobs_state rows of <paramref name="jobIds"/>, a hundred per statement and
    /// by key. A predicate on anything else scans the shared table under update locks on SQL Server, waits behind
    /// rows the specs running beside this one hold, and exhausts the connection pool while it waits.
    /// </summary>
    public static async Task UpdateAsync(
        IDbSession db,
        IEnumerable<long> jobIds,
        string set,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters
    )
    {
        foreach (var chunk in jobIds.Chunk(100))
        {
            var ids = chunk.Select((id, i) => ($"@p_id{i}", (object?)id)).ToArray();
            await db.ExecuteRawAsync(
                $"UPDATE {{schema}}.jobs_state SET {set} WHERE job_id IN ({string.Join(", ", ids.Select(p => p.Item1))})",
                ct,
                [.. parameters, .. ids]
            );
        }
    }
}
