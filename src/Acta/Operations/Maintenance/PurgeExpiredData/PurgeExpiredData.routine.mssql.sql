/* One section, one bounded atomic batch; section numbers match RetentionSection. Alert-required events above the
   namespace floor stay with their jobs (PurgeExpiredDataCommand). */
CREATE OR ALTER PROCEDURE {{schema}}.purge_expired_data
    @p_namespace_id INT,
    @p_section INT,
    @p_cutoff_utc DATETIME2(7),
    @p_batch_size INT,
    @p_before_created_utc DATETIME2(7) = NULL,
    @p_before_job_id BIGINT = 0,
    @p_alert_floor_utc DATETIME2(7) = NULL,
    @p_alert_floor_event_id BIGINT = NULL
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
        DECLARE @next_created_utc DATETIME2(3) = NULL;
        DECLARE @next_job_id BIGINT = 0;
        DECLARE @job_del TABLE (job_id BIGINT NOT NULL, created_at_utc DATETIME2(3) NOT NULL);
        DECLARE @event_del TABLE (worker_id INT NOT NULL, event_id BIGINT NOT NULL, PRIMARY KEY (worker_id, event_id));
        DECLARE @lock_del TABLE (lock_key VARCHAR(256) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL);
        DECLARE @alert_del TABLE (alert_id BIGINT NOT NULL PRIMARY KEY, deduplication_key VARCHAR(128) COLLATE Latin1_General_100_BIN2_UTF8 NULL);
        DECLARE @alert_locked TABLE (alert_id BIGINT NOT NULL PRIMARY KEY);
        DECLARE @worker_del TABLE (worker_id BIGINT NOT NULL);
        DECLARE @lane_del TABLE (lane_id BIGINT NOT NULL);
        DECLARE @floor_utc DATETIME2(7) = COALESCE(@p_alert_floor_utc, @p_cutoff_utc);
        DECLARE @floor_id BIGINT = COALESCE(@p_alert_floor_event_id, 9223372036854775807);
        IF @p_section = 1
            BEGIN
                DELETE @job_del;

                /* No retention index: walk this namespace's jobs from newer to older through ix_jobs_namespace_created, ten
                   batches a slice, and delete the expired ones in it. A cycle ends at the namespace's oldest job, which answers
                   no cursor; a full batch ends the slice at its last delete, so the walk moves on past what it deleted. */
                DECLARE @below DATETIME2(3) = @p_before_created_utc;
                DECLARE @below_id BIGINT = @p_before_job_id;
                IF @below IS NULL
                    BEGIN
                        /* A cycle starts at the newest job old enough to have expired: none expires sooner than the
                           shortest retention any definition of the namespace has, a retired one included. The framework's own
                           are left out: they run only permanent jobs, and their default would hold a longer-keeping walk to it. */
                        SELECT @below = DATEADD(SECOND, -COALESCE(MIN(d.retention_seconds_effective), 0), @p_cutoff_utc)
                        FROM {{schema}}.definitions d WITH (FORCESEEK (ux_definitions_namespace_name (namespace_id)))
                        WHERE d.namespace_id = @p_namespace_id AND d.definition_name NOT LIKE 'sys.%';
                        SET @below_id = 9223372036854775807;
                    END;

                -- The walk skips a row another transaction holds; the delete below locks and re-checks its own.
                DECLARE @slice_end DATETIME2(3), @slice_end_id BIGINT;
                SELECT TOP (1) @slice_end = s.created_at_utc, @slice_end_id = s.job_id
                FROM (
                    SELECT TOP (@p_batch_size * 10) w.created_at_utc, w.job_id
                    FROM {{schema}}.jobs w WITH (READCOMMITTEDLOCK, READPAST, FORCESEEK (ix_jobs_namespace_created (namespace_id, created_at_utc)))
                    WHERE
                        w.namespace_id = @p_namespace_id
                        AND w.created_at_utc <= @below
                        AND (w.created_at_utc < @below OR w.job_id < @below_id)
                    ORDER BY w.created_at_utc DESC, w.job_id DESC
                ) s
                ORDER BY s.created_at_utc, s.job_id;

                /* A locking read seeks (docs/sql-internals.md, "Locking reads seek"). The walk reads the index without
                   update locks, so a slice of live jobs holds none; a job is locked through its state row, where the
                   status and the deadline are read, and then its own row. */
                INSERT INTO @job_del (job_id, created_at_utc)
                SELECT TOP (@p_batch_size) j.job_id, w.created_at_utc
                FROM {{schema}}.jobs w WITH (READCOMMITTEDLOCK, READPAST, FORCESEEK (ix_jobs_namespace_created (namespace_id, created_at_utc)))
                INNER JOIN {{schema}}.jobs_state r WITH (UPDLOCK, READPAST, FORCESEEK) ON r.job_id = w.job_id
                INNER JOIN {{schema}}.jobs j WITH (UPDLOCK, READPAST, FORCESEEK) ON j.job_id = r.job_id
                WHERE
                    w.namespace_id = @p_namespace_id
                    AND w.created_at_utc <= @below
                    AND (w.created_at_utc < @below OR w.job_id < @below_id)
                    AND w.created_at_utc >= @slice_end
                    AND (w.created_at_utc > @slice_end OR w.job_id >= @slice_end_id)
                    AND r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    AND r.retention_until_utc IS NOT NULL
                    AND r.retention_until_utc <= @p_cutoff_utc
                    -- Lineage guard: parent_job_id carries no FK, so purging a parent whose children still
                    -- exist would orphan their lineage (same rule as the manual purge_job). Only leaves
                    -- delete; a fully-expired subtree drains bottom-up across iterations.
                    AND NOT EXISTS (
                        SELECT 1 FROM {{schema}}.jobs c WITH (FORCESEEK (ix_jobs_parent (parent_job_id)))
                        WHERE c.parent_job_id = j.job_id AND c.parent_job_id IS NOT NULL
                    )
                    -- A completed child of a live parent is kept: the parent's replay dedupes onto its row
                    -- and reads its result, so purging it would run the child again. The tree drains once
                    -- the parent is terminal.
                    AND NOT EXISTS (
                        SELECT 1 FROM {{schema}}.jobs_state p WITH (FORCESEEK)
                        WHERE p.job_id = j.parent_job_id
                            AND p.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    )
                    -- A stopped recurring job stays while its definition is live: the next deploy would register
                    -- its declaration as a new job and undo the cancel or failure. Retiring the definition lets
                    -- it drain.
                    AND NOT EXISTS (
                        SELECT 1 FROM {{schema}}.schedules s WITH (FORCESEEK (ix_schedules_job (job_id)))
                        INNER JOIN {{schema}}.definitions d WITH (FORCESEEK) ON d.definition_id = j.definition_id
                        WHERE
                            s.job_id = j.job_id
                            AND s.job_id IS NOT NULL
                            AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                    )
                    -- A job stays while the projector still owes one of its events, since raising an alert reads the job row.
                    AND NOT EXISTS (
                        SELECT 1
                        FROM {{schema}}.events e WITH (FORCESEEK (ix_events_entity_timeline (entity_code, entity_id)))
                        INNER JOIN {{schema}}.definitions d WITH (FORCESEEK) ON d.definition_id = e.definition_id
                        WHERE
                            e.entity_code = 50 /* EntityCode.Job */
                            AND e.entity_id = j.job_id
                            AND e.created_at_utc >= @floor_utc
                            AND (e.created_at_utc > @floor_utc OR e.event_id > @floor_id)
                            AND (
                                e.event_code IN (41 /* EventCode.JobExecutionFailed */, 42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */)
                                OR (e.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND e.execution_number > 1)
                            )
                            AND d.alert_profile_code_effective <> 0 /* AlertProfileCode.None */
                    )
                    -- A job stays while an alert of it is undelivered: the failure it reports has reached nobody yet.
                    AND NOT EXISTS (
                        SELECT 1 FROM {{schema}}.alerts a WITH (FORCESEEK (ix_alerts_job (job_id)))
                        WHERE
                            a.job_id = j.job_id
                            AND a.job_id IS NOT NULL
                            AND a.delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
                    )
                ORDER BY w.created_at_utc DESC, w.job_id DESC
                OPTION (FORCE ORDER, LOOP JOIN);

                IF (SELECT COUNT(*) FROM @job_del) = @p_batch_size
                    SELECT TOP (1) @next_created_utc = d.created_at_utc, @next_job_id = d.job_id
                    FROM @job_del d
                    ORDER BY d.created_at_utc, d.job_id;
                ELSE IF @slice_end IS NOT NULL
                    SELECT @next_created_utc = @slice_end, @next_job_id = @slice_end_id;

                -- Each delete below seeks its rows by job id: an unhinted plan can scan
                -- the whole child table under update locks
                -- (docs/sql-internals.md, "Foreign keys in set-based writes").
                DELETE t FROM {{schema}}.tags t WITH (FORCESEEK (pk_tags (entity_code, entity_id)))
                INNER JOIN @job_del d ON t.entity_code = 50 /* EntityCode.Job */ AND t.entity_id = d.job_id;

                /* The jobs' settled alerts go with them, so no alert outlives its job. They are read through ix_alerts_job
                   without locks, each identity row is locked before its row (docs/sql-internals.md, "Alert lock order"),
                   and the delete rechecks the job, since a deduplicated raise moves an open alert to its own job. */
                DELETE @alert_del;
                INSERT INTO @alert_del (alert_id, deduplication_key)
                SELECT a.alert_id, a.deduplication_key
                FROM @job_del d
                INNER JOIN {{schema}}.alerts a WITH (FORCESEEK (ix_alerts_job (job_id))) ON a.job_id = d.job_id AND a.job_id IS NOT NULL;
                DECLARE @alert_identities INT = (
                    SELECT COUNT(*)
                    FROM @alert_del d
                    INNER JOIN {{schema}}.alerts a WITH (UPDLOCK, FORCESEEK (ix_alerts_deduplication_key_identity (namespace_id, deduplication_key)))
                        ON
                            a.namespace_id = @p_namespace_id
                            AND a.deduplication_key = d.deduplication_key
                            AND a.alert_id = d.alert_id
                    WHERE d.deduplication_key IS NOT NULL
                );
                DELETE a FROM {{schema}}.alerts a WITH (FORCESEEK) INNER JOIN @alert_del d ON d.alert_id = a.alert_id
                WHERE EXISTS (SELECT 1 FROM @job_del j WHERE j.job_id = a.job_id);
                DELETE c FROM {{schema}}.checkpoints c WITH (FORCESEEK) INNER JOIN @job_del d ON d.job_id = c.job_id;
                DELETE r FROM {{schema}}.jobs_state r WITH (FORCESEEK) INNER JOIN @job_del d ON d.job_id = r.job_id;
                DELETE s FROM {{schema}}.steps s WITH (FORCESEEK) INNER JOIN @job_del d ON d.job_id = s.job_id;
                DELETE st FROM {{schema}}.schedules_state st WITH (FORCESEEK (pk_schedules_state (schedule_id)))
                INNER JOIN {{schema}}.schedules s WITH (FORCESEEK (ix_schedules_job (job_id))) ON s.schedule_id = st.schedule_id
                INNER JOIN @job_del d ON d.job_id = s.job_id;
                DELETE s FROM {{schema}}.schedules s WITH (FORCESEEK) INNER JOIN @job_del d ON d.job_id = s.job_id;
                DELETE j FROM {{schema}}.jobs j WITH (FORCESEEK) INNER JOIN @job_del d ON d.job_id = j.job_id;
                SET @rows = (SELECT COUNT(*) FROM @job_del);

            END;

        ELSE IF @p_section = 2
            BEGIN
                DELETE @event_del;

                -- Collected by the clustered key, so each delete is a direct seek on pk_events.
                -- Events still owed to the alert projector stay past the cutoff; see the floor above.
                INSERT INTO @event_del (worker_id, event_id)
                SELECT TOP (@p_batch_size) e.worker_id, e.event_id
                FROM {{schema}}.events e WITH (UPDLOCK, READPAST, FORCESEEK (ix_events_namespace_created (namespace_id)))
                WHERE
                    e.namespace_id = @p_namespace_id
                    AND e.created_at_utc <= @p_cutoff_utc
                    AND NOT (
                        e.entity_code = 50 /* EntityCode.Job */
                        AND e.created_at_utc >= @floor_utc
                        AND (e.created_at_utc > @floor_utc OR e.event_id > @floor_id)
                        AND (
                            e.event_code IN (41 /* EventCode.JobExecutionFailed */, 42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */)
                            OR (e.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND e.execution_number > 1)
                        )
                        AND EXISTS (
                            SELECT 1
                            FROM {{schema}}.definitions d
                            WHERE d.definition_id = e.definition_id AND d.alert_profile_code_effective <> 0 /* AlertProfileCode.None */
                        )
                    )
                ORDER BY e.created_at_utc, e.event_id;
                DELETE e FROM {{schema}}.events e WITH (FORCESEEK) INNER JOIN @event_del d ON d.worker_id = e.worker_id AND d.event_id = e.event_id;
                SET @rows = (SELECT COUNT(*) FROM @event_del);

            END;

        ELSE IF @p_section = 3
            BEGIN
                DELETE @alert_del;
                DELETE @alert_locked;

                INSERT INTO @alert_del (alert_id, deduplication_key)
                SELECT TOP (@p_batch_size) alert_id, deduplication_key
                FROM {{schema}}.alerts WITH (READPAST)
                WHERE
                    namespace_id = @p_namespace_id
                    AND created_at_utc <= @p_cutoff_utc
                    AND delivery_status_code IN (
                        30 /* AlertDeliveryStatusCode.Suppressed */,
                        100 /* AlertDeliveryStatusCode.Delivered */,
                        200 /* AlertDeliveryStatusCode.Failed */
                    )
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

                DELETE a
                FROM {{schema}}.alerts a WITH (FORCESEEK)
                INNER JOIN @alert_locked l ON l.alert_id = a.alert_id
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

                INSERT INTO @alert_del (alert_id, deduplication_key)
                SELECT TOP (@p_batch_size) alert_id, deduplication_key
                FROM {{schema}}.alerts WITH (READPAST)
                WHERE
                    namespace_id = @p_namespace_id
                    AND created_at_utc <= @p_cutoff_utc
                    AND delivery_status_code IN (
                        10 /* AlertDeliveryStatusCode.Pending */,
                        20 /* AlertDeliveryStatusCode.RetryAfter */
                    )
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

                DELETE a
                FROM {{schema}}.alerts a WITH (FORCESEEK)
                INNER JOIN @alert_locked l ON l.alert_id = a.alert_id
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
                DELETE @worker_del;

                -- Events about a terminal worker land after its last heartbeat, and each one stamps modified_at_utc, so
                -- the row goes once both are past the window and no Executing job it leases is left for recovery.
                INSERT INTO @worker_del (worker_id)
                SELECT TOP (@p_batch_size) w.worker_id
                FROM {{schema}}.workers w WITH (UPDLOCK, READPAST)
                WHERE
                    w.namespace_id = @p_namespace_id
                    AND w.worker_status_code IN (100 /* WorkerStatusCode.Stopped */, 200 /* WorkerStatusCode.Dead */)
                    AND w.last_seen_at_utc <= @p_cutoff_utc
                    AND w.modified_at_utc <= @p_cutoff_utc
                    AND NOT EXISTS (
                        SELECT 1
                        FROM {{schema}}.jobs_state r
                        WHERE
                            r.leased_by_worker_id = w.worker_id
                            -- ix_jobs_state_worker_inflight's filter restated: a filtered index serves only a query that states it.
                            AND r.leased_by_worker_id IS NOT NULL
                            AND r.job_status_code = 50 /* JobStatusCode.Executing */
                    )
                ORDER BY w.last_seen_at_utc, w.worker_id;
                DELETE w FROM {{schema}}.workers w WITH (FORCESEEK) INNER JOIN @worker_del d ON d.worker_id = w.worker_id;
                SET @rows = (SELECT COUNT(*) FROM @worker_del);

            END;

        ELSE IF @p_section = 6
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

        ELSE IF @p_section = 7
            BEGIN
            -- Unreferenced lanes, locked by id on the clustered key skipping a lane an enqueue or settle
            -- holds, then re-checked under the lock (docs/sql-internals.md, "Lane lock order").
                DELETE @lane_del;
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
            END;

        SELECT @rows AS deleted_count, @next_created_utc AS next_created_utc, @next_job_id AS next_job_id;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
