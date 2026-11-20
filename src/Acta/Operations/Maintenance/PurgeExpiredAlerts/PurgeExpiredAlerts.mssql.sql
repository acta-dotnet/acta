SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @alert_del TABLE (alert_id BIGINT NOT NULL PRIMARY KEY, deduplication_key VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL);
    DECLARE @alert_locked TABLE (alert_id BIGINT NOT NULL PRIMARY KEY);
    DECLARE @alert_deleted TABLE (delivery_status_code TINYINT NOT NULL);

    -- Past the window an alert goes whether its delivery settled or not, so a stuck row is not immortal, and an open
    -- incident goes too, which frees its identity for the next failure.
    INSERT INTO @alert_del (alert_id, deduplication_key)
    SELECT TOP (@p_batch_size) alert_id, deduplication_key
    FROM {{schema}}.alerts WITH (READPAST)
    WHERE
        namespace_id = @p_namespace_id
        AND created_at_utc <= @p_cutoff_utc
    ORDER BY created_at_utc, alert_id;

    -- Staged without locks, then each identity row is locked through its ix_alerts_deduplication_key_identity
    -- key before the row and the rest by key, skipping a row another transaction holds
    -- (docs/sql-internals.md, "Alert lock order").
    INSERT INTO @alert_locked (alert_id)
    SELECT a.alert_id
    FROM @alert_del d
    INNER JOIN {{schema}}.alerts a WITH (UPDLOCK, READPAST, FORCESEEK (ix_alerts_deduplication_key_identity (namespace_id, deduplication_key)))
        ON
            a.namespace_id = @p_namespace_id
            AND a.deduplication_key = d.deduplication_key
            AND a.alert_id = d.alert_id
    WHERE d.deduplication_key IS NOT NULL;
    INSERT INTO @alert_locked (alert_id)
    SELECT a.alert_id
    FROM @alert_del d
    INNER JOIN {{schema}}.alerts a WITH (UPDLOCK, READPAST, FORCESEEK) ON a.alert_id = d.alert_id
    WHERE d.deduplication_key IS NULL;

    -- The count of undelivered rows is read from the rows deleted, which hold the status they were deleted with.
    DELETE a
    OUTPUT DELETED.delivery_status_code INTO @alert_deleted (delivery_status_code)
    FROM {{schema}}.alerts a WITH (FORCESEEK (pk_alerts (alert_id)))
    INNER JOIN @alert_locked l ON l.alert_id = a.alert_id
    WHERE a.created_at_utc <= @p_cutoff_utc;

    SELECT
        COUNT(*) AS deleted_count,
        COUNT(CASE WHEN delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.Retrying */) THEN 1 END)
            AS undelivered_count
    FROM @alert_deleted;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
