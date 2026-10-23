SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @stopped TABLE (worker_id INT NOT NULL PRIMARY KEY);

    UPDATE {{schema}}.workers
    SET
        worker_status_code = 100 /* WorkerStatusCode.Stopped */,
        modified_at_utc = @now,
        version = version + 1
    OUTPUT INSERTED.worker_id INTO @stopped (worker_id)
    WHERE
        worker_id = @p_worker_id
        AND worker_status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */);

    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        worker_id,
        entity_code,
        entity_id)
    SELECT
        121 /* EventCode.WorkerStopped */,
        @now,
        @p_namespace_id,
        s.worker_id,
        130 /* EntityCode.Worker */,
        s.worker_id
    FROM @stopped s;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
