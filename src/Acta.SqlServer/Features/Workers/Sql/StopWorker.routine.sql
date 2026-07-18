CREATE OR ALTER PROCEDURE {{schema}}.stop_worker
    @p_namespace_id SMALLINT,
    @p_worker_id        INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @stopped TABLE (id INT NOT NULL PRIMARY KEY);

    UPDATE {{schema}}.workers
       SET status_code     = 100 /* WorkerStatusCode.Stopped */,
           modified_at_utc = @now,
           version         = version + 1
    OUTPUT inserted.id INTO @stopped (id)
     WHERE id          = @p_worker_id
       AND status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */);

    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, execution_number,
        lineage_root_id, definition_id, worker_id, from_status_code, to_status_code,
        execution_status_code, duration_ms, reason_code, reason_message)
    SELECT 121 /* JobEventCode.WorkerStopped */, @now, @p_namespace_id, 70 /* JobActorCode.Worker */, CAST(s.id AS VARCHAR(128)),
           NULL, NULL, NULL, NULL, s.id, NULL, NULL, NULL, NULL, 100 /* JobEventReasonCode.WorkerCleanShutdown */, NULL
      FROM @stopped s;
END;
GO
