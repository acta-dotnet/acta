SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @marked TABLE (worker_id INT NOT NULL PRIMARY KEY, namespace_id INT NOT NULL);

    UPDATE {{schema}}.workers WITH (ROWLOCK, READPAST)
    SET
        worker_status_code = 200 /* WorkerStatusCode.Dead */,
        modified_at_utc = SYSUTCDATETIME(),
        version = version + 1
    OUTPUT INSERTED.worker_id, INSERTED.namespace_id INTO @marked (worker_id, namespace_id)
    WHERE
        -- A worker that stopped heartbeating mid-drain, killed or past its host's shutdown budget, is as
        -- dead as an active one; nothing else ever moves a Draining row.
        worker_status_code IN (10 /* WorkerStatusCode.Active */, 80 /* WorkerStatusCode.Draining */)
        AND last_seen_at_utc < DATEADD(SECOND, -@p_dead_after_seconds, SYSUTCDATETIME());

    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        worker_id,
        entity_code,
        entity_id)
    SELECT
        122 /* EventCode.WorkerDied */,
        SYSUTCDATETIME(),
        m.namespace_id,
        m.worker_id,
        130 /* EntityCode.Worker */,
        m.worker_id
    FROM @marked m;

    SELECT COUNT(*) AS marked FROM @marked;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
