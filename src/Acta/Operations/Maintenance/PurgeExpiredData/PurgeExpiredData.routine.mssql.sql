-- One section, one bounded atomic batch. Section numbers match RetentionSection.
CREATE OR ALTER PROCEDURE {{schema}}.purge_expired_data
    @p_namespace_id INT,
    @p_section INT,
    @p_cutoff_utc DATETIME2(7),
    @p_batch_size INT,
    @p_before_job_id BIGINT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        IF @p_batch_size <= 0 OR @p_section NOT BETWEEN 1 AND 8
            THROW 50002, 'Invalid retention section or batch size.', 1;

        DECLARE @rows INT = 0;
        DECLARE @next_job_id BIGINT = 0;
        DECLARE @del TABLE (id BIGINT NOT NULL);
        DECLARE @lock_del TABLE (lock_key VARCHAR(256) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL);
        DECLARE @alert_del TABLE (id BIGINT NOT NULL PRIMARY KEY, deduplication_key VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL);
        DECLARE @alert_locked TABLE (id BIGINT NOT NULL PRIMARY KEY);

        IF @p_section = 1
            BEGIN
                DELETE @del;

                /* No retention index: walk runtimes downward from the cursor, ten batches a slice, and delete this
                   namespace's expired jobs in it; downward, so a walk has a fixed end however fast enqueues arrive.
                   A full batch ends the slice at its last delete; the oldest row answers zero, and the walk restarts. */
                DECLARE @below BIGINT = CASE WHEN @p_before_job_id = 0 THEN 9223372036854775807 ELSE @p_before_job_id END;
                DECLARE @slice_end BIGINT = (
                    SELECT MIN(s.job_id)
                    FROM (
                        SELECT TOP (@p_batch_size * 10) r.job_id
                        FROM {{schema}}.runtimes r
                        WHERE r.job_id < @below
                        ORDER BY r.job_id DESC
                    ) s
                );

                -- A locking read seeks (docs/sql-execution-policy.md, "Locking reads seek").
                INSERT INTO @del (id)
                SELECT TOP (@p_batch_size) j.id
                FROM {{schema}}.runtimes r WITH (UPDLOCK, READPAST, FORCESEEK)
                INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, READPAST, FORCESEEK) ON j.id = r.job_id
                WHERE
                    r.job_id < @below
                    AND r.job_id >= @slice_end
                    AND r.namespace_id = @p_namespace_id
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
                    -- A completed child of a live parent is kept: the parent's replay dedupes onto its row
                    -- and reads its result, so purging it would run the child again. The tree drains once
                    -- the parent is terminal.
                    AND NOT EXISTS (
                        SELECT 1 FROM {{schema}}.runtimes p
                        WHERE p.job_id = j.parent_id
                            AND p.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    )
                    -- A stopped recurring job stays while its definition is live: the next start would register
                    -- its declaration as a new job and undo the cancel or failure. Retiring the definition lets
                    -- it drain.
                    AND NOT EXISTS (
                        SELECT 1 FROM {{schema}}.schedules s
                        INNER JOIN {{schema}}.definitions d ON d.id = j.definition_id
                        WHERE s.job_id = j.id AND d.status_code <> 240 /* DefinitionStatusCode.Retired */
                    )
                ORDER BY r.job_id DESC;

                SET @next_job_id = CASE
                    WHEN @slice_end IS NULL THEN 0
                    WHEN (SELECT COUNT(*) FROM @del) = @p_batch_size THEN (SELECT MIN(id) FROM @del)
                    ELSE @slice_end
                END;

                DELETE FROM {{schema}}.tags
                WHERE scope_code = 50 /* TagScopeCode.Job */ AND scope_id IN (SELECT id FROM @del);

                -- Each delete below seeks its rows by job id, the jobs cascade included: an unhinted plan can scan
                -- the whole child table under update locks
                -- (docs/sql-execution-policy.md, "Foreign keys in set-based writes").
                DELETE c FROM {{schema}}.checkpoints c WITH (FORCESEEK) INNER JOIN @del d ON d.id = c.job_id;
                DELETE r FROM {{schema}}.runtimes r WITH (FORCESEEK) INNER JOIN @del d ON d.id = r.job_id;
                DELETE s FROM {{schema}}.steps s WITH (FORCESEEK) INNER JOIN @del d ON d.id = s.job_id;
                DELETE x FROM {{schema}}.results x WITH (FORCESEEK) INNER JOIN @del d ON d.id = x.job_id;
                DELETE j FROM {{schema}}.jobs j WITH (FORCESEEK) INNER JOIN @del d ON d.id = j.id
                OPTION (LOOP JOIN);
                SET @rows = (SELECT COUNT(*) FROM @del);

            END;

        ELSE IF @p_section = 2
            BEGIN
                DELETE @del;

                INSERT INTO @del (id)
                SELECT TOP (@p_batch_size) id
                FROM {{schema}}.events WITH (UPDLOCK, READPAST, FORCESEEK (ix_events_namespace_created (namespace_id)))
                WHERE
                    namespace_id = @p_namespace_id
                    AND created_at_utc <= @p_cutoff_utc
                ORDER BY created_at_utc, id;
                DELETE e FROM {{schema}}.events e WITH (FORCESEEK) INNER JOIN @del d ON d.id = e.id;
                SET @rows = (SELECT COUNT(*) FROM @del);

            END;

        ELSE IF @p_section = 3
            BEGIN
                DELETE @alert_del;
                DELETE @alert_locked;

                INSERT INTO @alert_del (id, deduplication_key)
                SELECT TOP (@p_batch_size) id, deduplication_key
                FROM {{schema}}.alerts WITH (READPAST)
                WHERE
                    namespace_id = @p_namespace_id
                    AND created_at_utc <= @p_cutoff_utc
                    AND delivery_status_code IN (
                        30 /* AlertDeliveryStatusCode.Suppressed */,
                        100 /* AlertDeliveryStatusCode.Delivered */,
                        200 /* AlertDeliveryStatusCode.Failed */
                    )
                ORDER BY created_at_utc, id;

                -- Staged without locks, then each identity row is locked through its ix_alerts_deduplication_key_identity
                -- key before the row and the rest by key, skipping a row another transaction holds
                -- (docs/sql-execution-policy.md, "Alert lock order").
                INSERT INTO @alert_locked (id)
                SELECT a.id
                FROM @alert_del d
                INNER JOIN {{schema}}.alerts a WITH (UPDLOCK, READPAST, FORCESEEK (ix_alerts_deduplication_key_identity (namespace_id, deduplication_key)))
                    ON
                        a.namespace_id = @p_namespace_id
                        AND a.deduplication_key = d.deduplication_key
                        AND a.id = d.id
                WHERE d.deduplication_key IS NOT NULL;
                INSERT INTO @alert_locked (id)
                SELECT a.id
                FROM @alert_del d
                INNER JOIN {{schema}}.alerts a WITH (UPDLOCK, READPAST, FORCESEEK) ON a.id = d.id
                WHERE d.deduplication_key IS NULL;

                DELETE a
                FROM {{schema}}.alerts a WITH (FORCESEEK)
                INNER JOIN @alert_locked l ON l.id = a.id
                WHERE
                    a.created_at_utc <= @p_cutoff_utc
                    AND a.delivery_status_code IN (
                        30 /* AlertDeliveryStatusCode.Suppressed */,
                        100 /* AlertDeliveryStatusCode.Delivered */,
                        200 /* AlertDeliveryStatusCode.Failed */
                    );
                SET @rows = @@ROWCOUNT;

            END;

        ELSE IF @p_section = 4
            BEGIN
                DELETE @alert_del;
                DELETE @alert_locked;

                INSERT INTO @alert_del (id, deduplication_key)
                SELECT TOP (@p_batch_size) id, deduplication_key
                FROM {{schema}}.alerts WITH (READPAST)
                WHERE
                    namespace_id = @p_namespace_id
                    AND created_at_utc <= @p_cutoff_utc
                    AND delivery_status_code IN (
                        10 /* AlertDeliveryStatusCode.Pending */,
                        20 /* AlertDeliveryStatusCode.RetryAfter */
                    )
                ORDER BY created_at_utc, id;

                -- Staged without locks, then each identity row is locked through its ix_alerts_deduplication_key_identity
                -- key before the row and the rest by key, skipping a row another transaction holds
                -- (docs/sql-execution-policy.md, "Alert lock order").
                INSERT INTO @alert_locked (id)
                SELECT a.id
                FROM @alert_del d
                INNER JOIN {{schema}}.alerts a WITH (UPDLOCK, READPAST, FORCESEEK (ix_alerts_deduplication_key_identity (namespace_id, deduplication_key)))
                    ON
                        a.namespace_id = @p_namespace_id
                        AND a.deduplication_key = d.deduplication_key
                        AND a.id = d.id
                WHERE d.deduplication_key IS NOT NULL;
                INSERT INTO @alert_locked (id)
                SELECT a.id
                FROM @alert_del d
                INNER JOIN {{schema}}.alerts a WITH (UPDLOCK, READPAST, FORCESEEK) ON a.id = d.id
                WHERE d.deduplication_key IS NULL;

                DELETE a
                FROM {{schema}}.alerts a WITH (FORCESEEK)
                INNER JOIN @alert_locked l ON l.id = a.id
                WHERE
                    a.created_at_utc <= @p_cutoff_utc
                    AND a.delivery_status_code IN (
                        10 /* AlertDeliveryStatusCode.Pending */,
                        20 /* AlertDeliveryStatusCode.RetryAfter */
                    );
                SET @rows = @@ROWCOUNT;

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
                    AND kind_code = 10 /* CheckpointKindCode.Variable */
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
                DELETE w FROM {{schema}}.workers w WITH (FORCESEEK) INNER JOIN @del d ON d.id = w.id;
                SET @rows = (SELECT COUNT(*) FROM @del);

            END;

        ELSE IF @p_section = 7
            BEGIN
            -- The batch is staged without holding locks, then the delete seeks each row by clustered key, first
            -- as every lock writer does, skipping a row a writer holds and re-checking its expiry. Taking the
            -- expiry index key first deadlocks against reserve_rate moving a bucket's expiry.
                DELETE @lock_del;
                INSERT INTO @lock_del (lock_key)
                SELECT TOP (@p_batch_size) lock_key
                FROM {{schema}}.locks WITH (READPAST)
                WHERE
                    expires_at_utc <= @p_cutoff_utc
                ORDER BY expires_at_utc;
                DELETE t
                FROM {{schema}}.locks t WITH (READPAST, FORCESEEK (pk_locks (lock_key)))
                INNER JOIN @lock_del d ON d.lock_key = t.lock_key
                WHERE t.expires_at_utc <= @p_cutoff_utc;
                SET @rows = @@ROWCOUNT;
            END;

        ELSE IF @p_section = 8
            BEGIN
            -- Unreferenced lanes, locked by id on the clustered key skipping a lane an enqueue or settle
            -- holds, then re-checked under the lock (docs/sql-execution-policy.md, "Lane lock order").
                DELETE @del;
                INSERT INTO @del (id)
                SELECT l.id
                FROM {{schema}}.lanes l WITH (UPDLOCK, READPAST, ROWLOCK, INDEX (pk_lanes))
                WHERE l.id IN (
                    SELECT TOP (@p_batch_size) c.id
                    FROM {{schema}}.lanes c
                    WHERE
                        c.namespace_id = @p_namespace_id
                        AND NOT EXISTS (SELECT 1 FROM {{schema}}.runtimes r WHERE r.lane_id = c.id)
                    ORDER BY c.id
                );
                DELETE l
                FROM {{schema}}.lanes l WITH (FORCESEEK)
                INNER JOIN @del d ON d.id = l.id
                WHERE NOT EXISTS (SELECT 1 FROM {{schema}}.runtimes r WHERE r.lane_id = l.id)
                OPTION (LOOP JOIN);
                SET @rows = @@ROWCOUNT;
            END;

        SELECT @rows AS deleted_count, @next_job_id AS next_job_id;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
