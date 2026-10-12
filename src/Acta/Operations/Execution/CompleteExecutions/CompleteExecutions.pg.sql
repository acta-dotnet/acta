CREATE OR REPLACE FUNCTION {{schema}}.complete_executions(
    p_leased_by_worker_id INT,
    p_job_ids BIGINT[],
    p_execution_numbers INT[],
    p_succeeded BOOLEAN[],
    p_result_format_ids SMALLINT[],
    p_results BYTEA[],
    p_reason_codes SMALLINT[],
    p_reason_messages VARCHAR[],
    p_duration_ms INT[],
    p_failure_counts INT[],
    p_retention_seconds INT[],
    p_reschedule_status_codes SMALLINT[],
    p_reschedule_delay_seconds INT[],
    p_reschedule_resume_at_utc TIMESTAMPTZ[],
    p_wait_signal_names VARCHAR[],
    p_handler_status_codes SMALLINT[],
    p_final_statuses SMALLINT[],
    p_job_next_run_at_utc TIMESTAMPTZ[],
    p_recurring_result_caps INT[],
    p_advance_schedule_ids BIGINT[],
    p_advance_next_runs TIMESTAMPTZ[],
    p_advance_versions INT[],
    p_plain BOOLEAN
)
RETURNS TABLE (
    job_id BIGINT,
    execution_number INT,
    action SMALLINT,
    final_status_code SMALLINT,
    final_next_run_at_utc TIMESTAMPTZ,
    db_now TIMESTAMPTZ,
    parent_released SMALLINT,
    lane_promoted SMALLINT
)
LANGUAGE plpgsql
AS $$
#variable_conflict use_column
DECLARE
    v_done BIGINT[];
    v_children BIGINT[];
    v_laned BIGINT[];
    v_released BIGINT[];
    v_promoted BIGINT[];
