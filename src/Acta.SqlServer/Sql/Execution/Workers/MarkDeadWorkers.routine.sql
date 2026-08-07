CREATE OR ALTER PROCEDURE {{schema}}.mark_dead_workers
    @p_dead_after_seconds INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @marked TABLE (id INT NOT NULL PRIMARY KEY, namespace_id SMALLINT NOT NULL);

    UPDATE {{schema}}.workers WITH (ROWLOCK, READPAST)
    SET
        status_code = 200 /* WorkerStatusCode.Dead */,
        modified_at_utc = SYSUTCDATETIME(),
        version = version + 1
    OUTPUT INSERTED.id, INSERTED.namespace_id INTO @marked (id, namespace_id)
    WHERE
        status_code = 10 /* WorkerStatusCode.Active */
        AND last_seen_at_utc < DATEADD(SECOND, -@p_dead_after_seconds, SYSUTCDATETIME());

    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, execution_number,
        lineage_root_id, definition_id, worker_id, from_status_code, to_status_code,
        execution_status_code, duration_ms, reason_code, reason_message
    )
    SELECT
        122 /* JobEventCode.WorkerDead */,
        SYSUTCDATETIME(),
        m.namespace_id,
        70 /* JobActorCode.Worker */,
        CAST(m.id AS VARCHAR(128)),
        NULL,
        NULL,
        NULL,
        NULL,
        m.id,
        NULL,
        NULL,
        NULL,
        NULL,
        101 /* JobEventReasonCode.WorkerHeartbeatStale */,
        NULL
    FROM @marked m;

    SELECT COUNT(*) FROM @marked;
END;
GO
