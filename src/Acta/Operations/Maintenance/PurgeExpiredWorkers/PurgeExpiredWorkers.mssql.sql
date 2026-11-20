SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @worker_del TABLE (worker_id BIGINT NOT NULL);

    -- Events about a terminal worker land after its last heartbeat, and each one stamps modified_at_utc, so
    -- the row goes once both are past the window and no Executing job it leases is left for recovery.
    INSERT INTO @worker_del (worker_id)
    SELECT TOP (@p_batch_size) w.worker_id
    FROM {{schema}}.workers w WITH (UPDLOCK, READPAST)
    WHERE
        w.namespace_id = @p_namespace_id
        AND w.worker_status_code IN (100 /* WorkerStatusCode.Stopped */, 200 /* WorkerStatusCode.Dead */)
        AND w.last_seen_at_utc <= @p_cutoff_utc
        AND w.modified_at_utc <= @p_cutoff_utc
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.jobs_state r
            WHERE
                r.leased_by_worker_id = w.worker_id
                -- ix_jobs_state_worker_inflight's filter restated: a filtered index serves only a query that states it.
                AND r.leased_by_worker_id IS NOT NULL
                AND r.job_status_code = 50 /* JobStatusCode.Executing */
        )
    ORDER BY w.last_seen_at_utc, w.worker_id;
    DELETE w FROM {{schema}}.workers w WITH (FORCESEEK) INNER JOIN @worker_del d ON d.worker_id = w.worker_id;

    SELECT (SELECT COUNT(*) FROM @worker_del) AS deleted_count;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
