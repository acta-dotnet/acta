-- One section, one bounded atomic batch. Section numbers match RetentionSection.
CREATE OR ALTER PROCEDURE {{schema}}.purge_expired_data
    @p_namespace_id INT,
    @p_section INT,
    @p_cutoff_utc DATETIME2(7),
    @p_batch_size INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        IF @p_batch_size <= 0 OR @p_section NOT BETWEEN 1 AND 7
            THROW 50002, 'Invalid retention section or batch size.', 1;

        DECLARE @rows INT = 0;
        DECLARE @del TABLE (id BIGINT NOT NULL);
        DECLARE @schedule_del TABLE (id BIGINT NOT NULL);
        DECLARE @lock_del TABLE (lock_key VARCHAR(256) NOT NULL);

        IF @p_section = 1
            BEGIN
                DELETE @del;

                INSERT INTO @del (id)
                SELECT TOP (@p_batch_size) j.id
                FROM {{schema}}.runtimes r WITH (UPDLOCK, READPAST)
                INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, READPAST) ON j.id = r.job_id
                WHERE
                    r.namespace_id = @p_namespace_id
                    AND r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    AND r.retention_until_utc IS NOT NULL
                    AND r.retention_until_utc <= @p_cutoff_utc
                    -- Lineage guard: parent_id carries no FK, so purging a parent whose children still
                    -- exist would orphan their lineage (same rule as the manual purge_job). Only leaves
                    -- delete; a fully-expired subtree drains bottom-up across iterations.
                    AND NOT EXISTS (
                        SELECT 1 FROM {{schema}}.jobs c
                        WHERE c.parent_id = j.id
                    )
                ORDER BY r.retention_until_utc, r.job_id;

                DELETE @schedule_del;
                INSERT INTO @schedule_del (id)
                SELECT s.id FROM {{schema}}.schedules s WITH (UPDLOCK)
                WHERE s.job_id IN (SELECT id FROM @del);

                DELETE FROM {{schema}}.tags
                WHERE
                    (scope_code = 50 /* TagScopeCode.Job */ AND scope_id IN (SELECT id FROM @del))
                    OR (scope_code = 60 /* TagScopeCode.Schedule */ AND scope_id IN (SELECT id FROM @schedule_del));

                DELETE FROM {{schema}}.checkpoints
                WHERE job_id IN (SELECT id FROM @del);
                DELETE FROM {{schema}}.runtimes
                WHERE job_id IN (SELECT id FROM @del);
                DELETE FROM {{schema}}.steps
                WHERE job_id IN (SELECT id FROM @del);
                DELETE FROM {{schema}}.results
                WHERE job_id IN (SELECT id FROM @del);
                DELETE FROM {{schema}}.jobs
                WHERE id IN (SELECT id FROM @del);
                SET @rows = (SELECT COUNT(*) FROM @del);

            END;

        ELSE IF @p_section = 2
            BEGIN
                DELETE @del;

                INSERT INTO @del (id)
                SELECT TOP (@p_batch_size) id
                FROM {{schema}}.events WITH (UPDLOCK, READPAST)
                WHERE
                    namespace_id = @p_namespace_id
                    AND created_at_utc <= @p_cutoff_utc
                ORDER BY created_at_utc, id;
                DELETE FROM {{schema}}.tags
                WHERE scope_code = 90 /* TagScopeCode.Event */ AND scope_id IN (SELECT id FROM @del);
                DELETE e FROM {{schema}}.events e INNER JOIN @del d ON d.id = e.id;
                SET @rows = (SELECT COUNT(*) FROM @del);

            END;

        ELSE IF @p_section = 3
            BEGIN
                DELETE @del;

                INSERT INTO @del (id)
                SELECT TOP (@p_batch_size) id
                FROM {{schema}}.alerts WITH (UPDLOCK, READPAST)
                WHERE
                    namespace_id = @p_namespace_id
                    AND created_at_utc <= @p_cutoff_utc
                    AND delivery_status_code IN (
                        30 /* AlertDeliveryStatusCode.Suppressed */,
                        100 /* AlertDeliveryStatusCode.Delivered */,
                        200 /* AlertDeliveryStatusCode.Failed */
                    )
                ORDER BY created_at_utc, id;
                DELETE FROM {{schema}}.tags
                WHERE scope_code = 80 /* TagScopeCode.Alert */ AND scope_id IN (SELECT id FROM @del);
                DELETE a FROM {{schema}}.alerts a INNER JOIN @del d ON d.id = a.id;
                SET @rows = (SELECT COUNT(*) FROM @del);

            END;

        ELSE IF @p_section = 4
            BEGIN
                DELETE @del;

                INSERT INTO @del (id)
                SELECT TOP (@p_batch_size) id
                FROM {{schema}}.alerts WITH (UPDLOCK, READPAST)
                WHERE
                    namespace_id = @p_namespace_id
                    AND created_at_utc <= @p_cutoff_utc
                    AND delivery_status_code IN (
                        10 /* AlertDeliveryStatusCode.Pending */,
                        20 /* AlertDeliveryStatusCode.RetryAfter */
                    )
                ORDER BY created_at_utc, id;
                DELETE FROM {{schema}}.tags
                WHERE scope_code = 80 /* TagScopeCode.Alert */ AND scope_id IN (SELECT id FROM @del);
                DELETE a FROM {{schema}}.alerts a INNER JOIN @del d ON d.id = a.id;
                SET @rows = (SELECT COUNT(*) FROM @del);

            END;

        ELSE IF @p_section = 5
            BEGIN
                DECLARE @alerts_slot_id BIGINT = (
                    SELECT j.id
                    FROM {{schema}}.jobs j
                    WHERE
                        j.namespace_id = @p_namespace_id
                        AND j.deduplication_key = 'sys.alerts'
                        AND j.parent_id IS NULL);
                DELETE TOP (@p_batch_size) FROM {{schema}}.checkpoints
                WHERE
                    job_id = @alerts_slot_id
                    AND kind_code = 10 /* JobCheckpointKindCode.Variable */
                    AND name LIKE 'alerts-skip-%'
                    AND modified_at_utc <= @p_cutoff_utc;
                SET @rows = @@ROWCOUNT;
            END;

        ELSE IF @p_section = 6
            BEGIN
                DELETE @del;

                INSERT INTO @del (id)
                SELECT TOP (@p_batch_size) id
                FROM {{schema}}.workers WITH (UPDLOCK, READPAST)
                WHERE
                    namespace_id = @p_namespace_id
                    AND status_code IN (100 /* WorkerStatusCode.Stopped */, 200 /* WorkerStatusCode.Dead */)
                    AND last_seen_at_utc <= @p_cutoff_utc
                ORDER BY last_seen_at_utc, id;
                DELETE FROM {{schema}}.tags
                WHERE scope_code = 70 /* TagScopeCode.Worker */ AND scope_id IN (SELECT id FROM @del);
                DELETE w FROM {{schema}}.workers w INNER JOIN @del d ON d.id = w.id;
                SET @rows = (SELECT COUNT(*) FROM @del);

            END;

        ELSE IF @p_section = 7
            BEGIN
            -- Stage the batch first (same shape as the sections above), so the READPAST probe runs
            -- exactly once per iteration and the delete stays within the batch size.
                DELETE @lock_del;
                INSERT INTO @lock_del (lock_key)
                SELECT TOP (@p_batch_size) lock_key
                FROM {{schema}}.locks WITH (UPDLOCK, READPAST)
                WHERE
                    expires_at_utc <= @p_cutoff_utc
                ORDER BY expires_at_utc;
                DELETE t FROM {{schema}}.locks t INNER JOIN @lock_del d ON d.lock_key = t.lock_key;
                SET @rows = @@ROWCOUNT;
            END;

        SELECT @rows AS deleted_count;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
