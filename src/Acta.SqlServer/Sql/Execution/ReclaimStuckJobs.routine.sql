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
                id BIGINT NOT NULL PRIMARY KEY,
                job_ref UNIQUEIDENTIFIER NOT NULL,
                namespace_id INT NOT NULL,
                execution_number INT NOT NULL,
                lineage_root_id BIGINT NULL,
                definition_id INT NOT NULL,
                tenant_id INT NULL,
                from_status_code TINYINT NOT NULL,
                to_status_code TINYINT NOT NULL,
                audit_level_code TINYINT NOT NULL,
                parent_id BIGINT NULL
            );

        /* A probe for any Blocked row gates a walk that seeks each active lane's head through
           ix_runtimes_lane. Both name the index: on a small runtimes table the optimizer prefers a
           clustered scan to either. */
        DECLARE @stranded TABLE (id BIGINT NOT NULL PRIMARY KEY);
        DECLARE @repaired TABLE (id BIGINT NOT NULL PRIMARY KEY);
        DECLARE @walk BIGINT = 0, @walk_lane BIGINT, @walk_status TINYINT, @walk_namespace INT, @stranded_count INT = 0;
        IF EXISTS (
            SELECT 1
            FROM {{schema}}.runtimes b WITH (INDEX (ix_runtimes_lane))
            WHERE
                b.lane_id IS NOT NULL
                AND b.status_code = 15 /* JobStatusCode.Blocked */
                AND b.namespace_id = @p_namespace_id
        )
            BEGIN
                WHILE @stranded_count < 100
                    BEGIN
                        SET @walk_lane = NULL;
                        SELECT TOP (1)
                            @walk_lane = m.lane_id,
                            @walk_status = m.status_code,
                            @walk_namespace = m.namespace_id
                        FROM {{schema}}.runtimes m WITH (INDEX (ix_runtimes_lane), FORCESEEK)
                        WHERE
                            m.lane_id > @walk
                            AND m.lane_id IS NOT NULL
                            AND m.status_code IN (
                                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                                30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                            )
                        ORDER BY m.lane_id, m.job_id;

                        IF @walk_lane IS NULL
                            BREAK;

                        IF @walk_status = 15 /* JobStatusCode.Blocked */ AND @walk_namespace = @p_namespace_id
                            BEGIN
                                INSERT INTO @stranded (id) VALUES (@walk_lane);
                                SET @stranded_count += 1;
                            END;

                        SET @walk = @walk_lane;
                    END;
            END;

        /* The stuck rows' and stranded lanes one row at a time in id order, then the runtime rows
           (docs/internals/sql-execution-policy.md, "Lane lock order"). A laned row whose lease expires
           after the lanes are taken waits for the next pass. */
        DECLARE @lanes TABLE (id BIGINT NOT NULL PRIMARY KEY);
        INSERT INTO @lanes (id)
        SELECT r.lane_id
        FROM {{schema}}.runtimes r
        WHERE
            r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
            AND r.lease_expires_at_utc < @now
            AND r.namespace_id = @p_namespace_id
            AND r.lane_id IS NOT NULL
        UNION
        SELECT s.id
        FROM @stranded s;

        DECLARE @lane_cursor BIGINT = 0, @lane_next BIGINT, @head_id BIGINT, @head_status TINYINT, @promoted INT;
        WHILE 1 = 1
            BEGIN
                SET @lane_next = NULL;
                SELECT TOP (1) @lane_next = e.id
                FROM @lanes e
                WHERE e.id > @lane_cursor
                ORDER BY e.id;

                IF @lane_next IS NULL
                    BREAK;

                SELECT @lane_cursor = l.id
                FROM {{schema}}.lanes l WITH (UPDLOCK, ROWLOCK)
                WHERE l.id = @lane_next;

                SET @lane_cursor = @lane_next;
            END;

        /* The walk read without locks, so each stranded lane is re-read under its lock and repaired only
           when its lowest-id unfinished member is still Blocked. */
        SET @lane_cursor = 0;
        WHILE 1 = 1
            BEGIN
                SET @lane_next = NULL;
                SELECT TOP (1) @lane_next = s.id
                FROM @stranded s
                WHERE s.id > @lane_cursor
                ORDER BY s.id;

                IF @lane_next IS NULL
                    BREAK;

                SET @promoted = 0;
                WHILE @promoted = 0
                    BEGIN
                        SET @head_id = NULL;
                        SET @head_status = NULL;

                        SELECT TOP (1)
                            @head_id = m.job_id,
                            @head_status = m.status_code
                        FROM {{schema}}.runtimes m
                        WHERE
                            m.lane_id = @lane_next
                            AND m.lane_id IS NOT NULL
                            AND m.status_code IN (
                                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                                30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                            )
                        ORDER BY m.job_id;

                        IF @head_status IS NULL OR @head_status <> 15 /* JobStatusCode.Blocked */
                            BREAK;

                        UPDATE {{schema}}.runtimes
                        SET
                            status_code = 10 /* JobStatusCode.Ready */,
                            next_run_at_utc = CASE WHEN next_run_at_utc > @now THEN next_run_at_utc ELSE @now END,
                            modified_at_utc = @now,
                            version = version + 1
                        WHERE
                            job_id = @head_id
                            AND status_code = 15 /* JobStatusCode.Blocked */;

                        SET @promoted = @@ROWCOUNT;
                        IF @promoted > 0
                            INSERT INTO @repaired (id) VALUES (@head_id);
                    END;

                SET @lane_cursor = @lane_next;
            END;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message
        )
        SELECT
            72 /* EventCode.JobResumed */,
            @now,
            j.namespace_id,
            10 /* ActorCode.Sys */,
            NULL,
            j.id,
            j.job_ref,
            r.execution_number,
            COALESCE(j.lineage_root_id, j.id),
            j.definition_id,
            j.tenant_id,
            NULL,
            15 /* JobStatusCode.Blocked */,
            10 /* JobStatusCode.Ready */,
            NULL,
            NULL,
            67 /* JobEventReasonCode.JobLaneRepaired */,
            N'Lane had no live head; the sys.recovery system job released its lowest Blocked member.'
        FROM @repaired x
        INNER JOIN {{schema}}.jobs j ON j.id = x.id
        INNER JOIN {{schema}}.runtimes r ON r.job_id = x.id
        WHERE j.audit_level_code IN (10 /* JobAuditLevelCode.Failures */, 20 /* JobAuditLevelCode.Audit */);

        WITH stuck AS (
            SELECT
                r.job_id AS id,
                r.status_code AS from_status,
                r.failure_count + 1 AS new_failure_count,
                jd.max_attempts_effective AS max_attempts,
                jd.retention_seconds_effective AS retention_seconds,
                /* The job was parked on THIS slot: the suspend copied the slot's due into
                   next_run_at_utc, so the equality names the wait this attempt woke for, and an
                   unbounded wait (NULL due) matches nothing. Expired means the timeout had resolved. */
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM {{schema}}.checkpoints c
                    WHERE
                        c.job_id = r.job_id
                        AND c.kind_code IN (20 /* JobCheckpointKindCode.Signal */, 50 /* JobCheckpointKindCode.ChildLatch */)
                        AND c.status_code = 30 /* JobCheckpointStatusCode.Expired */
                        AND c.due_at_utc = r.next_run_at_utc
                ) THEN 1 ELSE 0 END AS wait_resolved,
                /* MaxAttempts is the one-off retry budget and a slot's failure_count accumulates
                   across occurrences, so a recurring reclaim always re-arms Ready instead of
                   terminally failing the schedule - mirroring ComputeRecurringOutcome on the worker path. */
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM {{schema}}.schedules sc
                    WHERE sc.job_id = r.job_id
                ) THEN 1 ELSE 0 END AS is_recurring
            FROM {{schema}}.runtimes r WITH (READPAST, UPDLOCK, ROWLOCK)
            INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
            INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
            WHERE
                r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
                AND r.lease_expires_at_utc < @now
                AND r.namespace_id = @p_namespace_id
                AND (r.lane_id IS NULL OR r.lane_id IN (SELECT e.id FROM @lanes e))
        )

        /* Budget-neutral for a resolved wait: the surviving path would have ended this attempt at no
           cost, so the job goes back to Suspended on the same past deadline, unclaimed and uncharged,
           and the replay lands whatever outcome the waiting overload chooses. */
        UPDATE r
        SET
            status_code = CASE
                WHEN s.wait_resolved = 1
                    THEN 20 /* JobStatusCode.Suspended */
                WHEN s.is_recurring = 0 AND s.new_failure_count >= s.max_attempts
                    THEN 200 /* JobStatusCode.Failed */
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
                WHEN s.is_recurring = 0 AND s.new_failure_count >= s.max_attempts
                    THEN r.next_run_at_utc
                ELSE @now
            END,
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = CASE
                WHEN s.wait_resolved = 0 AND s.is_recurring = 0 AND s.new_failure_count >= s.max_attempts
                    THEN DATEADD(SECOND, s.retention_seconds, @now)
                ELSE r.retention_until_utc
            END,
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT
            INSERTED.job_id, j.job_ref, j.namespace_id, INSERTED.execution_number,
            j.lineage_root_id, j.definition_id, j.tenant_id,
            DELETED.status_code, INSERTED.status_code, j.audit_level_code,
            j.parent_id
        INTO
            @reclaimed (
                id, job_ref, namespace_id, execution_number, lineage_root_id,
                definition_id, tenant_id, from_status_code, to_status_code, audit_level_code, parent_id
            )
        -- FORCESEEK keeps this update on a key seek instead of a lock-escalating scan of runtimes;
        -- see docs/internals/sql-execution-policy.md.
        FROM {{schema}}.runtimes r WITH (FORCESEEK)
        INNER JOIN stuck s ON s.id = r.job_id
        INNER JOIN {{schema}}.jobs j ON j.id = r.job_id;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message
        )
        SELECT
            41 /* EventCode.JobExecutionFinished */,
            @now,
            namespace_id,
            10 /* ActorCode.Sys */,
            NULL,
            id,
            job_ref,
            execution_number,
            COALESCE(lineage_root_id, id),
            definition_id,
            tenant_id,
            NULL,
            from_status_code,
            to_status_code,
            230 /* ExecutionStatusCode.Orphaned */,
            NULL,
            21 /* JobEventReasonCode.JobLeaseExpired */,
            /* Suspended is reachable only through the resolved-wait arm above, so the landed status is
               what tells the two messages apart. */
            CASE WHEN to_status_code = 20 /* JobStatusCode.Suspended */
                THEN N'Worker lease expired while an expired wait was resolving; re-armed on the same deadline with no attempt charged.'
                ELSE N'Worker lease expired; reclaimed by the sys.recovery system job.'
            END
        FROM @reclaimed
        WHERE audit_level_code IN (10 /* JobAuditLevelCode.Failures */, 20 /* JobAuditLevelCode.Audit */);

        /* A head reclaimed to Failed hands its lane on; a head re-armed Ready keeps it. Promotion runs
           under the lane lock and re-reads rather than trusting an update that matched nothing. */
        SET @lane_cursor = 0;
        WHILE 1 = 1
            BEGIN
                SET @lane_next = NULL;
                SELECT TOP (1) @lane_next = e.id
                FROM @lanes e
                WHERE e.id > @lane_cursor
                ORDER BY e.id;

                IF @lane_next IS NULL
                    BREAK;

                SET @promoted = 0;
                WHILE @promoted = 0
                    BEGIN
                        SET @head_id = NULL;
                        SET @head_status = NULL;

                        SELECT TOP (1)
                            @head_id = m.job_id,
                            @head_status = m.status_code
                        FROM {{schema}}.runtimes m
                        WHERE
                            m.lane_id = @lane_next
                            AND m.lane_id IS NOT NULL
                            AND m.status_code IN (
                                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                                30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                            )
                        ORDER BY m.job_id;

                        IF @head_status IS NULL OR @head_status <> 15 /* JobStatusCode.Blocked */
                            BREAK;

                        UPDATE {{schema}}.runtimes
                        SET
                            status_code = 10 /* JobStatusCode.Ready */,
                            next_run_at_utc = CASE WHEN next_run_at_utc > @now THEN next_run_at_utc ELSE @now END,
                            modified_at_utc = @now,
                            version = version + 1
                        WHERE
                            job_id = @head_id
                            AND status_code = 15 /* JobStatusCode.Blocked */;

                        SET @promoted = @@ROWCOUNT;
                    END;

                SET @lane_cursor = @lane_next;
            END;

        SELECT
            id AS job_id,
            to_status_code AS to_status,
            parent_id,
            CAST(0 AS TINYINT) AS lane_repaired
        FROM @reclaimed
        UNION ALL
        SELECT
            id,
            CAST(10 /* JobStatusCode.Ready */ AS TINYINT),
            NULL,
            CAST(1 AS TINYINT)
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
GO
