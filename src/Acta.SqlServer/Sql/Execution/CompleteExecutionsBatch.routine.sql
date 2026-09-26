CREATE OR ALTER PROCEDURE {{schema}}.complete_executions_batch
    @p_batch {{schema}}.complete_executions_batch READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

        /* Lock order: the batch's lanes one row at a time in id order, then its runtime rows
           (docs/internals/sql-execution-policy.md, "Lane lock order"). extend_worker_leases never takes
           a lane, so a heartbeat holding runtime rows never waits on anything a flush holds first. */
        DECLARE @lanes TABLE (id BIGINT NOT NULL PRIMARY KEY);
        DECLARE @promoted_lanes TABLE (id BIGINT NOT NULL PRIMARY KEY);
        INSERT INTO @lanes (id)
        SELECT DISTINCT r.lane_id
        FROM {{schema}}.runtimes r
        INNER JOIN @p_batch b ON b.job_id = r.job_id
        WHERE r.lane_id IS NOT NULL;

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

        DECLARE @updated TABLE (
            ordinal INT NOT NULL PRIMARY KEY,
            job_id BIGINT NOT NULL,
            execution_number INT NOT NULL,
            job_ref UNIQUEIDENTIFIER NOT NULL,
            namespace_id INT NOT NULL,
            lineage_root_id BIGINT NULL,
            definition_id INT NOT NULL,
            tenant_id INT NULL,
            audit_level_code TINYINT NOT NULL
        );

        UPDATE r
        SET
            status_code = CASE WHEN b.succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */ ELSE 200 /* JobStatusCode.Failed */ END,
            failure_count = COALESCE(b.failure_count, r.failure_count),
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = CASE
                WHEN b.retention_seconds IS NOT NULL
                    THEN DATEADD(SECOND, b.retention_seconds, @now)
                ELSE r.retention_until_utc
            END,
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT
            b.ordinal, INSERTED.job_id, INSERTED.execution_number, j.job_ref, j.namespace_id,
            j.lineage_root_id, j.definition_id, j.tenant_id, j.audit_level_code
        INTO @updated
        -- FORCESEEK keeps this update on a key seek instead of a lock-escalating scan of runtimes;
        -- see docs/internals/sql-execution-policy.md.
        FROM {{schema}}.runtimes r WITH (FORCESEEK)
        INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
        INNER JOIN @p_batch b
            ON
                b.job_id = r.job_id
                AND b.execution_number = r.execution_number
        WHERE
            r.status_code = 50 /* JobStatusCode.Executing */
            AND j.parent_id IS NULL
            AND r.leased_by_worker_id = b.worker_id;

        -- LOOP JOIN makes fk_results_jobs seek each job instead of scanning jobs
        -- (docs/internals/sql-execution-policy.md, "Foreign keys in set-based writes").
        INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
        SELECT
            u.job_id,
            u.execution_number,
            b.result_format_id,
            b.result,
            @now
        FROM @updated u
        INNER JOIN @p_batch b ON b.ordinal = u.ordinal
        WHERE b.result_format_id <> 0 /* JobPayloadFormat.None */
        OPTION (LOOP JOIN);

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key,
            job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id,
            worker_id, from_status_code, to_status_code, execution_status_code, duration_ms,
            reason_code, reason_message
        )
        SELECT
            41 /* EventCode.JobExecutionFinished */,
            @now,
            u.namespace_id,
            70 /* ActorCode.Worker */,
            NULL,
            u.job_id,
            u.job_ref,
            u.execution_number,
            COALESCE(u.lineage_root_id, u.job_id),
            u.definition_id,
            u.tenant_id,
            b.worker_id,
            50 /* JobStatusCode.Executing */,
            CASE WHEN b.succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */ ELSE 200 /* JobStatusCode.Failed */ END,
            CASE WHEN b.succeeded = 1 THEN 100 /* ExecutionStatusCode.Succeeded */ ELSE 200 /* ExecutionStatusCode.Failed */ END,
            b.duration_ms,
            b.reason_code,
            b.reason_message
        FROM @updated u
        INNER JOIN @p_batch b ON b.ordinal = u.ordinal
        WHERE
            u.audit_level_code = 20 /* JobAuditLevelCode.Audit */
            OR (u.audit_level_code = 10 /* JobAuditLevelCode.Failures */ AND b.succeeded = 0);

        -- At Failures a success is written only when it answers a recorded failure: the job's newest
        -- finished event is not a success, same rule as complete_execution. Its own statement, so the
        -- optimizer cannot hoist the timeline seek above the cheap gate and run it for every Audit row.
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key,
            job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id,
            worker_id, from_status_code, to_status_code, execution_status_code, duration_ms,
            reason_code, reason_message
        )
        SELECT
            41 /* EventCode.JobExecutionFinished */,
            @now,
            u.namespace_id,
            70 /* ActorCode.Worker */,
            NULL,
            u.job_id,
            u.job_ref,
            u.execution_number,
            COALESCE(u.lineage_root_id, u.job_id),
            u.definition_id,
            u.tenant_id,
            b.worker_id,
            50 /* JobStatusCode.Executing */,
            100 /* JobStatusCode.Succeeded */,
            100 /* ExecutionStatusCode.Succeeded */,
            b.duration_ms,
            b.reason_code,
            b.reason_message
        FROM @updated u
        INNER JOIN @p_batch b ON b.ordinal = u.ordinal
        CROSS APPLY (
            SELECT TOP (1) e.execution_status_code FROM {{schema}}.events e
            WHERE e.job_id = u.job_id AND e.event_code = 41 /* EventCode.JobExecutionFinished */
            ORDER BY e.created_at_utc DESC, e.id DESC
        ) newest
        WHERE
            u.audit_level_code = 10 /* JobAuditLevelCode.Failures */
            AND b.succeeded = 1
            AND newest.execution_status_code <> 100 /* ExecutionStatusCode.Succeeded */;

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

                /* Promotion under the lane lock (docs/internals/sql-execution-policy.md, "Lane lock order"):
                   a Blocked lowest-id unfinished member becomes Ready at its own due instant or now. */
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
                            INSERT INTO @promoted_lanes (id) VALUES (@lane_next);
                    END;

                SET @lane_cursor = @lane_next;
            END;

        SELECT
            b.ordinal,
            CAST(CASE WHEN u.ordinal IS NOT NULL THEN 1 ELSE 0 END AS SMALLINT) AS finalized,
            -- The finalized row's lane promoted a member, which the caller announces to the namespace.
            CAST(CASE WHEN u.ordinal IS NOT NULL AND EXISTS (
                SELECT 1
                FROM {{schema}}.runtimes pr
                INNER JOIN @promoted_lanes pl ON pl.id = pr.lane_id
                WHERE pr.job_id = b.job_id
            ) THEN 1 ELSE 0 END AS SMALLINT) AS lane_promoted
        FROM @p_batch b
        LEFT JOIN @updated u ON u.ordinal = b.ordinal
        ORDER BY b.ordinal;

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
