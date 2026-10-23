CREATE OR ALTER PROCEDURE {{schema}}.complete_executions
    @p_leased_by_worker_id INT,
    @p_batch {{schema}}.complete_executions READONLY,
    @p_plain BIT = 0,
    @p_schedule_advances NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @locked INT, @lanes BIT = 0, @children BIT = 0, @waits BIT = 0, @shapes BIT = 0, @recurring BIT = 0;
        DECLARE @rows TABLE (
            job_id BIGINT NOT NULL,
            execution_number INT NOT NULL,
            lane_id BIGINT NULL,
            parent_job_id BIGINT NULL,
            sig_state TINYINT NULL,
            sig_due DATETIME2(3) NULL,
            PRIMARY KEY (job_id, execution_number)
        );
        DECLARE @done TABLE (
            job_id BIGINT NOT NULL PRIMARY KEY,
            execution_number INT NOT NULL,
            job_status_code TINYINT NOT NULL,
            lane_id BIGINT NULL,
            parent_job_id BIGINT NULL,
            slot_wake DATETIME2(3) NULL,
            parent_released BIT NOT NULL DEFAULT 0,
            lane_promoted BIT NOT NULL DEFAULT 0,
            prior_failure_count INT NOT NULL
        );

        /* Each step is skipped when no row needs it, and a plain batch (the binder marks one with no shape, lane, or parent)
           reads no lane or parent and skips them all. Locks: a fire's schedule lock, lanes, slot locks, awaited slots, own jobs_state
           rows, schedules, parent latches and rows, in key order (docs/sql-internals.md, "Lane lock order"); lane and parent ids never change. */
        -- Every join from the batch into jobs or jobs_state seeks (same policy, "Locking reads seek").
        IF @p_plain = 1
            INSERT INTO @rows (job_id, execution_number)
            SELECT c.job_id, c.execution_number
            FROM @p_batch c;
        ELSE
            INSERT INTO @rows (job_id, execution_number, lane_id, parent_job_id)
            SELECT c.job_id, c.execution_number, r.lane_id, j.parent_job_id
            FROM @p_batch c
            INNER JOIN {{schema}}.jobs_state r WITH (FORCESEEK) ON r.job_id = c.job_id
            INNER JOIN {{schema}}.jobs j WITH (FORCESEEK) ON j.job_id = c.job_id;

        IF @p_plain = 0
            BEGIN
            SELECT
                @lanes = MAX(CASE WHEN lane_id IS NOT NULL THEN 1 ELSE 0 END),
                @children = MAX(CASE WHEN parent_job_id IS NOT NULL THEN 1 ELSE 0 END)
            FROM @rows;

            SELECT
                @waits = MAX(CASE WHEN wait_signal_name IS NOT NULL THEN 1 ELSE 0 END),
                @shapes = MAX(
                    CASE
                        WHEN reschedule_event_code IS NOT NULL OR handler_job_status_code IS NOT NULL OR final_job_status_code IS NOT NULL THEN 1
                        ELSE 0
                    END
                ),
                @recurring = MAX(CASE WHEN final_job_status_code IS NOT NULL THEN 1 ELSE 0 END)
            FROM @p_batch;

            -- Each fired slot's namespace schedule lock, shared, before any other lock (same policy, "Schedule lock").
            IF @recurring = 1
                BEGIN
                    DECLARE @schedule_ns INT = (
                        SELECT MIN(r.namespace_id)
                        FROM @p_batch c
                        INNER JOIN {{schema}}.jobs_state r WITH (FORCESEEK) ON r.job_id = c.job_id
                        WHERE c.final_job_status_code IS NOT NULL
                    );
                    DECLARE @schedule_lock NVARCHAR(255), @schedule_lock_result INT;
                    WHILE @schedule_ns IS NOT NULL
                        BEGIN
                            SET @schedule_lock = N'{{schema}}.schedules.' + CAST(@schedule_ns AS NVARCHAR(12));
                            EXEC @schedule_lock_result = sp_getapplock @schedule_lock, 'Shared', 'Transaction', -1;
                            IF @schedule_lock_result < 0
                                THROW 51205, 'ACTA:SCHEDULE_LOCK:The namespace schedule lock was not granted (deadlock victim or cancelled).', 1;
                            SET @schedule_ns = (
                                SELECT MIN(r.namespace_id)
                                FROM @p_batch c
                                INNER JOIN {{schema}}.jobs_state r WITH (FORCESEEK) ON r.job_id = c.job_id
                                WHERE c.final_job_status_code IS NOT NULL AND r.namespace_id > @schedule_ns
                            );
                        END;
                END

            IF @lanes = 1
                BEGIN
                    CREATE TABLE #lanes (lane_id BIGINT NOT NULL PRIMARY KEY);
                    INSERT INTO #lanes (lane_id)
                    SELECT DISTINCT lane_id FROM @rows WHERE lane_id IS NOT NULL;

                    SELECT @locked = COUNT(*)
                    FROM #lanes x
                    INNER JOIN {{schema}}.lanes l WITH (UPDLOCK, ROWLOCK, FORCESEEK) ON l.lane_id = x.lane_id
                    OPTION (FORCE ORDER, LOOP JOIN);
                END

            /* Each slot this batch may read or write takes the slot's own lock, as wait_signal and raise_signal name it, all in
               name order and before any slot row or jobs_state row (docs/sql-internals.md, "Slot locks"): each awaited slot
               under both kinds a wait can name, and each parent's latch for a child here, whether or not the child lands terminal. */
            IF @waits = 1 OR @children = 1
                BEGIN
                    DECLARE @slot_locks TABLE (name NVARCHAR(255) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY);
                    INSERT INTO @slot_locks (name)
                    SELECT CONCAT(N'{{schema}}.slot:', c.job_id, N':', k.kind_code, N':', c.wait_signal_name)
                    FROM @p_batch c
                    CROSS JOIN (VALUES (20 /* CheckpointKindCode.Signal */), (50 /* CheckpointKindCode.ChildLatch */)) k (kind_code)
                    WHERE c.wait_signal_name IS NOT NULL
                    UNION
                    SELECT CONCAT(N'{{schema}}.slot:', x.parent_job_id, N':50:sys.child.', x.job_id)
                    FROM @rows x
                    WHERE x.parent_job_id IS NOT NULL;
                    DECLARE @slot_lock NVARCHAR(255) = (SELECT MIN(name) FROM @slot_locks), @slot_lock_result INT;
                    WHILE @slot_lock IS NOT NULL
                        BEGIN
                            -- Positional: resource, mode, owner, timeout (-1 waits like any lock).
                            EXEC @slot_lock_result = sp_getapplock @slot_lock, 'Exclusive', 'Transaction', -1;
                            IF @slot_lock_result < 0
                                THROW 51205, 'ACTA:SLOT_LOCK:The checkpoint slot lock was not granted (deadlock victim or cancelled).', 1;
                            SET @slot_lock = (SELECT MIN(name) FROM @slot_locks WHERE name > @slot_lock);
                        END;
                END

            -- The awaited slot is the only place a wait's deadline lives; its lock lets a raise that won the race land the job
            -- Ready, and makes a raise that creates a missing slot wait for this landing and then wake the job.
            IF @waits = 1
                UPDATE x
                SET sig_state = s.checkpoint_status_code, sig_due = s.due_at_utc
                FROM @rows x
                INNER JOIN @p_batch c ON c.job_id = x.job_id AND c.execution_number = x.execution_number AND c.wait_signal_name IS NOT NULL
                INNER JOIN {{schema}}.checkpoints s WITH (UPDLOCK, FORCESEEK)
                    ON
                        s.job_id = x.job_id
                        AND s.checkpoint_kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
                        AND s.checkpoint_name = c.wait_signal_name
                OPTION (FORCE ORDER, LOOP JOIN);
            END

        /* Locks each row by its clustered key in job_id order. A row that re-arms, waits, is held, or fires stays Executing and
           leased here and takes its status, and a fire its result (ck_jobs_state_result_idle), with its next run below: it enters
           ix_jobs_state_claim_ready once, a consumed timer never meets ck_jobs_state_ready_due, and landings never write next_run_at_utc. */
        UPDATE r
        SET
            job_status_code = CASE WHEN s.shaped = 1 THEN r.job_status_code ELSE s.to_status END,
            failure_count = COALESCE(c.failure_count, r.failure_count),
            leased_by_worker_id = CASE WHEN s.shaped = 1 THEN r.leased_by_worker_id END,
            lease_expires_at_utc = CASE WHEN s.shaped = 1 THEN r.lease_expires_at_utc END,
            retention_until_utc = CASE
                WHEN
                    s.to_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    AND c.retention_seconds IS NOT NULL
                    THEN DATEADD(SECOND, c.retention_seconds, @now)
                ELSE r.retention_until_utc
            END,
            result_format_id = CASE WHEN s.shaped = 1 THEN r.result_format_id ELSE c.result_format_id END,
            result = CASE WHEN s.shaped = 1 THEN r.result ELSE c.result END,
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT
            INSERTED.job_id, INSERTED.execution_number, s.to_status, x.lane_id, x.parent_job_id, DELETED.failure_count
        INTO @done (job_id, execution_number, job_status_code, lane_id, parent_job_id, prior_failure_count)
        FROM @rows x
        INNER JOIN {{schema}}.jobs_state r WITH (INDEX (pk_jobs_state), FORCESEEK, ROWLOCK) ON r.job_id = x.job_id
        INNER JOIN @p_batch c ON c.job_id = x.job_id AND c.execution_number = x.execution_number
        CROSS APPLY (
            SELECT CASE
                WHEN c.wait_signal_name IS NOT NULL AND x.sig_state = 20 /* CheckpointStatusCode.Set */ THEN 10 /* JobStatusCode.Ready */
                WHEN c.wait_signal_name IS NOT NULL THEN 20 /* JobStatusCode.Suspended */
                WHEN c.reschedule_event_code IS NOT NULL THEN 10 /* JobStatusCode.Ready */
                WHEN c.handler_job_status_code IS NOT NULL THEN c.handler_job_status_code
                WHEN c.final_job_status_code IS NOT NULL THEN c.final_job_status_code
                WHEN c.succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */
                ELSE 200 /* JobStatusCode.Failed */
            END AS to_status,
            CASE
                WHEN c.reschedule_event_code IS NOT NULL OR c.handler_job_status_code IS NOT NULL OR c.final_job_status_code IS NOT NULL THEN 1
                ELSE 0
            END AS shaped
        ) s
        WHERE
            r.execution_number = x.execution_number
            AND r.job_status_code = 50 /* JobStatusCode.Executing */
            AND r.leased_by_worker_id = @p_leased_by_worker_id
        OPTION (FORCE ORDER);

        -- A recurring fire takes both in its own step, from the schedules as they stand after the advance.
        IF @shapes = 1
            UPDATE r
            SET
                job_status_code = d.job_status_code,
                leased_by_worker_id = NULL,
                lease_expires_at_utc = NULL,
                next_run_at_utc = CASE
                    WHEN c.wait_signal_name IS NOT NULL AND x.sig_state = 20 /* CheckpointStatusCode.Set */ THEN @now
                    WHEN c.wait_signal_name IS NOT NULL THEN x.sig_due
                    WHEN c.reschedule_event_code IS NOT NULL
                        THEN COALESCE(c.reschedule_resume_at_utc, DATEADD(SECOND, c.reschedule_delay_seconds, @now))
                    -- A handler's pause keeps a next run, which marks it held (ScheduleWalker, "held").
                    WHEN c.handler_job_status_code = 30 /* JobStatusCode.Paused */ THEN @now
                    ELSE NULL
                END
            FROM @done d
            INNER JOIN @rows x ON x.job_id = d.job_id AND x.execution_number = d.execution_number
            INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
            INNER JOIN {{schema}}.jobs_state r WITH (INDEX (pk_jobs_state), FORCESEEK, ROWLOCK) ON r.job_id = d.job_id
            WHERE (c.reschedule_event_code IS NOT NULL OR c.handler_job_status_code IS NOT NULL) AND c.final_job_status_code IS NULL;

        IF @p_plain = 0
            BEGIN
            IF @recurring = 1
                BEGIN
                    /* A fire advances its schedules: an edit made while it ran wins over the older plan, an operator pause
                       wins unless it was timed and has elapsed, and an orphaned or retired schedule is left alone. The audit
                       insert shares the update's predicate. A schedule name is dotted kebab ASCII, so it sits in the JSON unescaped. */
                    INSERT INTO {{schema}}.events (
                        event_code, created_at_utc, namespace_id,
                        entity_code, entity_id,
                        definition_id, tenant_id, lineage_root_job_id,
                        execution_number,
                        details_format_id, details
                    )
                    SELECT
                        102 /* EventCode.SchedulePauseExpired */, @now, j.namespace_id,
                        50 /* EntityCode.Job */, d.job_id,
                        j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, d.job_id),
                        d.execution_number,
                        1 /* JobPayloadFormat.Json */, CAST('{"schedule":"' + js.schedule_name + '"}' AS VARBINARY(MAX))
                    FROM OPENJSON(@p_schedule_advances) WITH (
                        job_id BIGINT '$.job_id', schedule_name VARCHAR(128) '$.schedule_name',
                        next_occurrence_at_utc DATETIME2(3) '$.next_occurrence_at_utc', expected_version INT '$.expected_version'
                    ) adv
                    INNER JOIN @done d ON d.job_id = adv.job_id
                    INNER JOIN {{schema}}.schedules js ON js.job_id = d.job_id AND js.schedule_name = adv.schedule_name
                    INNER JOIN {{schema}}.definitions sd ON sd.definition_id = js.definition_id
                    INNER JOIN {{schema}}.jobs j WITH (FORCESEEK) ON j.job_id = d.job_id
                    WHERE
                        (adv.expected_version IS NULL OR js.version = adv.expected_version)
                        AND sd.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                        AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
                        AND js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */
                        AND js.paused_until_utc IS NOT NULL
                        AND js.paused_until_utc <= @now;

                    UPDATE st
                    SET
                        st.next_occurrence_at_utc = adv.next_occurrence_at_utc,
                        st.last_advanced_occurrence_at_utc = st.next_occurrence_at_utc,
                        st.modified_at_utc = @now
                    FROM OPENJSON(@p_schedule_advances) WITH (
                        job_id BIGINT '$.job_id', schedule_name VARCHAR(128) '$.schedule_name',
                        next_occurrence_at_utc DATETIME2(3) '$.next_occurrence_at_utc', expected_version INT '$.expected_version'
                    ) adv
                    INNER JOIN @done d ON d.job_id = adv.job_id
                    INNER JOIN {{schema}}.schedules js ON js.job_id = d.job_id AND js.schedule_name = adv.schedule_name
                    INNER JOIN {{schema}}.definitions sd ON sd.definition_id = js.definition_id
                    INNER JOIN {{schema}}.schedules_state st WITH (FORCESEEK) ON st.job_id = js.job_id AND st.schedule_name = js.schedule_name
                    WHERE
                        (adv.expected_version IS NULL OR js.version = adv.expected_version)
                        AND sd.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                        AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
                        AND (
                            js.schedule_status_code <> 30 /* ScheduleStatusCode.Paused */
                            OR (js.paused_until_utc IS NOT NULL AND js.paused_until_utc <= @now)
                        );

                    -- A timed pause the advance passed is over, and only then does the fire touch the schedules row.
                    UPDATE js
                    SET
                        js.schedule_status_code = 10 /* ScheduleStatusCode.Active */,
                        js.paused_until_utc = NULL,
                        js.modified_at_utc = @now,
                        js.version = js.version + 1
                    FROM OPENJSON(@p_schedule_advances) WITH (
                        job_id BIGINT '$.job_id', schedule_name VARCHAR(128) '$.schedule_name', expected_version INT '$.expected_version'
                    ) adv
                    INNER JOIN @done d ON d.job_id = adv.job_id
                    INNER JOIN {{schema}}.schedules js WITH (FORCESEEK) ON js.job_id = d.job_id AND js.schedule_name = adv.schedule_name
                    INNER JOIN {{schema}}.definitions sd ON sd.definition_id = js.definition_id
                    WHERE
                        (adv.expected_version IS NULL OR js.version = adv.expected_version)
                        AND sd.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                        AND js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */
                        AND js.paused_until_utc IS NOT NULL
                        AND js.paused_until_utc <= @now;

                    -- The slot's next state is read from the schedules as they stand now, not from the plan made before the handler ran.
                    UPDATE d
                    SET
                        d.job_status_code = CASE WHEN w.wake IS NULL THEN 30 /* JobStatusCode.Paused */ ELSE 10 /* JobStatusCode.Ready */ END,
                        d.slot_wake = w.wake
                    FROM @done d
                    INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
                    OUTER APPLY (
                        SELECT
                            MIN(
                                CASE
                                    WHEN js.schedule_status_code = 30 /* ScheduleStatusCode.Paused */ THEN js.paused_until_utc
                                    ELSE st.next_occurrence_at_utc
                                END
                            ) AS wake
                        FROM {{schema}}.schedules js
                        INNER JOIN {{schema}}.definitions sd ON sd.definition_id = js.definition_id
                        INNER JOIN {{schema}}.schedules_state st ON st.job_id = js.job_id AND st.schedule_name = js.schedule_name
                        WHERE
                            js.job_id = d.job_id
                            AND js.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
                            AND sd.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
                    ) w
                    WHERE c.final_job_status_code IS NOT NULL;

                    UPDATE r
                    SET
                        job_status_code = d.job_status_code,
                        next_run_at_utc = d.slot_wake,
                        leased_by_worker_id = NULL,
                        lease_expires_at_utc = NULL,
                        result_format_id = c.result_format_id,
                        result = c.result
                    FROM @done d
                    INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
                    INNER JOIN {{schema}}.jobs_state r WITH (INDEX (pk_jobs_state), FORCESEEK, ROWLOCK) ON r.job_id = d.job_id
                    WHERE c.final_job_status_code IS NOT NULL;
                END
            END

        /* The attempt's own event under the recording gate (EventRecordingCode) plus the alert overlay, which also writes a one-shot's
           success past attempt 1, since a restart zeroes the count yet the success must resolve what the failure opened; a recurring
           fire keeps the recovery rule, or every idle tick would write. Details are fixed kebab words, so the JSON needs no escaping. */
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            entity_code, entity_id,
            definition_id, tenant_id, lineage_root_job_id,
            worker_id, execution_number, duration_ms,
            event_message, details_format_id, details
        )
        SELECT
            c.event_code, @now, j.namespace_id,
            50 /* EntityCode.Job */, d.job_id,
            j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, d.job_id),
            @p_leased_by_worker_id, d.execution_number, c.duration_ms,
            c.event_message,
            CASE WHEN x.details IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END,
            CAST(x.details AS VARBINARY(MAX))
        FROM @done d
        INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
        INNER JOIN {{schema}}.jobs j WITH (FORCESEEK) ON j.job_id = d.job_id
        INNER JOIN {{schema}}.definitions jd WITH (FORCESEEK) ON jd.definition_id = j.definition_id
        CROSS APPLY (
            SELECT CASE
                WHEN c.event_code = 41 /* EventCode.JobExecutionFailed */
                    THEN ',"to":"' + CASE d.job_status_code
                        WHEN 10 /* JobStatusCode.Ready */ THEN 'ready'
                        WHEN 20 /* JobStatusCode.Suspended */ THEN 'suspended'
                        WHEN 30 /* JobStatusCode.Paused */ THEN 'paused'
                    END + '"'
                WHEN c.event_code = 43 /* EventCode.JobExecutionYielded */
                    THEN ',"outcome":"' + CASE
                        WHEN c.reschedule_event_code = 60 /* EventCode.JobSuspended */ THEN 'suspended'
                        WHEN c.reschedule_event_code IS NOT NULL THEN 'rescheduled'
                        WHEN c.handler_job_status_code = 30 /* JobStatusCode.Paused */ THEN 'paused'
                        ELSE 'cancelled'
                    END + '"'
            END AS head
        ) h
        CROSS APPLY (
            SELECT NULLIF(CONCAT('{', STUFF(CONCAT(h.head, ',"reason":"' + c.reason + '"'), 1, 1, ''), '}'), '{}') AS details
        ) x
        WHERE
            j.event_recording_code = 20 /* EventRecordingCode.Audit */
            OR (
                j.event_recording_code = 10 /* EventRecordingCode.Failures */
                AND (
                    c.event_code IN (41 /* EventCode.JobExecutionFailed */, 44 /* EventCode.JobFailed */)
                    OR (c.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND d.prior_failure_count > 0)
                )
            )
            OR (
                jd.alert_profile_code_effective <> 0 /* AlertProfileCode.None */
                AND (
                    c.event_code IN (41 /* EventCode.JobExecutionFailed */, 44 /* EventCode.JobFailed */)
                    OR (
                        c.event_code = 40 /* EventCode.JobExecutionSucceeded */
                        AND (d.prior_failure_count > 0 OR (d.execution_number > 1 AND c.final_job_status_code IS NULL))
                    )
                )
            );

        IF @p_plain = 0
            BEGIN
            /* At Audit each shape also says what it did: rolled over or paused a recurring slot, re-armed, or held by its
               handler. A suspension lands Ready on a sleep and Suspended on a wait, so its details say where. */
            IF @shapes = 1
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    entity_code, entity_id,
                    definition_id, tenant_id, lineage_root_job_id,
                    worker_id, execution_number,
                    event_message, details_format_id, details
                )
                SELECT
                    k.event_code, @now, j.namespace_id,
                    50 /* EntityCode.Job */, d.job_id,
                    j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, d.job_id),
                    @p_leased_by_worker_id, d.execution_number,
                    c.event_message,
                    CASE WHEN x.details IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END,
                    CAST(x.details AS VARBINARY(MAX))
                FROM @done d
                INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
                INNER JOIN {{schema}}.jobs j WITH (FORCESEEK) ON j.job_id = d.job_id
                CROSS APPLY (
                    VALUES
                    (
                        1,
                        CASE
                            WHEN c.final_job_status_code IN (10 /* JobStatusCode.Ready */, 30 /* JobStatusCode.Paused */)
                                THEN CASE
                                    WHEN d.job_status_code = 10 /* JobStatusCode.Ready */ THEN 50 /* EventCode.JobRecurringRolledOver */
                                    ELSE 71 /* EventCode.JobPaused */
                                END
                        END
                    ),
                    (
                        2,
                        -- A wait whose signal had already arrived landed Ready and re-armed nothing.
                        CASE
                            WHEN c.reschedule_event_code IS NOT NULL AND NOT (c.wait_signal_name IS NOT NULL AND d.job_status_code = 10 /* JobStatusCode.Ready */)
                                THEN c.reschedule_event_code
                        END
                    ),
                    (
                        3,
                        CASE
                            WHEN c.handler_job_status_code = 220 /* JobStatusCode.Cancelled */ THEN 70 /* EventCode.JobCancelled */
                            WHEN c.handler_job_status_code = 30 /* JobStatusCode.Paused */ THEN 71 /* EventCode.JobPaused */
                        END
                    )
                ) k (ord, event_code)
                CROSS APPLY (
                    SELECT NULLIF(
                        CONCAT(
                            '{',
                            STUFF(
                                CONCAT(
                                    CASE
                                        WHEN k.event_code = 60 /* EventCode.JobSuspended */
                                            THEN CASE d.job_status_code
                                                WHEN 10 /* JobStatusCode.Ready */ THEN ',"to":"ready"'
                                                WHEN 20 /* JobStatusCode.Suspended */ THEN ',"to":"suspended"'
                                            END
                                    END,
                                    ',"reason":"' + c.reason + '"'
                                ),
                                1, 1, ''
                            ),
                            '}'
                        ),
                        '{}'
                    ) AS details
                ) x
                WHERE j.event_recording_code = 20 /* EventRecordingCode.Audit */ AND k.event_code IS NOT NULL
                ORDER BY d.job_id, k.ord;

            -- A child that landed terminal sets its latch on the parent and wakes a Suspended parent.
            IF @children = 1
                BEGIN
                    CREATE TABLE #parents (
                        parent_job_id BIGINT NOT NULL,
                        child_job_id BIGINT NOT NULL,
                        latch_name VARCHAR(128) NOT NULL,
                        envelope VARBINARY(MAX) NOT NULL,
                        reason VARCHAR(64) NULL,
                        event_message NVARCHAR(512) NULL,
                        latch_state TINYINT NULL,
                        parent_status TINYINT NULL,
                        parent_execution INT NULL,
                        -- The latches' own key order: the name sorts as text, so child 10 comes before child 9.
                        PRIMARY KEY (parent_job_id, latch_name)
                    );

                    INSERT INTO #parents (parent_job_id, child_job_id, latch_name, envelope, reason, event_message)
                    SELECT
                        d.parent_job_id,
                        d.job_id,
                        'sys.child.' + CAST(d.job_id AS VARCHAR(20)),
                        CAST(
                            CAST(
                                N'{"childJobId":' + CAST(d.job_id AS NVARCHAR(20))
                                + N',"status":' + CAST(d.job_status_code AS NVARCHAR(3))
                                + N',"executionNumber":' + CAST(d.execution_number AS NVARCHAR(11)) + N'}'
                                AS VARCHAR(MAX)
                            ) COLLATE latin1_general_100_bin2_utf8 AS VARBINARY(MAX)
                        ),
                        c.reason,
                        c.event_message
                    FROM @done d
                    INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
                    WHERE
                        d.parent_job_id IS NOT NULL
                        AND d.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

                    UPDATE p
                    SET latch_state = s.checkpoint_status_code
                    FROM #parents p
                    LEFT JOIN {{schema}}.checkpoints s WITH (UPDLOCK, FORCESEEK)
                        ON s.job_id = p.parent_job_id AND s.checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */ AND s.checkpoint_name = p.latch_name
                    OPTION (FORCE ORDER, LOOP JOIN);

                    UPDATE p
                    SET parent_status = pr.job_status_code, parent_execution = pr.execution_number
                    FROM #parents p
                    INNER JOIN {{schema}}.jobs_state pr WITH (UPDLOCK, ROWLOCK, FORCESEEK) ON pr.job_id = p.parent_job_id
                    OPTION (FORCE ORDER, LOOP JOIN);

                    /* No revival: an Expired latch already timed the parent's wait out under this same slot lock, so a later
                       child writes no latch and wakes no one, though the audit event records it. A finished parent takes
                       nothing. */
                    UPDATE s
                    SET
                        checkpoint_status_code = 20 /* CheckpointStatusCode.Set */,
                        value_format_id = 1 /* JobPayloadFormat.Json */,
                        value = p.envelope,
                        modified_at_utc = @now,
                        version = s.version + 1
                    FROM #parents p
                    INNER JOIN {{schema}}.checkpoints s WITH (FORCESEEK)
                        ON s.job_id = p.parent_job_id AND s.checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */ AND s.checkpoint_name = p.latch_name
                    WHERE
                        p.latch_state <> 30 /* CheckpointStatusCode.Expired */
                        AND p.parent_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

                    INSERT INTO {{schema}}.checkpoints (
                        job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id, value,
                        created_at_utc, modified_at_utc, version
                    )
                    SELECT
                        p.parent_job_id, 50 /* CheckpointKindCode.ChildLatch */, p.latch_name,
                        20 /* CheckpointStatusCode.Set */, 1 /* JobPayloadFormat.Json */, p.envelope,
                        @now, @now, 0
                    FROM #parents p
                    WHERE
                        p.latch_state IS NULL
                        AND p.parent_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    OPTION (LOOP JOIN);

                    -- The child's reason and message ride the parent's event, the message capped because it can already sit at the column width.
                    INSERT INTO {{schema}}.events (
                        event_code, created_at_utc, namespace_id,
                        entity_code, entity_id,
                        definition_id, tenant_id, lineage_root_job_id,
                        execution_number,
                        event_message, details_format_id, details
                    )
                    SELECT
                        80 /* EventCode.JobSignalRaised */, @now, pj.namespace_id,
                        50 /* EntityCode.Job */, p.parent_job_id,
                        pj.definition_id, pj.tenant_id, COALESCE(pj.lineage_root_job_id, p.parent_job_id),
                        p.parent_execution,
                        CASE
                            WHEN p.latch_state = 30 /* CheckpointStatusCode.Expired */
                                THEN LEFT(
                                    COALESCE(p.event_message + N' ', N'') + N'Child outcome not applied: the wait had already expired.',
                                    512
                                )
                            ELSE p.event_message
                        END,
                        CASE WHEN p.reason IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END,
                        CAST('{"reason":"' + p.reason + '"}' AS VARBINARY(MAX))
                    FROM #parents p
                    INNER JOIN {{schema}}.jobs pj ON pj.job_id = p.parent_job_id
                    WHERE
                        pj.event_recording_code = 20 /* EventRecordingCode.Audit */
                        AND p.parent_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    ORDER BY p.parent_job_id, p.child_job_id;

                    UPDATE pr
                    SET
                        job_status_code = 10 /* JobStatusCode.Ready */,
                        next_run_at_utc = @now,
                        modified_at_utc = @now,
                        version = pr.version + 1
                    FROM (
                        SELECT DISTINCT parent_job_id
                        FROM #parents
                        WHERE
                            parent_status = 20 /* JobStatusCode.Suspended */
                            AND (latch_state IS NULL OR latch_state <> 30 /* CheckpointStatusCode.Expired */)
                    ) w
                    INNER JOIN {{schema}}.jobs_state pr WITH (INDEX (pk_jobs_state), FORCESEEK, ROWLOCK) ON pr.job_id = w.parent_job_id
                    WHERE pr.job_status_code = 20 /* JobStatusCode.Suspended */;

                    INSERT INTO {{schema}}.events (
                        event_code, created_at_utc, namespace_id,
                        entity_code, entity_id,
                        definition_id, tenant_id, lineage_root_job_id,
                        execution_number,
                        details_format_id, details
                    )
                    SELECT
                        72 /* EventCode.JobResumed */, @now, pj.namespace_id,
                        50 /* EntityCode.Job */, w.parent_job_id,
                        pj.definition_id, pj.tenant_id, COALESCE(pj.lineage_root_job_id, w.parent_job_id),
                        w.parent_execution,
                        1 /* JobPayloadFormat.Json */, CAST('{"reason":"signal-released"}' AS VARBINARY(MAX))
                    FROM (
                        SELECT DISTINCT parent_job_id, parent_execution
                        FROM #parents
                        WHERE
                            parent_status = 20 /* JobStatusCode.Suspended */
                            AND (latch_state IS NULL OR latch_state <> 30 /* CheckpointStatusCode.Expired */)
                    ) w
                    INNER JOIN {{schema}}.jobs pj ON pj.job_id = w.parent_job_id
                    WHERE pj.event_recording_code = 20 /* EventRecordingCode.Audit */;

                    UPDATE d
                    SET parent_released = 1
                    FROM @done d
                    INNER JOIN #parents p ON p.child_job_id = d.job_id
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
                        job_status_code = 10 /* JobStatusCode.Ready */,
                        next_run_at_utc = CASE WHEN m.next_run_at_utc > @now THEN m.next_run_at_utc ELSE @now END,
                        modified_at_utc = @now,
                        version = m.version + 1
                    OUTPUT INSERTED.lane_id INTO #promoted (lane_id)
                    FROM (
                        SELECT DISTINCT d.lane_id
                        FROM @done d
                        WHERE
                            d.lane_id IS NOT NULL
                            AND d.job_status_code IN (
                                30 /* JobStatusCode.Paused */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */,
                                220 /* JobStatusCode.Cancelled */
                            )
                    ) settled
                    CROSS APPLY (
                        SELECT TOP (1) h.job_id, h.job_status_code
                        FROM {{schema}}.jobs_state h WITH (INDEX (ix_jobs_state_lane), FORCESEEK)
                        WHERE
                            h.lane_id = settled.lane_id
                            AND h.lane_id IS NOT NULL
                            AND h.job_status_code IN (
                                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                            )
                        ORDER BY h.job_id
                    ) head
                    INNER JOIN {{schema}}.jobs_state m WITH (INDEX (pk_jobs_state), FORCESEEK, ROWLOCK) ON m.job_id = head.job_id
                    WHERE head.job_status_code = 15 /* JobStatusCode.Blocked */ AND m.job_status_code = 15 /* JobStatusCode.Blocked */;

                    UPDATE d
                    SET lane_promoted = 1
                    FROM @done d
                    INNER JOIN #promoted p ON p.lane_id = d.lane_id;
                END
            END

        SELECT
            c.job_id,
            c.execution_number,
            CASE
                WHEN d.job_id IS NOT NULL THEN 1 /* CompleteExecutionsAction.Completed */
                WHEN
                    r.job_status_code IS NULL
                    OR r.job_status_code IN (
                        100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
                    )
                    THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
                WHEN r.leased_by_worker_id IS NULL OR r.leased_by_worker_id <> @p_leased_by_worker_id
                    THEN 2 /* CompleteExecutionsAction.NotOwner */
                ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
            END AS action,
            r.job_status_code AS final_job_status_code,
            r.next_run_at_utc AS final_next_run_at_utc,
            @now AS db_now,
            CAST(COALESCE(d.parent_released, 0) AS TINYINT) AS parent_released,
            CAST(COALESCE(d.lane_promoted, 0) AS TINYINT) AS lane_promoted
        FROM @p_batch c
        LEFT JOIN @done d ON d.job_id = c.job_id AND d.execution_number = c.execution_number
        LEFT JOIN {{schema}}.jobs_state r WITH (FORCESEEK) ON r.job_id = c.job_id;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
