SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    -- The index keys are DATETIME2(3), and a forced seek on them compiles only against values of that type.
    DECLARE @cutoff_utc DATETIME2(3) = @p_cutoff_utc;
    DECLARE @after_utc DATETIME2(3) = @p_after_created_utc;
    DECLARE @event_del TABLE (
        worker_id INT NOT NULL,
        event_id BIGINT NOT NULL,
        created_at_utc DATETIME2(3) NOT NULL,
        PRIMARY KEY (worker_id, event_id)
    );

    -- A batch reads on from the position its pass gives, so no batch reads the index entries an earlier one deleted, and
    -- collects by the clustered key, so each delete is a direct seek on pk_events.
    INSERT INTO @event_del (worker_id, event_id, created_at_utc)
    SELECT TOP (@p_batch_size) e.worker_id, e.event_id, e.created_at_utc
    FROM {{schema}}.events e WITH (UPDLOCK, READPAST, FORCESEEK (ix_events_namespace_created (namespace_id, created_at_utc)))
    WHERE
        e.namespace_id = @p_namespace_id
        AND e.created_at_utc < @cutoff_utc
        AND e.created_at_utc >= @after_utc
        AND (e.created_at_utc > @after_utc OR e.event_id > @p_after_event_id)
    ORDER BY e.created_at_utc, e.event_id;
    DELETE e FROM {{schema}}.events e WITH (FORCESEEK) INNER JOIN @event_del d ON d.worker_id = e.worker_id AND d.event_id = e.event_id;

    SELECT
        (SELECT COUNT(*) FROM @event_del) AS deleted_count,
        COALESCE(l.created_at_utc, @after_utc) AS last_created_utc,
        COALESCE(l.event_id, @p_after_event_id) AS last_event_id
    FROM (SELECT 1 AS one) o
    LEFT JOIN (SELECT TOP (1) d.created_at_utc, d.event_id FROM @event_del d ORDER BY d.created_at_utc DESC, d.event_id DESC) l ON 1 = 1;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
