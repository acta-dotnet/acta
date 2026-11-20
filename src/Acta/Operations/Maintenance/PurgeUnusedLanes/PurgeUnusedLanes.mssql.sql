SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @lane_del TABLE (lane_id BIGINT NOT NULL);
    DECLARE @rows INT;

    -- Unreferenced lanes, locked by id on the clustered key skipping a lane an enqueue or settle
    -- holds, then re-checked under the lock (docs/sql-internals.md, "Lane lock order").
    INSERT INTO @lane_del (lane_id)
    SELECT l.lane_id
    FROM {{schema}}.lanes l WITH (UPDLOCK, READPAST, ROWLOCK, INDEX (pk_lanes))
    WHERE l.lane_id IN (
        SELECT TOP (@p_batch_size) c.lane_id
        FROM {{schema}}.lanes c WITH (READCOMMITTEDLOCK, READPAST)
        WHERE
            c.namespace_id = @p_namespace_id
            AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs_state r WITH (READCOMMITTEDLOCK, READPAST) WHERE r.lane_id = c.lane_id)
        ORDER BY c.lane_id
    );
    DELETE l
    FROM {{schema}}.lanes l WITH (FORCESEEK)
    INNER JOIN @lane_del d ON d.lane_id = l.lane_id
    WHERE NOT EXISTS (SELECT 1 FROM {{schema}}.jobs_state r WHERE r.lane_id = l.lane_id)
    OPTION (LOOP JOIN);
    SET @rows = @@ROWCOUNT;

    SELECT @rows AS deleted_count;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
