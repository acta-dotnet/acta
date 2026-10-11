CREATE OR ALTER PROCEDURE {{schema}}.complete_executions
    @p_leased_by_worker_id INT,
    @p_batch {{schema}}.complete_executions_batch READONLY,
    @p_schedule_advances {{schema}}.advance_schedules_batch READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @locked INT, @lanes BIT, @children BIT, @waits BIT, @shapes BIT, @recurring BIT;
        DECLARE @rows TABLE (
            job_id BIGINT NOT NULL,
            execution_number INT NOT NULL,
            lane_id BIGINT NULL,
            parent_id BIGINT NULL,
            sig_state TINYINT NULL,
            sig_due DATETIME2(3) NULL,
            PRIMARY KEY (job_id, execution_number)
        );
        DECLARE @done TABLE (
            job_id BIGINT NOT NULL PRIMARY KEY,
            execution_number INT NOT NULL,
            status_code TINYINT NOT NULL,
            lane_id BIGINT NULL,
            parent_id BIGINT NULL,
            slot_wake DATETIME2(3) NULL,
            parent_released BIT NOT NULL DEFAULT 0,
            lane_promoted BIT NOT NULL DEFAULT 0
        );

        /* Every completion takes the same steps, each skipped when no row needs it. Locks are taken lanes first,
           then awaited signal slots, own runtimes, schedules, parent latches, and parent runtimes, each in key order
           (docs/internals/sql-execution-policy.md, "Lane lock order"). lane_id and parent_id never change. */
        INSERT INTO @rows (job_id, execution_number, lane_id, parent_id)
        SELECT c.job_id, c.execution_number, r.lane_id, j.parent_id
        FROM @p_batch c
        INNER JOIN {{schema}}.runtimes r ON r.job_id = c.job_id
        INNER JOIN {{schema}}.jobs j ON j.id = c.job_id;

        SELECT
            @lanes = MAX(CASE WHEN lane_id IS NOT NULL THEN 1 ELSE 0 END),
            @children = MAX(CASE WHEN parent_id IS NOT NULL THEN 1 ELSE 0 END)
        FROM @rows;

        SELECT
            @waits = MAX(CASE WHEN wait_signal_name IS NOT NULL THEN 1 ELSE 0 END),
            @shapes = MAX(
                CASE WHEN reschedule_status_code IS NOT NULL OR handler_status_code IS NOT NULL OR final_status IS NOT NULL THEN 1 ELSE 0 END
            ),
            @recurring = MAX(CASE WHEN final_status IS NOT NULL THEN 1 ELSE 0 END)
        FROM @p_batch;

        IF @lanes = 1
            BEGIN
                CREATE TABLE #lanes (id BIGINT NOT NULL PRIMARY KEY);
                INSERT INTO #lanes (id)
                SELECT DISTINCT lane_id FROM @rows WHERE lane_id IS NOT NULL;

                SELECT @locked = COUNT(*)
                FROM #lanes x
                INNER JOIN {{schema}}.lanes l WITH (UPDLOCK, ROWLOCK, FORCESEEK) ON l.id = x.id
                OPTION (FORCE ORDER, LOOP JOIN);
            END

        -- The awaited slot is the only place a wait's deadline lives; the lock lets a raise that won the race land the job Ready.
        IF @waits = 1
            UPDATE x
            SET sig_state = s.status_code, sig_due = s.due_at_utc
            FROM @rows x
            INNER JOIN @p_batch c ON c.job_id = x.job_id AND c.execution_number = x.execution_number
            INNER JOIN {{schema}}.checkpoints s WITH (UPDLOCK, HOLDLOCK, FORCESEEK)
                ON
                    s.job_id = x.job_id
                    AND s.kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
                    AND s.name = c.wait_signal_name
            OPTION (FORCE ORDER, LOOP JOIN);

        /* The update locks each row through its clustered key in job_id order. The next run is written apart, only for
           the shapes that move it, so a batch of landings leaves next_run_at_utc and its index alone; a row being completed
           is Executing and still carries the instant it was claimed on, so a re-armed row is never Ready without one. */
        UPDATE r
        SET
            status_code = s.to_status,
            failure_count = COALESCE(c.failure_count, r.failure_count),
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = CASE
                WHEN
                    s.to_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    AND c.retention_seconds IS NOT NULL
                    THEN DATEADD(SECOND, c.retention_seconds, @now)
                ELSE r.retention_until_utc
            END,
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT
            INSERTED.job_id, INSERTED.execution_number, INSERTED.status_code, x.lane_id, x.parent_id
        INTO @done (job_id, execution_number, status_code, lane_id, parent_id)
        FROM @rows x
        INNER JOIN {{schema}}.runtimes r WITH (INDEX (pk_runtimes), FORCESEEK, ROWLOCK) ON r.job_id = x.job_id
        INNER JOIN @p_batch c ON c.job_id = x.job_id AND c.execution_number = x.execution_number
        CROSS APPLY (
            SELECT CASE
                WHEN c.wait_signal_name IS NOT NULL AND x.sig_state = 20 /* CheckpointStatusCode.Set */ THEN 10 /* JobStatusCode.Ready */
                WHEN c.wait_signal_name IS NOT NULL THEN 20 /* JobStatusCode.Suspended */
                WHEN c.reschedule_status_code IS NOT NULL THEN 10 /* JobStatusCode.Ready */
                WHEN c.handler_status_code IS NOT NULL THEN c.handler_status_code
                WHEN c.final_status IS NOT NULL THEN c.final_status
                WHEN c.succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */
                ELSE 200 /* JobStatusCode.Failed */
            END AS to_status
        ) s
        WHERE
            r.execution_number = x.execution_number
            AND r.status_code = 50 /* JobStatusCode.Executing */
            AND r.leased_by_worker_id = @p_leased_by_worker_id;

        IF @shapes = 1
            UPDATE r
            SET next_run_at_utc = CASE
                WHEN c.wait_signal_name IS NOT NULL AND x.sig_state = 20 /* CheckpointStatusCode.Set */ THEN @now
                WHEN c.wait_signal_name IS NOT NULL THEN x.sig_due
                WHEN c.reschedule_status_code IS NOT NULL
                    THEN COALESCE(c.reschedule_resume_at_utc, DATEADD(SECOND, c.reschedule_delay_seconds, @now))
                -- A handler's pause keeps a next run, which marks it held (ScheduleWalker, "held").
                WHEN c.handler_status_code = 30 /* JobStatusCode.Paused */ THEN @now
                WHEN c.handler_status_code IS NOT NULL THEN NULL
                ELSE c.job_next_run_at_utc
            END
            FROM @done d
            INNER JOIN @rows x ON x.job_id = d.job_id AND x.execution_number = d.execution_number
            INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
            INNER JOIN {{schema}}.runtimes r WITH (INDEX (pk_runtimes), FORCESEEK, ROWLOCK) ON r.job_id = d.job_id
            WHERE c.reschedule_status_code IS NOT NULL OR c.handler_status_code IS NOT NULL OR c.final_status IS NOT NULL;

        -- fk_results_jobs seeks each job by key (docs/internals/sql-execution-policy.md, "Foreign keys in set-based writes").
        INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
        SELECT d.job_id, d.execution_number, c.result_format_id, c.result, @now
        FROM @done d
        INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
        WHERE c.result_format_id <> 0 /* JobPayloadFormat.None */
        OPTION (LOOP JOIN);

        IF @recurring = 1
            BEGIN
                /* A fire advances its schedules: an edit made while it ran wins over the older plan, an operator pause
                   wins unless it was timed and has elapsed, and an orphaned schedule is left alone. The audit insert
                   shares the update's predicate, so the two agree. */
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    reason_code, reason_message
                )
                SELECT
                    102 /* EventCode.SchedulePauseExpired */, @now, j.namespace_id,
                    10 /* ActorCode.Sys */, NULL,
                    d.job_id, j.job_ref, d.execution_number,
                    COALESCE(j.lineage_root_id, d.job_id), j.definition_id, j.tenant_id,
                    NULL,
                    NULL, NULL,
                    NULL, NULL,
                    NULL, js.name
                FROM @p_schedule_advances adv
                INNER JOIN {{schema}}.schedules js ON js.id = adv.schedule_id
                INNER JOIN @done d ON d.job_id = js.job_id
                INNER JOIN {{schema}}.jobs j ON j.id = d.job_id
                WHERE
                    (adv.expected_version IS NULL OR js.version = adv.expected_version)
                    AND j.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
                    AND js.status_code = 30 /* ScheduleStatusCode.Paused */
                    AND js.paused_until_utc IS NOT NULL
                    AND js.paused_until_utc <= @now;

                UPDATE js
                SET
                    js.next_run_at_utc = adv.next_run_at_utc,
                    js.last_occurrence_at_utc = js.next_run_at_utc,
                    js.status_code = 10 /* ScheduleStatusCode.Active */,
                    js.paused_until_utc = NULL,
                    js.modified_at_utc = @now,
                    js.version = js.version + 1
                FROM @p_schedule_advances adv
                INNER JOIN {{schema}}.schedules js ON js.id = adv.schedule_id
                INNER JOIN @done d ON d.job_id = js.job_id
                WHERE
                    (adv.expected_version IS NULL OR js.version = adv.expected_version)
                    AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
                    AND (
                        js.status_code <> 30 /* ScheduleStatusCode.Paused */
                        OR (js.paused_until_utc IS NOT NULL AND js.paused_until_utc <= @now)
                    );

                -- The slot's next state is read from the schedules as they stand now, not from the plan made before the handler ran.
                UPDATE d
                SET
                    d.status_code = CASE WHEN w.wake IS NULL THEN 30 /* JobStatusCode.Paused */ ELSE 10 /* JobStatusCode.Ready */ END,
                    d.slot_wake = w.wake
                FROM @done d
                INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
                OUTER APPLY (
                    SELECT
                        MIN(
                            CASE WHEN js.status_code = 30 /* ScheduleStatusCode.Paused */ THEN js.paused_until_utc ELSE js.next_run_at_utc END
                        ) AS wake
                    FROM {{schema}}.schedules js
                    WHERE js.job_id = d.job_id AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
                ) w
                WHERE c.final_status IS NOT NULL;

                UPDATE r
                SET status_code = d.status_code, next_run_at_utc = d.slot_wake
                FROM @done d
                INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
                INNER JOIN {{schema}}.runtimes r WITH (INDEX (pk_runtimes), FORCESEEK, ROWLOCK) ON r.job_id = d.job_id
                WHERE c.final_status IS NOT NULL;

                DELETE res
                FROM {{schema}}.results res
                INNER JOIN (
                    SELECT
                        k.job_id,
                        k.execution_number,
                        c.recurring_result_cap AS cap,
                        ROW_NUMBER() OVER (PARTITION BY k.job_id ORDER BY k.execution_number DESC) AS newest
                    FROM @done d
                    INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
                    INNER JOIN {{schema}}.results k ON k.job_id = d.job_id
                    WHERE c.final_status IS NOT NULL AND c.recurring_result_cap > 0
                ) old ON old.job_id = res.job_id AND old.execution_number = res.execution_number
                WHERE old.newest > old.cap;
            END

        /* At Failures a failure carrying no re-arm or handler hold is written, and a success only when it answers a
           recorded failure, which closes the incident: the job's newest finished event is not a success. One seek on
           the timeline index per row. */
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message
        )
        SELECT
            41 /* EventCode.JobExecutionFinished */, @now, j.namespace_id,
            70 /* ActorCode.Worker */, NULL,
            d.job_id, j.job_ref, d.execution_number,
            COALESCE(j.lineage_root_id, d.job_id), j.definition_id, j.tenant_id,
            @p_leased_by_worker_id,
            50 /* JobStatusCode.Executing */, d.status_code,
            CASE
                WHEN c.reschedule_status_code IS NOT NULL THEN c.reschedule_status_code
                WHEN c.handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 220 /* ExecutionStatusCode.Cancelled */
                WHEN c.handler_status_code = 30 /* JobStatusCode.Paused */ THEN 152 /* ExecutionStatusCode.Paused */
                WHEN c.succeeded = 1 THEN 100 /* ExecutionStatusCode.Succeeded */
                ELSE 200 /* ExecutionStatusCode.Failed */
            END,
            c.duration_ms,
            c.reason_code, c.reason_message
        FROM @done d
        INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
        INNER JOIN {{schema}}.jobs j ON j.id = d.job_id
        WHERE
            j.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
            OR (
                j.audit_level_code = 10 /* DefinitionAuditLevelCode.Failures */
                AND c.reschedule_status_code IS NULL
                AND (c.handler_status_code IS NULL OR c.handler_status_code NOT IN (220 /* JobStatusCode.Cancelled */, 30 /* JobStatusCode.Paused */))
                AND (
                    c.succeeded = 0
                    OR COALESCE((
                        SELECT TOP (1) e.execution_status_code FROM {{schema}}.events e
                        WHERE e.job_id = d.job_id AND e.event_code = 41 /* EventCode.JobExecutionFinished */
                        ORDER BY e.created_at_utc DESC, e.id DESC), 100) <> 100 /* ExecutionStatusCode.Succeeded */
                )
            );

        -- At Audit each shape also says what it did: rolled over or paused a recurring slot, re-armed, or held by its handler.
        IF @shapes = 1
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                actor_code, actor,
                job_id, job_ref, execution_number,
                lineage_root_id, definition_id, tenant_id,
                worker_id,
                from_status_code, to_status_code,
                execution_status_code, duration_ms,
                reason_code, reason_message
            )
            SELECT
                k.event_code, @now, j.namespace_id,
                70 /* ActorCode.Worker */, NULL,
                d.job_id, j.job_ref, d.execution_number,
                COALESCE(j.lineage_root_id, d.job_id), j.definition_id, j.tenant_id,
                @p_leased_by_worker_id,
                50 /* JobStatusCode.Executing */, d.status_code,
                NULL, NULL,
                c.reason_code, c.reason_message
            FROM @done d
            INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
            INNER JOIN {{schema}}.jobs j ON j.id = d.job_id
            CROSS APPLY (
                VALUES
                (
                    1,
                    CASE
                        WHEN c.final_status IN (10 /* JobStatusCode.Ready */, 30 /* JobStatusCode.Paused */)
                            THEN CASE
                                WHEN d.status_code = 10 /* JobStatusCode.Ready */ THEN 50 /* EventCode.JobRecurringRolledOver */
                                ELSE 71 /* EventCode.JobPaused */
                            END
                    END
                ),
                (
                    2,
                    -- A wait whose signal had already arrived landed Ready and re-armed nothing.
                    CASE
                        WHEN c.reschedule_status_code IS NOT NULL AND NOT (c.wait_signal_name IS NOT NULL AND d.status_code = 10 /* JobStatusCode.Ready */)
                            THEN CASE
                                WHEN c.reschedule_status_code = 151 /* ExecutionStatusCode.Suspended */ THEN 60 /* EventCode.JobSuspended */
                                ELSE 61 /* EventCode.JobRescheduled */
                            END
                    END
                ),
                (
                    3,
                    CASE
                        WHEN c.handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 70 /* EventCode.JobCancelled */
                        WHEN c.handler_status_code = 30 /* JobStatusCode.Paused */ THEN 71 /* EventCode.JobPaused */
                    END
                )
            ) k (ord, event_code)
            WHERE j.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */ AND k.event_code IS NOT NULL
            ORDER BY d.job_id, k.ord;

        -- A child that landed terminal sets its latch on the parent and wakes a Suspended parent.
        IF @children = 1
            BEGIN
                CREATE TABLE #parents (
                    parent_id BIGINT NOT NULL,
                    child_id BIGINT NOT NULL,
                    latch_name VARCHAR(128) NOT NULL,
                    envelope VARBINARY(MAX) NOT NULL,
                    reason_code TINYINT NULL,
                    reason_message NVARCHAR(512) NULL,
                    latch_state TINYINT NULL,
                    parent_status TINYINT NULL,
                    parent_execution INT NULL,
                    PRIMARY KEY (parent_id, child_id)
                );

                INSERT INTO #parents (parent_id, child_id, latch_name, envelope, reason_code, reason_message)
                SELECT
                    d.parent_id,
                    d.job_id,
                    'sys.child.' + CAST(d.job_id AS VARCHAR(20)),
                    CAST(
                        CAST(
                            N'{"childJobId":' + CAST(d.job_id AS NVARCHAR(20)) + N',"status":' + CAST(d.status_code AS NVARCHAR(3)) + N'}'
                            AS VARCHAR(MAX)
                        ) COLLATE latin1_general_100_bin2_utf8 AS VARBINARY(MAX)
                    ),
                    c.reason_code,
                    c.reason_message
                FROM @done d
                INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
                WHERE
                    d.parent_id IS NOT NULL
                    AND d.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

                UPDATE p
                SET latch_state = s.status_code
                FROM #parents p
                LEFT JOIN {{schema}}.checkpoints s WITH (UPDLOCK, HOLDLOCK, FORCESEEK)
                    ON s.job_id = p.parent_id AND s.kind_code = 50 /* CheckpointKindCode.ChildLatch */ AND s.name = p.latch_name
                OPTION (FORCE ORDER, LOOP JOIN);

                UPDATE p
                SET parent_status = pr.status_code, parent_execution = pr.execution_number
                FROM #parents p
                INNER JOIN {{schema}}.runtimes pr WITH (UPDLOCK, ROWLOCK, FORCESEEK) ON pr.job_id = p.parent_id
                OPTION (FORCE ORDER, LOOP JOIN);

                /* No revival: an Expired latch already timed the parent's wait out under this same slot lock, so a later
                   child writes no latch and wakes no one, though the audit event records it. A finished parent takes
                   nothing. */
                UPDATE s
                SET
                    status_code = 20 /* CheckpointStatusCode.Set */,
                    value_format_id = 1 /* JobPayloadFormat.Json */,
                    value = p.envelope,
                    modified_at_utc = @now,
                    version = s.version + 1
                FROM #parents p
                INNER JOIN {{schema}}.checkpoints s WITH (FORCESEEK)
                    ON s.job_id = p.parent_id AND s.kind_code = 50 /* CheckpointKindCode.ChildLatch */ AND s.name = p.latch_name
                WHERE
                    p.latch_state <> 30 /* CheckpointStatusCode.Expired */
                    AND p.parent_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

                INSERT INTO {{schema}}.checkpoints (
                    job_id, kind_code, name, status_code, value_format_id, value,
                    created_at_utc, modified_at_utc, version
                )
                SELECT
                    p.parent_id, 50 /* CheckpointKindCode.ChildLatch */, p.latch_name,
                    20 /* CheckpointStatusCode.Set */, 1 /* JobPayloadFormat.Json */, p.envelope,
                    @now, @now, 0
                FROM #parents p
                WHERE
                    p.latch_state IS NULL
                    AND p.parent_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                OPTION (LOOP JOIN);

                -- Capped because the caller's message can already sit at the column width.
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    reason_code, reason_message
                )
                SELECT
                    80 /* EventCode.JobSignalRaised */, @now, pj.namespace_id,
                    10 /* ActorCode.Sys */, NULL,
                    p.parent_id, pj.job_ref, p.parent_execution,
                    COALESCE(pj.lineage_root_id, p.parent_id), pj.definition_id, pj.tenant_id,
                    NULL,
                    p.parent_status, p.parent_status,
                    NULL, NULL,
                    p.reason_code,
                    CASE
                        WHEN p.latch_state = 30 /* CheckpointStatusCode.Expired */
                            THEN LEFT(
                                COALESCE(p.reason_message + N' ', N'') + N'Child outcome not applied: the wait had already expired.',
                                512
                            )
                        ELSE p.reason_message
                    END
                FROM #parents p
                INNER JOIN {{schema}}.jobs pj ON pj.id = p.parent_id
                WHERE
                    pj.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
                    AND p.parent_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                ORDER BY p.parent_id, p.child_id;

                UPDATE pr
                SET
                    status_code = 10 /* JobStatusCode.Ready */,
                    next_run_at_utc = @now,
                    modified_at_utc = @now,
                    version = pr.version + 1
                FROM (
                    SELECT DISTINCT parent_id
                    FROM #parents
                    WHERE
                        parent_status = 20 /* JobStatusCode.Suspended */
                        AND (latch_state IS NULL OR latch_state <> 30 /* CheckpointStatusCode.Expired */)
                ) w
                INNER JOIN {{schema}}.runtimes pr WITH (INDEX (pk_runtimes), FORCESEEK, ROWLOCK) ON pr.job_id = w.parent_id
                WHERE pr.status_code = 20 /* JobStatusCode.Suspended */;

                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    reason_code, reason_message
                )
                SELECT
                    72 /* EventCode.JobResumed */, @now, pj.namespace_id,
                    10 /* ActorCode.Sys */, NULL,
                    w.parent_id, pj.job_ref, w.parent_execution,
                    COALESCE(pj.lineage_root_id, w.parent_id), pj.definition_id, pj.tenant_id,
                    NULL,
                    20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */,
                    NULL, NULL,
                    60 /* EventReasonCode.JobSignalReleased */, NULL
                FROM (
                    SELECT DISTINCT parent_id, parent_execution
                    FROM #parents
                    WHERE
                        parent_status = 20 /* JobStatusCode.Suspended */
                        AND (latch_state IS NULL OR latch_state <> 30 /* CheckpointStatusCode.Expired */)
                ) w
                INNER JOIN {{schema}}.jobs pj ON pj.id = w.parent_id
                WHERE pj.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */;

                UPDATE d
                SET parent_released = 1
                FROM @done d
                INNER JOIN #parents p ON p.child_id = d.job_id
                WHERE
                    p.parent_status = 20 /* JobStatusCode.Suspended */
                    AND (p.latch_state IS NULL OR p.latch_state <> 30 /* CheckpointStatusCode.Expired */);
            END

        /* A laned job that finished, or that its handler paused, leaves the lane with no runner: the lowest-id
           unfinished member becomes Ready at its own due instant or now when it is Blocked, and any other head keeps
           the lane. The lane lock taken above makes the read and the write one step. */
        IF @lanes = 1
            BEGIN
                CREATE TABLE #promoted (lane_id BIGINT NOT NULL PRIMARY KEY);

                UPDATE m
                SET
                    status_code = 10 /* JobStatusCode.Ready */,
                    next_run_at_utc = CASE WHEN m.next_run_at_utc > @now THEN m.next_run_at_utc ELSE @now END,
                    modified_at_utc = @now,
                    version = m.version + 1
                OUTPUT INSERTED.lane_id INTO #promoted (lane_id)
                FROM (
                    SELECT DISTINCT d.lane_id
                    FROM @done d
                    WHERE
                        d.lane_id IS NOT NULL
                        AND d.status_code IN (
                            30 /* JobStatusCode.Paused */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */,
                            220 /* JobStatusCode.Cancelled */
                        )
                ) settled
                CROSS APPLY (
                    SELECT TOP (1) h.job_id, h.status_code
                    FROM {{schema}}.runtimes h WITH (INDEX (ix_runtimes_lane), FORCESEEK)
                    WHERE
                        h.lane_id = settled.lane_id
                        AND h.lane_id IS NOT NULL
                        AND h.status_code IN (
                            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                            30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                        )
                    ORDER BY h.job_id
                ) head
                INNER JOIN {{schema}}.runtimes m WITH (INDEX (pk_runtimes), FORCESEEK, ROWLOCK) ON m.job_id = head.job_id
                WHERE head.status_code = 15 /* JobStatusCode.Blocked */ AND m.status_code = 15 /* JobStatusCode.Blocked */;

                UPDATE d
                SET lane_promoted = 1
                FROM @done d
                INNER JOIN #promoted p ON p.lane_id = d.lane_id;
            END

        SELECT
            c.job_id,
            c.execution_number,
            CASE
                WHEN d.job_id IS NOT NULL THEN 1 /* CompleteExecutionsAction.Completed */
                WHEN
                    r.status_code IS NULL
                    OR r.status_code IN (
                        100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
                    )
                    THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
                WHEN r.leased_by_worker_id IS NULL OR r.leased_by_worker_id <> @p_leased_by_worker_id
                    THEN 2 /* CompleteExecutionsAction.NotOwner */
                ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
            END AS action,
            r.status_code AS final_status_code,
            r.next_run_at_utc AS final_next_run_at_utc,
            @now AS db_now,
            CAST(COALESCE(d.parent_released, 0) AS TINYINT) AS parent_released,
            CAST(COALESCE(d.lane_promoted, 0) AS TINYINT) AS lane_promoted
        FROM @p_batch c
        LEFT JOIN @done d ON d.job_id = c.job_id AND d.execution_number = c.execution_number
        LEFT JOIN {{schema}}.runtimes r ON r.job_id = c.job_id;

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