BEGIN
    -- A plain batch (the binder marks one with no shape, lane, or parent) lands, stores, audits, and answers in one
    -- statement, so it pays for one plan and one executor start rather than one per step.
    IF p_plain THEN
        RETURN QUERY
        WITH completions AS (
            SELECT *
            FROM unnest(
                p_job_ids, p_execution_numbers, p_succeeded, p_result_format_ids, p_results,
                p_reason_codes, p_reason_messages, p_duration_ms, p_failure_counts, p_retention_seconds
            ) AS c (
                job_id, execution_number, succeeded, result_format_id, result,
                reason_code, reason_message, duration_ms, failure_count, retention_seconds
            )
        ),
        eligible AS (
            SELECT r.job_id, r.execution_number
            FROM {{schema}}.runtimes r
            INNER JOIN completions c ON c.job_id = r.job_id AND c.execution_number = r.execution_number
            WHERE r.status_code = 50 /* JobStatusCode.Executing */ AND r.leased_by_worker_id = p_leased_by_worker_id
            ORDER BY r.job_id
            FOR UPDATE OF r
        ),
        done AS (
            UPDATE {{schema}}.runtimes r
            SET
                status_code = CASE WHEN c.succeeded THEN 100 /* JobStatusCode.Succeeded */ ELSE 200 /* JobStatusCode.Failed */ END,
                failure_count = COALESCE(c.failure_count, r.failure_count),
                leased_by_worker_id = NULL,
                lease_expires_at_utc = NULL,
                retention_until_utc = COALESCE(now() + c.retention_seconds * INTERVAL '1 second', r.retention_until_utc),
                modified_at_utc = now(),
                version = r.version + 1
            FROM eligible e
            INNER JOIN completions c ON c.job_id = e.job_id AND c.execution_number = e.execution_number
            INNER JOIN {{schema}}.jobs j ON j.id = e.job_id
            WHERE r.job_id = e.job_id
            RETURNING
                r.job_id, r.execution_number, r.status_code, r.next_run_at_utc, c.succeeded, c.result_format_id, c.result,
                c.reason_code, c.reason_message, c.duration_ms, j.namespace_id, j.lineage_root_id, j.definition_id,
                j.tenant_id, j.audit_level_code, j.job_ref
        ),
        stored AS (
            INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
            SELECT d.job_id, d.execution_number, d.result_format_id, d.result, now()
            FROM done d
            WHERE d.result_format_id <> 0 /* JobPayloadFormat.None */
        ),
        finished AS (
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
                41 /* EventCode.JobExecutionFinished */, now(), d.namespace_id,
                70 /* ActorCode.Worker */, NULL,
                d.job_id, d.job_ref, d.execution_number,
                COALESCE(d.lineage_root_id, d.job_id), d.definition_id, d.tenant_id,
                p_leased_by_worker_id,
                50 /* JobStatusCode.Executing */, d.status_code,
                CASE WHEN d.succeeded THEN 100 /* ExecutionStatusCode.Succeeded */ ELSE 200 /* ExecutionStatusCode.Failed */ END,
                d.duration_ms,
                d.reason_code, d.reason_message
            FROM done d
            WHERE
                d.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
                OR (
                    d.audit_level_code = 10 /* DefinitionAuditLevelCode.Failures */
                    AND (
                        NOT d.succeeded
                        OR COALESCE((
                            SELECT e.execution_status_code
                            FROM {{schema}}.events e
                            WHERE e.job_id = d.job_id AND e.event_code = 41 /* EventCode.JobExecutionFinished */
                            ORDER BY e.created_at_utc DESC, e.id DESC
                            LIMIT 1), 100) <> 100 /* ExecutionStatusCode.Succeeded */
                    )
                )
        )
        -- runtimes reads as it stood before the update, so a row this call completed takes its state from done.
        SELECT
            c.job_id,
            c.execution_number,
            CAST(CASE
                WHEN d.execution_number = c.execution_number THEN 1 /* CompleteExecutionsAction.Completed */
                WHEN
                    r.status_code IS NULL
                    OR r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
                WHEN r.leased_by_worker_id IS DISTINCT FROM p_leased_by_worker_id THEN 2 /* CompleteExecutionsAction.NotOwner */
                ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
            END AS SMALLINT),
            COALESCE(d.status_code, r.status_code),
            CASE WHEN d.job_id IS NULL THEN r.next_run_at_utc ELSE d.next_run_at_utc END,
            now(),
            CAST(0 AS SMALLINT),
            CAST(0 AS SMALLINT)
        FROM completions c
        LEFT JOIN done d ON d.job_id = c.job_id
        LEFT JOIN {{schema}}.runtimes r ON r.job_id = c.job_id;
        RETURN;
    END IF;

    /* Every completion takes the same steps, each skipped when no row needs it. Locks are taken lanes first, then
       awaited signal slots, own runtimes, schedules, parent latches, and parent runtimes, each in key order
       (docs/internals/sql-execution-policy.md, "Lane lock order"). lane_id and parent_id never change. */
    PERFORM 1
    FROM {{schema}}.lanes l
    WHERE l.id IN (SELECT r.lane_id FROM {{schema}}.runtimes r WHERE r.job_id = ANY (p_job_ids))
    ORDER BY l.id
    FOR UPDATE OF l;

    -- The awaited slot is the only place a wait's deadline lives; the lock lets a raise that won the race land the job Ready.
    IF cardinality(array_remove(p_wait_signal_names, NULL)) > 0 THEN
        PERFORM 1
        FROM {{schema}}.checkpoints s
        INNER JOIN unnest(p_job_ids, p_wait_signal_names) AS w (job_id, name) ON s.job_id = w.job_id AND s.name = w.name
        WHERE s.kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
        ORDER BY s.job_id
        FOR UPDATE OF s;
    END IF;

    WITH completions AS (
        SELECT *
        FROM unnest(
            p_job_ids, p_execution_numbers, p_succeeded, p_result_format_ids, p_results, p_failure_counts, p_retention_seconds,
            p_reschedule_status_codes, p_reschedule_delay_seconds, p_reschedule_resume_at_utc, p_wait_signal_names,
            p_handler_status_codes, p_final_statuses, p_job_next_run_at_utc
        ) AS c (
            job_id, execution_number, succeeded, result_format_id, result, failure_count, retention_seconds,
            reschedule_status_code, reschedule_delay_seconds, reschedule_resume_at_utc, wait_signal_name,
            handler_status_code, final_status, job_next_run_at_utc
        )
    ),
    -- The runtimes rows lock in job_id order.
    eligible AS (
        SELECT r.job_id, r.execution_number, r.lane_id, j.parent_id, s.status_code AS sig_state, s.due_at_utc AS sig_due
        FROM {{schema}}.runtimes r
        INNER JOIN completions c ON c.job_id = r.job_id AND c.execution_number = r.execution_number
        INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
        LEFT JOIN {{schema}}.checkpoints s
            ON
                c.wait_signal_name IS NOT NULL
                AND s.job_id = r.job_id
                AND s.kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
                AND s.name = c.wait_signal_name
        WHERE
            r.status_code = 50 /* JobStatusCode.Executing */
            AND r.leased_by_worker_id = p_leased_by_worker_id
        ORDER BY r.job_id
        FOR UPDATE OF r
    ),
    done AS (
        UPDATE {{schema}}.runtimes r
        SET
            status_code = t.to_status,
            next_run_at_utc = CASE
                WHEN c.wait_signal_name IS NOT NULL AND e.sig_state = 20 /* CheckpointStatusCode.Set */ THEN now()
                WHEN c.wait_signal_name IS NOT NULL THEN e.sig_due
                WHEN c.reschedule_status_code IS NOT NULL
                    THEN COALESCE(c.reschedule_resume_at_utc, now() + make_interval(secs => c.reschedule_delay_seconds))
                -- A handler's pause keeps a next run, which marks it held (ScheduleWalker, "held").
                WHEN c.handler_status_code = 30 /* JobStatusCode.Paused */ THEN now()
                WHEN c.handler_status_code IS NOT NULL THEN NULL
                WHEN c.final_status IS NOT NULL THEN c.job_next_run_at_utc
                ELSE r.next_run_at_utc
            END,
            failure_count = COALESCE(c.failure_count, r.failure_count),
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = CASE
                WHEN
                    t.to_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    AND c.retention_seconds IS NOT NULL
                    THEN now() + c.retention_seconds * INTERVAL '1 second'
                ELSE r.retention_until_utc
            END,
            modified_at_utc = now(),
            version = r.version + 1
        FROM eligible e
        INNER JOIN completions c ON c.job_id = e.job_id AND c.execution_number = e.execution_number
        CROSS JOIN LATERAL (
            SELECT CASE
                WHEN c.wait_signal_name IS NOT NULL AND e.sig_state = 20 /* CheckpointStatusCode.Set */ THEN 10 /* JobStatusCode.Ready */
                WHEN c.wait_signal_name IS NOT NULL THEN 20 /* JobStatusCode.Suspended */
                WHEN c.reschedule_status_code IS NOT NULL THEN 10 /* JobStatusCode.Ready */
                WHEN c.handler_status_code IS NOT NULL THEN c.handler_status_code
                WHEN c.final_status IS NOT NULL THEN c.final_status
                WHEN c.succeeded THEN 100 /* JobStatusCode.Succeeded */
                ELSE 200 /* JobStatusCode.Failed */
            END AS to_status
        ) t
        WHERE r.job_id = e.job_id
        RETURNING r.job_id, r.execution_number, r.status_code, e.lane_id, e.parent_id, c.result_format_id, c.result
    ),
    stored AS (
        INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
        SELECT d.job_id, d.execution_number, d.result_format_id, d.result, now()
        FROM done d
        WHERE d.result_format_id <> 0 /* JobPayloadFormat.None */
    )
    SELECT
        array_agg(d.job_id),
        -- Only a child that landed terminal touches its parent, so only its parent is locked.
        array_agg(d.job_id) FILTER (
            WHERE
                d.parent_id IS NOT NULL
                AND d.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        ),
        array_agg(d.job_id) FILTER (WHERE d.lane_id IS NOT NULL)
    INTO v_done, v_children, v_laned
    FROM done d;

    IF cardinality(array_remove(p_final_statuses, NULL)) > 0 THEN
        /* A recurring fire advances its schedules. An edit made while the attempt ran wins over the older plan, an
           operator pause wins unless it was a timed pause that has elapsed, and a schedule orphaned by a deployment
           is left alone. The audit insert shares the update's predicate, so the two agree. */
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
            102 /* EventCode.SchedulePauseExpired */, now(), j.namespace_id,
            10 /* ActorCode.Sys */, NULL,
            j.id, j.job_ref, r.execution_number,
            COALESCE(j.lineage_root_id, j.id), j.definition_id, j.tenant_id,
            NULL,
            NULL, NULL,
            NULL, NULL,
            NULL, js.name
        FROM unnest(p_advance_schedule_ids, p_advance_versions) AS adv (schedule_id, expected_version)
        INNER JOIN {{schema}}.schedules js ON js.id = adv.schedule_id
        INNER JOIN {{schema}}.jobs j ON j.id = js.job_id
        INNER JOIN {{schema}}.runtimes r ON r.job_id = js.job_id
        WHERE
            js.job_id = ANY (v_done)
            AND (adv.expected_version IS NULL OR js.version = adv.expected_version)
            AND j.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
            AND js.status_code = 30 /* ScheduleStatusCode.Paused */
            AND js.paused_until_utc IS NOT NULL
            AND js.paused_until_utc <= now();

        UPDATE {{schema}}.schedules js
        SET
            next_run_at_utc = adv.next_run,
            last_occurrence_at_utc = js.next_run_at_utc,
            status_code = 10 /* ScheduleStatusCode.Active */,
            paused_until_utc = NULL,
            modified_at_utc = now(),
            version = js.version + 1
        FROM unnest(p_advance_schedule_ids, p_advance_next_runs, p_advance_versions) AS adv (schedule_id, next_run, expected_version)
        WHERE
            js.id = adv.schedule_id
            AND js.job_id = ANY (v_done)
            AND (adv.expected_version IS NULL OR js.version = adv.expected_version)
            AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
            AND (
                js.status_code <> 30 /* ScheduleStatusCode.Paused */
                OR (js.paused_until_utc IS NOT NULL AND js.paused_until_utc <= now())
            );

        -- The slot's next state is read from the schedules as they stand now, not from the plan made before the handler ran.
        UPDATE {{schema}}.runtimes r
        SET
            status_code = CASE WHEN w.wake IS NULL THEN 30 /* JobStatusCode.Paused */ ELSE 10 /* JobStatusCode.Ready */ END,
            next_run_at_utc = w.wake
        FROM unnest(p_job_ids, p_execution_numbers, p_final_statuses) AS c (job_id, execution_number, final_status)
        CROSS JOIN LATERAL (
            SELECT MIN(CASE WHEN js.status_code = 30 /* ScheduleStatusCode.Paused */ THEN js.paused_until_utc ELSE js.next_run_at_utc END) AS wake
            FROM {{schema}}.schedules js
            WHERE js.job_id = c.job_id AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
        ) w
        WHERE r.job_id = c.job_id AND r.execution_number = c.execution_number AND c.final_status IS NOT NULL AND c.job_id = ANY (v_done);

        DELETE FROM {{schema}}.results res
        USING (
            SELECT
                k.job_id,
                k.execution_number,
                c.cap,
                ROW_NUMBER() OVER (PARTITION BY k.job_id ORDER BY k.execution_number DESC) AS newest
            FROM unnest(p_job_ids, p_execution_numbers, p_final_statuses, p_recurring_result_caps) AS c (job_id, execution_number, final_status, cap)
            -- Only the row that completed: a stale execution of the same job in the batch would count each result twice.
            INNER JOIN {{schema}}.runtimes r ON r.job_id = c.job_id AND r.execution_number = c.execution_number
            INNER JOIN {{schema}}.results k ON k.job_id = c.job_id
            WHERE c.final_status IS NOT NULL AND c.cap > 0 AND c.job_id = ANY (v_done)
        ) old
        WHERE res.job_id = old.job_id AND res.execution_number = old.execution_number AND old.newest > old.cap;
    END IF;

    /* At Failures a failure carrying no re-arm or handler hold is written, and a success only when it answers a
       recorded failure, which closes the incident: the job's newest finished event is not a success. One seek on the
       timeline index per row. */
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
        41 /* EventCode.JobExecutionFinished */, now(), j.namespace_id,
        70 /* ActorCode.Worker */, NULL,
        r.job_id, j.job_ref, r.execution_number,
        COALESCE(j.lineage_root_id, r.job_id), j.definition_id, j.tenant_id,
        p_leased_by_worker_id,
        50 /* JobStatusCode.Executing */, r.status_code,
        CASE
            WHEN c.reschedule_status_code IS NOT NULL THEN c.reschedule_status_code
            WHEN c.handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 220 /* ExecutionStatusCode.Cancelled */
            WHEN c.handler_status_code = 30 /* JobStatusCode.Paused */ THEN 152 /* ExecutionStatusCode.Paused */
            WHEN c.succeeded THEN 100 /* ExecutionStatusCode.Succeeded */
            ELSE 200 /* ExecutionStatusCode.Failed */
        END,
        c.duration_ms,
        c.reason_code, c.reason_message
    FROM unnest(
        p_job_ids, p_execution_numbers, p_succeeded, p_reason_codes, p_reason_messages, p_duration_ms,
        p_reschedule_status_codes, p_handler_status_codes
    ) AS c (job_id, execution_number, succeeded, reason_code, reason_message, duration_ms, reschedule_status_code, handler_status_code)
    INNER JOIN {{schema}}.runtimes r ON r.job_id = c.job_id AND r.execution_number = c.execution_number
    INNER JOIN {{schema}}.jobs j ON j.id = c.job_id
    WHERE
        c.job_id = ANY (v_done)
        AND (
            j.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
            OR (
                j.audit_level_code = 10 /* DefinitionAuditLevelCode.Failures */
                AND c.reschedule_status_code IS NULL
                AND (c.handler_status_code IS NULL OR c.handler_status_code NOT IN (220 /* JobStatusCode.Cancelled */, 30 /* JobStatusCode.Paused */))
                AND (
                    NOT c.succeeded
                    OR COALESCE((
                        SELECT e.execution_status_code
                        FROM {{schema}}.events e
                        WHERE e.job_id = c.job_id AND e.event_code = 41 /* EventCode.JobExecutionFinished */
                        ORDER BY e.created_at_utc DESC, e.id DESC
                        LIMIT 1), 100) <> 100 /* ExecutionStatusCode.Succeeded */
                )
            )
        );

    -- At Audit each shape also says what it did: rolled over or paused a recurring slot, re-armed, or held by its handler.
    IF
        cardinality(array_remove(p_reschedule_status_codes, NULL)) > 0
        OR cardinality(array_remove(p_handler_status_codes, NULL)) > 0
        OR cardinality(array_remove(p_final_statuses, NULL)) > 0 THEN
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
            k.event_code, now(), j.namespace_id,
            70 /* ActorCode.Worker */, NULL,
            r.job_id, j.job_ref, r.execution_number,
            COALESCE(j.lineage_root_id, r.job_id), j.definition_id, j.tenant_id,
            p_leased_by_worker_id,
            50 /* JobStatusCode.Executing */, r.status_code,
            NULL, NULL,
            c.reason_code, c.reason_message
        FROM unnest(
            p_job_ids, p_execution_numbers, p_reason_codes, p_reason_messages,
            p_reschedule_status_codes, p_wait_signal_names, p_handler_status_codes, p_final_statuses
        ) AS c (job_id, execution_number, reason_code, reason_message, reschedule_status_code, wait_signal_name, handler_status_code, final_status)
        INNER JOIN {{schema}}.runtimes r ON r.job_id = c.job_id AND r.execution_number = c.execution_number
        INNER JOIN {{schema}}.jobs j ON j.id = c.job_id
        CROSS JOIN LATERAL (
            VALUES
            (
                1,
                CASE
                    WHEN c.final_status IN (10 /* JobStatusCode.Ready */, 30 /* JobStatusCode.Paused */)
                        THEN CASE
                            WHEN r.status_code = 10 /* JobStatusCode.Ready */ THEN 50 /* EventCode.JobRecurringRolledOver */
                            ELSE 71 /* EventCode.JobPaused */
                        END
                END
            ),
            (
                2,
                -- A wait whose signal had already arrived landed Ready and re-armed nothing.
                CASE
                    WHEN c.reschedule_status_code IS NOT NULL AND NOT (c.wait_signal_name IS NOT NULL AND r.status_code = 10 /* JobStatusCode.Ready */)
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
        ) AS k (ord, event_code)
        WHERE c.job_id = ANY (v_done) AND j.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */ AND k.event_code IS NOT NULL
        ORDER BY r.job_id, k.ord;
    END IF;

    -- A child that landed terminal sets its latch on the parent and wakes a Suspended parent.
    IF v_children IS NOT NULL THEN
        PERFORM 1
        FROM {{schema}}.checkpoints s
        INNER JOIN {{schema}}.jobs j ON s.job_id = j.parent_id AND s.name = 'sys.child.' || j.id::text
        WHERE j.id = ANY (v_children) AND s.kind_code = 50 /* CheckpointKindCode.ChildLatch */
        ORDER BY s.job_id, s.name
        FOR UPDATE OF s;

        PERFORM 1
        FROM {{schema}}.runtimes pr
        WHERE pr.job_id IN (SELECT j.parent_id FROM {{schema}}.jobs j WHERE j.id = ANY (v_children))
        ORDER BY pr.job_id
        FOR UPDATE OF pr;

        /* No revival: an Expired latch already resolved the parent's wait TimedOut under this same slot lock, so a
           child landing afterwards writes no latch and releases no parent. The audit event still records that it
           happened. A parent already finished takes nothing. */
        WITH settled AS (
            SELECT
                j.parent_id,
                r.job_id AS child_id,
                r.status_code AS child_status,
                c.reason_code,
                c.reason_message,
                COALESCE(l.status_code = 30 /* CheckpointStatusCode.Expired */, FALSE) AS latch_expired,
                pr.status_code AS parent_status,
                pr.execution_number AS parent_execution,
                pj.namespace_id,
                pj.lineage_root_id,
                pj.definition_id,
                pj.tenant_id,
                pj.audit_level_code,
                pj.job_ref
            FROM unnest(p_job_ids, p_execution_numbers, p_reason_codes, p_reason_messages)
                AS c (job_id, execution_number, reason_code, reason_message)
            INNER JOIN {{schema}}.runtimes r ON r.job_id = c.job_id AND r.execution_number = c.execution_number
            INNER JOIN {{schema}}.jobs j ON j.id = c.job_id
            INNER JOIN {{schema}}.runtimes pr ON pr.job_id = j.parent_id
            INNER JOIN {{schema}}.jobs pj ON pj.id = j.parent_id
            LEFT JOIN {{schema}}.checkpoints l
                ON
                    l.job_id = j.parent_id
                    AND l.kind_code = 50 /* CheckpointKindCode.ChildLatch */
                    AND l.name = 'sys.child.' || r.job_id::text
            WHERE
                c.job_id = ANY (v_children)
                AND r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                AND pr.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        ),
        latched AS (
            INSERT INTO {{schema}}.checkpoints (job_id, kind_code, name, status_code, value_format_id, value, created_at_utc, modified_at_utc, version)
            SELECT
                s.parent_id,
                50 /* CheckpointKindCode.ChildLatch */,
                'sys.child.' || s.child_id::text,
                20 /* CheckpointStatusCode.Set */,
                1 /* JobPayloadFormat.Json */,
                convert_to(json_build_object('childJobId', s.child_id, 'status', s.child_status)::text, 'UTF8'),
                now(),
                now(),
                0
            FROM settled s
            WHERE NOT s.latch_expired
            ON CONFLICT (job_id, kind_code, name) DO UPDATE SET
                status_code = 20 /* CheckpointStatusCode.Set */,
                value_format_id = EXCLUDED.value_format_id,
                value = EXCLUDED.value,
                modified_at_utc = now(),
                version = {{schema}}.checkpoints.version + 1
        ),
        -- Capped because the caller's message can already sit at the column width.
        raised AS (
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
                80 /* EventCode.JobSignalRaised */, now(), s.namespace_id,
                10 /* ActorCode.Sys */, NULL,
                s.parent_id, s.job_ref, s.parent_execution,
                COALESCE(s.lineage_root_id, s.parent_id), s.definition_id, s.tenant_id,
                NULL,
                s.parent_status, s.parent_status,
                NULL, NULL,
                s.reason_code,
                CASE
                    WHEN s.latch_expired
                        THEN LEFT(COALESCE(s.reason_message || ' ', '') || 'Child outcome not applied: the wait had already expired.', 512)
                    ELSE s.reason_message
                END
            FROM settled s
            WHERE s.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
            ORDER BY s.parent_id, s.child_id
        ),
        woken AS (
            UPDATE {{schema}}.runtimes pr
            SET
                status_code = 10 /* JobStatusCode.Ready */,
                next_run_at_utc = now(),
                modified_at_utc = now(),
                version = pr.version + 1
            WHERE
                pr.job_id IN (SELECT s.parent_id FROM settled s WHERE s.parent_status = 20 /* JobStatusCode.Suspended */ AND NOT s.latch_expired)
                AND pr.status_code = 20 /* JobStatusCode.Suspended */
            RETURNING pr.job_id
        ),
        resumed AS (
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
                72 /* EventCode.JobResumed */, now(), w.namespace_id,
                10 /* ActorCode.Sys */, NULL,
                w.parent_id, w.job_ref, w.parent_execution,
                COALESCE(w.lineage_root_id, w.parent_id), w.definition_id, w.tenant_id,
                NULL,
                20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */,
                NULL, NULL,
                60 /* EventReasonCode.JobSignalReleased */, NULL
            FROM (
                SELECT DISTINCT s.parent_id, s.parent_execution, s.namespace_id, s.lineage_root_id, s.definition_id, s.tenant_id, s.job_ref
                FROM settled s
                WHERE
                    s.parent_status = 20 /* JobStatusCode.Suspended */
                    AND NOT s.latch_expired
                    AND s.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
            ) w
        )
        SELECT array_agg(s.child_id) INTO v_released
        FROM settled s
        WHERE s.parent_status = 20 /* JobStatusCode.Suspended */ AND NOT s.latch_expired;
    END IF;

    /* A laned job that finished, or that its handler paused, leaves the lane with no runner: the lowest-id
       unfinished member becomes Ready at its own due instant or now when it is Blocked, and any other head keeps
       the lane. The lane lock taken above makes the read and the write one step. */
    IF v_laned IS NOT NULL THEN
        WITH promoted AS (
            UPDATE {{schema}}.runtimes m
            SET
                status_code = 10 /* JobStatusCode.Ready */,
                next_run_at_utc = GREATEST(m.next_run_at_utc, now()),
                modified_at_utc = now(),
                version = m.version + 1
            FROM (
                SELECT DISTINCT r.lane_id
                FROM {{schema}}.runtimes r
                WHERE
                    r.job_id = ANY (v_laned)
                    AND r.status_code IN (
                        30 /* JobStatusCode.Paused */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
                    )
            ) settled
            CROSS JOIN LATERAL (
                SELECT h.job_id, h.status_code
                FROM {{schema}}.runtimes h
                WHERE
                    h.lane_id = settled.lane_id
                    AND h.lane_id IS NOT NULL
                    AND h.status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                    )
                ORDER BY h.job_id
                LIMIT 1
            ) head
            WHERE m.job_id = head.job_id AND head.status_code = 15 /* JobStatusCode.Blocked */ AND m.status_code = 15 /* JobStatusCode.Blocked */
            RETURNING m.lane_id
        )
        SELECT array_agg(p.lane_id) INTO v_promoted FROM promoted p;
    END IF;

    RETURN QUERY
    SELECT
        c.job_id,
        c.execution_number,
        CAST(CASE
            WHEN c.job_id = ANY (v_done) AND r.execution_number = c.execution_number THEN 1 /* CompleteExecutionsAction.Completed */
            WHEN
                r.status_code IS NULL
                OR r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
            WHEN r.leased_by_worker_id IS DISTINCT FROM p_leased_by_worker_id THEN 2 /* CompleteExecutionsAction.NotOwner */
            ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
        END AS SMALLINT),
        r.status_code,
        r.next_run_at_utc,
        now(),
        CAST(CASE WHEN c.job_id = ANY (v_released) THEN 1 ELSE 0 END AS SMALLINT),
        CAST(CASE WHEN c.job_id = ANY (v_done) AND r.lane_id = ANY (v_promoted) THEN 1 ELSE 0 END AS SMALLINT)
    FROM unnest(p_job_ids, p_execution_numbers) AS c (job_id, execution_number)
    LEFT JOIN {{schema}}.runtimes r ON r.job_id = c.job_id;
END;
$$;
