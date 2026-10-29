CREATE OR ALTER PROCEDURE {{schema}}.reclaim_stuck_jobs
    @p_namespace_id INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

        DECLARE
            @reclaimed TABLE
            (
                job_id BIGINT NOT NULL PRIMARY KEY,
                namespace_id INT NOT NULL,
                execution_number INT NOT NULL,
                lineage_root_job_id BIGINT NULL,
                definition_id INT NOT NULL,
                tenant_id INT NULL,
                worker_id INT NOT NULL,
                to_status_code TINYINT NOT NULL,
                event_recording_code TINYINT NOT NULL,
                alert_profile_code TINYINT NOT NULL,
                parent_job_id BIGINT NULL
            );

        /* A pass visits at most 1,000 of the namespace's lanes from a random lane id with wrap and repairs at
           most 100 stranded ones in visiting order (docs/sql-internals.md, "Lane lock order").
           Each head is sought through ix_jobs_state_lane, named because a small jobs_state table tempts a scan. */
        DECLARE @window TABLE (ord INT IDENTITY (1, 1) PRIMARY KEY, lane_id BIGINT NOT NULL);
        DECLARE @stranded TABLE (lane_id BIGINT NOT NULL PRIMARY KEY);
        DECLARE @repaired TABLE (job_id BIGINT NOT NULL PRIMARY KEY);
        DECLARE @min_lane BIGINT, @max_lane BIGINT, @start_lane BIGINT, @window_count INT;
        SELECT @min_lane = MIN(l.lane_id), @max_lane = MAX(l.lane_id) FROM {{schema}}.lanes l;
        IF @max_lane IS NOT NULL
            BEGIN
                SET @start_lane = @min_lane + CAST(FLOOR(RAND(CHECKSUM(NEWID())) * (@max_lane - @min_lane + 1)) AS BIGINT);
                INSERT INTO @window (lane_id)
                SELECT TOP (1000) l.lane_id
                FROM {{schema}}.lanes l
                WHERE l.namespace_id = @p_namespace_id AND l.lane_id >= @start_lane
                ORDER BY l.lane_id;
                SET @window_count = @@ROWCOUNT;
                INSERT INTO @window (lane_id)
                SELECT TOP (1000 - @window_count) l.lane_id
                FROM {{schema}}.lanes l
                WHERE l.namespace_id = @p_namespace_id AND l.lane_id < @start_lane
                ORDER BY l.lane_id;

                INSERT INTO @stranded (lane_id)
                SELECT TOP (100) w.lane_id
                FROM @window w
                CROSS APPLY (
                    SELECT TOP (1) m.job_status_code
                    FROM {{schema}}.jobs_state m WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
                    WHERE
                        m.lane_id = w.lane_id
                        AND m.lane_id IS NOT NULL
                        AND m.job_status_code IN (
                            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                            30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                        )
                    ORDER BY m.job_id
                ) h
                WHERE
                    h.job_status_code = 15 /* JobStatusCode.Blocked */
                    AND NOT EXISTS (
                        SELECT 1
                        FROM {{schema}}.jobs_state o WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
                        WHERE
                            o.lane_id = w.lane_id
                            AND o.lane_id IS NOT NULL
                            AND o.job_status_code IN (
                                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                                50 /* JobStatusCode.Executing */
                            )
                    )
                ORDER BY w.ord;
            END;

        /* Stuck candidates are read through the filtered ix_jobs_state_worker_inflight without holding locks,
           and only those rows are locked below, by primary key: the index is shared by every namespace, so
           holding its keys would make another namespace's sweep skip its own rows under READPAST. */
        DECLARE @stuck TABLE (job_id BIGINT NOT NULL PRIMARY KEY, lane_id BIGINT NULL, worker_id INT NOT NULL);
        INSERT INTO @stuck (job_id, lane_id, worker_id)
        SELECT r.job_id, r.lane_id, r.leased_by_worker_id
        FROM {{schema}}.jobs_state r WITH (INDEX (ix_jobs_state_worker_inflight), READPAST)
        WHERE
            r.job_status_code = 50 /* JobStatusCode.Executing */
            AND r.leased_by_worker_id IS NOT NULL
            AND r.lease_expires_at_utc < @now
            AND r.namespace_id = @p_namespace_id;

        /* Lanes are locked one UPDLOCK seek at a time in id order, skipping a lane another transaction holds,
           whose stuck rows and repair wait for a later pass. */
        DECLARE @candidates TABLE (lane_id BIGINT NOT NULL PRIMARY KEY);
        INSERT INTO @candidates (lane_id)
        SELECT k.lane_id
        FROM @stuck k
        WHERE k.lane_id IS NOT NULL
        UNION
        SELECT s.lane_id
        FROM @stranded s;

        DECLARE @lanes TABLE (lane_id BIGINT NOT NULL PRIMARY KEY);
        DECLARE @lane_cursor BIGINT = 0, @lane_next BIGINT, @lane_locked BIGINT;
        WHILE 1 = 1
            BEGIN
                SET @lane_next = NULL;
                SELECT TOP (1) @lane_next = c.lane_id
                FROM @candidates c
                WHERE c.lane_id > @lane_cursor
                ORDER BY c.lane_id;

                IF @lane_next IS NULL
                    BREAK;

                SET @lane_locked = NULL;
                SELECT @lane_locked = l.lane_id
                FROM {{schema}}.lanes l WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE l.lane_id = @lane_next;

                IF @lane_locked IS NOT NULL
                    INSERT INTO @lanes (lane_id) VALUES (@lane_locked);

                SET @lane_cursor = @lane_next;
            END;

        /* Each worker a stuck row names is stamped at the instant of the Orphaned event below, so the purge keeps it
           while that event lasts. Workers lock before any jobs_state row, the order the heartbeat takes them. */
        UPDATE w
        SET
            modified_at_utc = @now,
            version = w.version + 1
        FROM {{schema}}.workers w WITH (FORCESEEK, ROWLOCK)
        INNER JOIN (
            SELECT DISTINCT k.worker_id
            FROM @stuck k
            WHERE
                k.lane_id IS NULL
                OR k.lane_id IN (SELECT e.lane_id FROM @lanes e)
        ) s ON s.worker_id = w.worker_id;

        /* The walk read without locks, so each stranded lane's head is re-read under its lock and promoted
           only while it is still Blocked and no member runs. */
        UPDATE r
        SET
            job_status_code = 10 /* JobStatusCode.Ready */,
            next_run_at_utc = CASE WHEN r.next_run_at_utc > @now THEN r.next_run_at_utc ELSE @now END,
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT INSERTED.job_id INTO @repaired (job_id)
        FROM @stranded s
        INNER JOIN @lanes e ON e.lane_id = s.lane_id
        CROSS APPLY (
            SELECT TOP (1) m.job_id, m.job_status_code
            FROM {{schema}}.jobs_state m WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
            WHERE
                m.lane_id = s.lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
        ) h
        INNER JOIN {{schema}}.jobs_state r WITH (FORCESEEK) ON r.job_id = h.job_id
        WHERE
            h.job_status_code = 15 /* JobStatusCode.Blocked */
            AND r.job_status_code = 15 /* JobStatusCode.Blocked */
            AND NOT EXISTS (
                SELECT 1
                FROM {{schema}}.jobs_state o WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
                WHERE
                    o.lane_id = s.lane_id
                    AND o.lane_id IS NOT NULL
                    AND o.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                        50 /* JobStatusCode.Executing */
                    )
            );

        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            entity_code,
            entity_id,
            definition_id,
            tenant_id,
            lineage_root_job_id,
            execution_number,
            event_message,
            details_format_id,
            details
        )
        SELECT
            72 /* EventCode.JobResumed */,
            @now,
            j.namespace_id,
            50 /* EntityCode.Job */,
            j.job_id,
            j.definition_id,
            j.tenant_id,
            COALESCE(j.lineage_root_job_id, j.job_id),
            r.execution_number,
            N'Lane had no live head; crash recovery released its lowest Blocked member.',
            1 /* JobPayloadFormat.Json */,
            CAST('{"reason":"lane-repaired"}' AS VARBINARY(MAX))
        FROM @repaired x
        INNER JOIN {{schema}}.jobs j ON j.job_id = x.job_id
        INNER JOIN {{schema}}.jobs_state r ON r.job_id = x.job_id
        WHERE j.event_recording_code IN (10 /* EventRecordingCode.Failures */, 20 /* EventRecordingCode.Audit */);

        WITH stuck AS (
            SELECT
                r.job_id,
                -- Saturates at 32767, the ceiling WorkerFinalizer.FailureCountCeiling names; registration caps MaxAttempts there.
                CASE WHEN r.failure_count >= 32767 THEN 32767 ELSE r.failure_count + 1 END AS new_failure_count,
                jd.max_attempts_effective AS max_attempts,
                jd.retention_seconds_effective AS retention_seconds,
                jd.alert_profile_code_effective AS alert_profile_code,
                /* The job was parked on THIS slot: the suspend copied the slot's due into
                   next_run_at_utc, so the equality names the wait this attempt woke for, and an
                   unbounded wait (NULL due) matches nothing. Expired means the timeout had resolved. */
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM {{schema}}.checkpoints c
                    WHERE
                        c.job_id = r.job_id
                        AND c.checkpoint_kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
                        AND c.checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */
                        AND c.due_at_utc = r.next_run_at_utc
                ) THEN 1 ELSE 0 END AS wait_resolved,
                -- A slot's spent budget returns it to Idle with its firing settled Failed, never to Failed itself.
                CASE WHEN j.job_kind_code = 20 /* JobKindCode.PermanentSlot */ THEN 1 ELSE 0 END AS is_slot
            FROM @stuck k
            INNER JOIN {{schema}}.jobs_state r WITH (READPAST, UPDLOCK, ROWLOCK, FORCESEEK) ON r.job_id = k.job_id
            INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
            INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
            WHERE
                r.job_status_code = 50 /* JobStatusCode.Executing */
                AND r.leased_by_worker_id IS NOT NULL
                AND r.lease_expires_at_utc < @now
                AND r.namespace_id = @p_namespace_id
                AND (r.lane_id IS NULL OR r.lane_id IN (SELECT e.lane_id FROM @lanes e))
        )

        /* Budget-neutral for a resolved wait: the surviving path would have ended this attempt at no
           cost, so the job goes back to Suspended on the same past deadline, unclaimed and uncharged,
           and the replay lands whatever outcome the waiting overload chooses. */
        UPDATE r
        SET
            job_status_code = CASE
                WHEN s.wait_resolved = 1
                    THEN 20 /* JobStatusCode.Suspended */
                WHEN s.new_failure_count >= s.max_attempts
                    THEN CASE WHEN s.is_slot = 1 THEN 40 /* JobStatusCode.Idle */ ELSE 200 /* JobStatusCode.Failed */ END
                ELSE 10  /* JobStatusCode.Ready */
            END,
            failure_count = CASE
                WHEN s.wait_resolved = 1
                    THEN r.failure_count
                ELSE s.new_failure_count
            END,
            next_run_at_utc = CASE
                WHEN s.wait_resolved = 1
                    THEN r.next_run_at_utc
                WHEN s.new_failure_count >= s.max_attempts
                    THEN CASE WHEN s.is_slot = 1 THEN NULL ELSE r.next_run_at_utc END
                ELSE @now
            END,
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = CASE
                WHEN s.wait_resolved = 0 AND s.is_slot = 0 AND s.new_failure_count >= s.max_attempts
                    THEN DATEADD(SECOND, s.retention_seconds, @now)
                ELSE r.retention_until_utc
            END,
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT
            INSERTED.job_id, j.namespace_id, INSERTED.execution_number,
            j.lineage_root_job_id, j.definition_id, j.tenant_id, DELETED.leased_by_worker_id,
            INSERTED.job_status_code, j.event_recording_code, s.alert_profile_code,
            j.parent_job_id
        INTO
            @reclaimed (
                job_id, namespace_id, execution_number, lineage_root_job_id,
                definition_id, tenant_id, worker_id, to_status_code, event_recording_code, alert_profile_code, parent_job_id
            )
        -- FORCESEEK keeps this update on a key seek instead of a lock-escalating scan of jobs_state;
        -- see docs/sql-internals.md.
        FROM {{schema}}.jobs_state r WITH (FORCESEEK)
        INNER JOIN stuck s ON s.job_id = r.job_id
        INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id;

        /* An orphaned attempt that spent the budget is the job failing for good; any other lands Ready or back on its
           wait. Failures and Audit record it, and so does an enabled alert profile at any level, in this transaction,
           so the projector meets every lease-loss transition it alerts on. */
        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
            namespace_id,
            entity_code,
            entity_id,
            definition_id,
            tenant_id,
            lineage_root_job_id,
            worker_id,
            execution_number,
            event_message,
            details_format_id,
            details
        )
        SELECT
            CASE WHEN to_status_code = 200 /* JobStatusCode.Failed */ THEN 44 /* EventCode.JobFailed */ ELSE 42 /* EventCode.JobExecutionOrphaned */ END,
            @now,
            namespace_id,
            50 /* EntityCode.Job */,
            job_id,
            definition_id,
            tenant_id,
            COALESCE(lineage_root_job_id, job_id),
            worker_id,
            execution_number,
            /* Suspended is reachable only through the resolved-wait arm above, so the landed status is
               what tells the two messages apart. */
            CASE WHEN to_status_code = 20 /* JobStatusCode.Suspended */
                THEN N'Worker lease expired while an expired wait was resolving; re-armed on the same deadline with no attempt charged.'
                ELSE N'Worker lease expired; reclaimed by crash recovery.'
            END,
            1 /* JobPayloadFormat.Json */,
            CAST(
                CASE to_status_code
                    WHEN 200 /* JobStatusCode.Failed */ THEN '{"reason":"lease-expired"}'
                    WHEN 40 /* JobStatusCode.Idle */ THEN '{"reason":"lease-expired","to":"idle"}'
                    WHEN 20 /* JobStatusCode.Suspended */ THEN '{"reason":"lease-expired","to":"suspended"}'
                    ELSE '{"reason":"lease-expired","to":"ready"}'
                END AS VARBINARY(MAX)
            )
        FROM @reclaimed
        WHERE
            event_recording_code IN (10 /* EventRecordingCode.Failures */, 20 /* EventRecordingCode.Audit */)
            OR alert_profile_code <> 0 /* AlertProfileCode.None */;

        -- A slot's lost attempt that spent the budget settles its firing Failed and wakes the members parked on the slot.
        UPDATE cp
        SET
            checkpoint_status_code = 120 /* CheckpointStatusCode.Failed */,
            modified_at_utc = @now,
            version = cp.version + 1
        FROM {{schema}}.checkpoints cp WITH (FORCESEEK)
        INNER JOIN @reclaimed r ON r.job_id = cp.job_id
        WHERE
            cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
            AND cp.checkpoint_name = 'sys.firing'
            AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
            AND r.to_status_code = 40 /* JobStatusCode.Idle */;

        UPDATE st
        SET
            next_evaluation_at_utc = @now,
            evaluation_status_code = 10 /* EvaluationStatusCode.Scheduled */,
            modified_at_utc = @now,
            version = st.version + 1
        FROM {{schema}}.schedules_state st WITH (FORCESEEK)
        INNER JOIN {{schema}}.schedules js ON js.schedule_id = st.schedule_id
        INNER JOIN @reclaimed r ON r.job_id = js.job_id
        WHERE
            r.to_status_code = 40 /* JobStatusCode.Idle */
            AND st.evaluation_status_code IN (20 /* EvaluationStatusCode.WaitingForCapacity */, 50 /* EvaluationStatusCode.SlotUnavailable */);

        /* A head reclaimed to Failed hands its lane on; a head re-armed Ready keeps it. Promotion runs
           under the lane locks and re-reads each head rather than trusting the walk. */
        UPDATE r
        SET
            job_status_code = 10 /* JobStatusCode.Ready */,
            next_run_at_utc = CASE WHEN r.next_run_at_utc > @now THEN r.next_run_at_utc ELSE @now END,
            modified_at_utc = @now,
            version = r.version + 1
        FROM @lanes e
        CROSS APPLY (
            SELECT TOP (1) m.job_id, m.job_status_code
            FROM {{schema}}.jobs_state m WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
            WHERE
                m.lane_id = e.lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
        ) h
        INNER JOIN {{schema}}.jobs_state r WITH (FORCESEEK) ON r.job_id = h.job_id
        WHERE
            h.job_status_code = 15 /* JobStatusCode.Blocked */
            AND r.job_status_code = 15 /* JobStatusCode.Blocked */
            AND NOT EXISTS (
                SELECT 1
                FROM {{schema}}.jobs_state o WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
                WHERE
                    o.lane_id = e.lane_id
                    AND o.lane_id IS NOT NULL
                    AND o.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                        50 /* JobStatusCode.Executing */
                    )
            );

        SELECT
            job_id,
            to_status_code AS to_status,
            parent_job_id,
            CAST(0 AS TINYINT) AS lane_repaired
        FROM @reclaimed
        UNION ALL
        SELECT
            job_id,
            CAST(10 /* JobStatusCode.Ready */ AS TINYINT) AS to_status,
            NULL AS parent_job_id,
            CAST(1 AS TINYINT) AS lane_repaired
        FROM @repaired;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
