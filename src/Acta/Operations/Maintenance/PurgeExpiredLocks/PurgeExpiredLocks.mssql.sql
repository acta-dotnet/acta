SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @lock_del TABLE (lock_key VARCHAR(256) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL);
    DECLARE @rows INT;

    -- The batch is staged without holding locks, then the delete seeks each row by clustered key, first
    -- as every lock writer does, skipping a row a writer holds and re-checking its expiry. Taking the
    -- expiry index key first deadlocks against reserve_rate moving a bucket's expiry.
    INSERT INTO @lock_del (lock_key)
    SELECT TOP (@p_batch_size) lock_key
    FROM {{schema}}.locks WITH (READPAST)
    WHERE expires_at_utc <= @p_cutoff_utc
    ORDER BY expires_at_utc;
    DELETE t
    FROM {{schema}}.locks t WITH (READPAST, FORCESEEK (pk_locks (lock_key)))
    INNER JOIN @lock_del d ON d.lock_key = t.lock_key
    WHERE t.expires_at_utc <= @p_cutoff_utc;
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
