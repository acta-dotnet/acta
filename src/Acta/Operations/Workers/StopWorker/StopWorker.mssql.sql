SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @stopped TABLE (id INT NOT NULL PRIMARY KEY);

    UPDATE {{schema}}.workers
    SET
        status_code = 100 /* WorkerStatusCode.Stopped */,
        modified_at_utc = @now,
        version = version + 1
    OUTPUT INSERTED.id INTO @stopped (id)
    WHERE
        id = @p_worker_id
        AND status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */);

    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id, actor_code, actor, job_id, execution_number,
        lineage_root_id, definition_id, worker_id, from_status_code, to_status_code,
        execution_status_code, duration_ms, reason_code, reason_message
    )
    SELECT
        121 /* EventCode.WorkerStopped */,
        @now,
        @p_namespace_id,
        70 /* ActorCode.Worker */,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        s.id,
        NULL,
        NULL,
        NULL,
        NULL,
        100 /* EventReasonCode.WorkerCleanShutdown */,
        NULL
    FROM @stopped s;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
