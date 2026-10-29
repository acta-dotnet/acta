CREATE OR REPLACE FUNCTION {{schema}}.complete_executions(
    p_leased_by_worker_id INT,
    p_job_ids BIGINT[],
    p_execution_numbers INT[],
    p_succeeded BOOLEAN[],
    p_result_format_ids SMALLINT[],
    p_results BYTEA[],
    p_event_codes SMALLINT[],
    p_reasons VARCHAR[],
    p_event_messages VARCHAR[],
    p_duration_ms INT[],
    p_failure_counts INT[],
    p_retention_seconds INT[],
    p_reschedule_event_codes SMALLINT[],
    p_reschedule_delay_seconds INT[],
    p_reschedule_resume_at_utc TIMESTAMPTZ[],
    p_wait_signal_names VARCHAR[],
    p_handler_job_status_codes SMALLINT[],
    p_final_job_status_codes SMALLINT[],
    p_job_next_run_at_utc TIMESTAMPTZ[],
    p_plain BOOLEAN
)
RETURNS TABLE (
    job_id BIGINT,
    execution_number INT,
    action SMALLINT,
    final_job_status_code SMALLINT,
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
    v_recovered BIGINT[];
BEGIN
    -- A plain batch (the binder marks one with no shape, lane, or parent) lands, stores, audits, and answers in one
    -- statement, so it pays for one plan and one executor start rather than one per step.
    IF p_plain THEN
        RETURN QUERY
        WITH completions AS (
            SELECT *
            FROM unnest(
                p_job_ids, p_execution_numbers, p_succeeded, p_result_format_ids, p_results,
                p_event_codes, p_reasons, p_event_messages, p_duration_ms, p_failure_counts, p_retention_seconds
            ) AS c (
                job_id, execution_number, succeeded, result_format_id, result,
                event_code, reason, event_message, duration_ms, failure_count, retention_seconds
            )
        ),
        eligible AS (
            SELECT r.job_id, r.execution_number, r.failure_count AS prior_failure_count
            FROM {{schema}}.jobs_state r
            INNER JOIN completions c ON c.job_id = r.job_id AND c.execution_number = r.execution_number
            -- The rows lock by pk_jobs_state probes: "=" would imply ix_jobs_state_worker_inflight's filter and let a generic plan
            -- walk the worker's range of it, dead entries included (docs/sql-internals.md, "Locking reads seek").
            WHERE
                r.job_status_code = 50 /* JobStatusCode.Executing */
                AND r.leased_by_worker_id IS NOT DISTINCT FROM p_leased_by_worker_id
            ORDER BY r.job_id
            FOR UPDATE OF r
        ),
        done AS (
            UPDATE {{schema}}.jobs_state r
            SET
                job_status_code = CASE WHEN c.succeeded THEN 100 /* JobStatusCode.Succeeded */ ELSE 200 /* JobStatusCode.Failed */ END,
                failure_count = COALESCE(c.failure_count, r.failure_count),
                leased_by_worker_id = NULL,
                lease_expires_at_utc = NULL,
                retention_until_utc = COALESCE(now() + c.retention_seconds * INTERVAL '1 second', r.retention_until_utc),
                result_format_id = c.result_format_id,
                result = c.result,
                modified_at_utc = now(),
                version = r.version + 1
            FROM eligible e
            INNER JOIN completions c ON c.job_id = e.job_id AND c.execution_number = e.execution_number
            INNER JOIN {{schema}}.jobs j ON j.job_id = e.job_id
            WHERE r.job_id = e.job_id
            RETURNING
                r.job_id, r.execution_number, r.job_status_code, r.next_run_at_utc, e.prior_failure_count,
                c.event_code, c.reason, c.event_message, c.duration_ms, j.namespace_id, j.lineage_root_job_id, j.definition_id,
                j.tenant_id, j.event_recording_code
        ),
        -- The attempt's own event under the recording gates and the alert overlay, as in the full path below; a plain
        -- landing is never a recurring fire, so the overlay's success test is the attempt number alone.
        finished AS (
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                definition_id, tenant_id, lineage_root_job_id,
                worker_id, execution_number, duration_ms,
                event_message, details_format_id, details
            )
            SELECT
                d.event_code, now(), d.namespace_id,
                50 /* EntityCode.Job */, d.job_id,
                d.definition_id, d.tenant_id, COALESCE(d.lineage_root_job_id, d.job_id),
                p_leased_by_worker_id, d.execution_number, d.duration_ms,
                d.event_message,
                CASE WHEN d.reason IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END,
                convert_to('{"reason":"' || d.reason || '"}', 'UTF8')
            FROM done d
            INNER JOIN {{schema}}.definitions jd ON jd.definition_id = d.definition_id
            WHERE
                d.event_recording_code = 20 /* EventRecordingCode.Audit */
                OR (
                    d.event_recording_code = 10 /* EventRecordingCode.Failures */
                    AND (d.event_code = 44 /* EventCode.JobFailed */ OR (d.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND d.prior_failure_count > 0))
                )
                OR (
                    jd.alert_profile_code_effective <> 0 /* AlertProfileCode.None */
                    AND (d.event_code = 44 /* EventCode.JobFailed */ OR (d.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND d.execution_number > 1))
                )
        )
        -- jobs_state reads as it stood before the update, so a row this call completed takes its state from done.
        SELECT
            c.job_id,
            c.execution_number,
            CAST(CASE
                WHEN d.execution_number = c.execution_number THEN 1 /* CompleteExecutionsAction.Completed */
                WHEN
                    r.job_status_code IS NULL
                    OR r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                    THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
                WHEN r.leased_by_worker_id IS DISTINCT FROM p_leased_by_worker_id THEN 2 /* CompleteExecutionsAction.NotOwner */
                ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
            END AS SMALLINT),
            COALESCE(d.job_status_code, r.job_status_code),
            CASE WHEN d.job_id IS NULL THEN r.next_run_at_utc ELSE d.next_run_at_utc END,
            now(),
            CAST(0 AS SMALLINT),
            CAST(0 AS SMALLINT)
        FROM completions c
        LEFT JOIN done d ON d.job_id = c.job_id
        LEFT JOIN {{schema}}.jobs_state r ON r.job_id = c.job_id;
        RETURN;
    END IF;

    -- Each fired slot's namespace schedule lock, shared, before any other lock (docs/sql-internals.md, "Schedule lock").
    IF cardinality(array_remove(p_final_job_status_codes, NULL)) > 0 THEN
        PERFORM pg_advisory_xact_lock_shared(hashtext('{{schema}}.schedules'), n.namespace_id)
        FROM (
            SELECT DISTINCT r.namespace_id
            FROM unnest(p_job_ids, p_final_job_status_codes) AS c (job_id, final_job_status_code)
            INNER JOIN {{schema}}.jobs_state r ON r.job_id = c.job_id
            WHERE c.final_job_status_code IS NOT NULL
            ORDER BY r.namespace_id
        ) n;
    END IF;

    /* Every completion takes the same steps, each skipped when no row needs it. Locks are then taken lanes first, then
       awaited signal slots, own jobs_state rows, schedules, parent latches, and parent jobs_state rows, each in key order
       (same policy, "Lane lock order"). lane_id and parent_job_id never change. */
    PERFORM 1
    FROM {{schema}}.lanes l
    WHERE l.lane_id IN (SELECT r.lane_id FROM {{schema}}.jobs_state r WHERE r.job_id = ANY (p_job_ids))
    ORDER BY l.lane_id
    FOR UPDATE OF l;

    -- The awaited slot is the only place a wait's deadline lives; the lock lets a raise that won the race land the job Ready.
    IF cardinality(array_remove(p_wait_signal_names, NULL)) > 0 THEN
        PERFORM 1
        FROM {{schema}}.checkpoints s
        INNER JOIN unnest(p_job_ids, p_wait_signal_names) AS w (job_id, wait_signal_name)
            ON s.job_id = w.job_id AND s.checkpoint_name = w.wait_signal_name
        WHERE s.checkpoint_kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
        ORDER BY s.job_id
        FOR UPDATE OF s;
    END IF;

    WITH completions AS (
        SELECT *
        FROM unnest(
            p_job_ids, p_execution_numbers, p_succeeded, p_result_format_ids, p_results, p_failure_counts, p_retention_seconds,
            p_reschedule_event_codes, p_reschedule_delay_seconds, p_reschedule_resume_at_utc, p_wait_signal_names,
            p_handler_job_status_codes, p_final_job_status_codes, p_job_next_run_at_utc
        ) AS c (
            job_id, execution_number, succeeded, result_format_id, result, failure_count, retention_seconds,
            reschedule_event_code, reschedule_delay_seconds, reschedule_resume_at_utc, wait_signal_name,
            handler_job_status_code, final_job_status_code, job_next_run_at_utc
        )
    ),
    -- The jobs_state rows lock in job_id order by pk_jobs_state probes, the worker test written as in the plain path.
    eligible AS (
        SELECT
            r.job_id,
            r.execution_number,
            r.failure_count AS prior_failure_count,
            r.lane_id,
            j.parent_job_id,
            s.checkpoint_status_code AS sig_state,
            s.due_at_utc AS sig_due
        FROM {{schema}}.jobs_state r
        INNER JOIN completions c ON c.job_id = r.job_id AND c.execution_number = r.execution_number
        INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
        LEFT JOIN {{schema}}.checkpoints s
            ON
                c.wait_signal_name IS NOT NULL
                AND s.job_id = r.job_id
                AND s.checkpoint_kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
                AND s.checkpoint_name = c.wait_signal_name
        WHERE
            r.job_status_code = 50 /* JobStatusCode.Executing */
            AND r.leased_by_worker_id IS NOT DISTINCT FROM p_leased_by_worker_id
        ORDER BY r.job_id
        FOR UPDATE OF r
    ),
    done AS (
        UPDATE {{schema}}.jobs_state r
        SET
            job_status_code = t.to_status,
            next_run_at_utc = CASE
                WHEN c.wait_signal_name IS NOT NULL AND e.sig_state = 20 /* CheckpointStatusCode.Set */ THEN now()
                WHEN c.wait_signal_name IS NOT NULL THEN e.sig_due
                WHEN c.reschedule_event_code IS NOT NULL
                    THEN COALESCE(c.reschedule_resume_at_utc, now() + make_interval(secs => c.reschedule_delay_seconds))
                -- A handler's pause keeps a next run, which marks it held (ScheduleWalker, "held").
                WHEN c.handler_job_status_code = 30 /* JobStatusCode.Paused */ THEN now()
                WHEN c.handler_job_status_code IS NOT NULL THEN NULL
                WHEN c.final_job_status_code IS NOT NULL THEN c.job_next_run_at_utc
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
            result_format_id = c.result_format_id,
            result = c.result,
            modified_at_utc = now(),
            version = r.version + 1
        FROM eligible e
        INNER JOIN completions c ON c.job_id = e.job_id AND c.execution_number = e.execution_number
        CROSS JOIN LATERAL (
            SELECT CASE
                WHEN c.wait_signal_name IS NOT NULL AND e.sig_state = 20 /* CheckpointStatusCode.Set */ THEN 10 /* JobStatusCode.Ready */
                WHEN c.wait_signal_name IS NOT NULL THEN 20 /* JobStatusCode.Suspended */
                WHEN c.reschedule_event_code IS NOT NULL THEN 10 /* JobStatusCode.Ready */
                WHEN c.handler_job_status_code IS NOT NULL THEN c.handler_job_status_code
                WHEN c.final_job_status_code IS NOT NULL THEN c.final_job_status_code
                WHEN c.succeeded THEN 100 /* JobStatusCode.Succeeded */
                ELSE 200 /* JobStatusCode.Failed */
            END AS to_status
        ) t
        WHERE r.job_id = e.job_id
        RETURNING r.job_id, r.execution_number, r.job_status_code, e.lane_id, e.parent_job_id, e.prior_failure_count
    )
    SELECT
        array_agg(d.job_id),
        -- Only a child that landed terminal touches its parent, so only its parent is locked.
        array_agg(d.job_id) FILTER (
            WHERE
                d.parent_job_id IS NOT NULL
                AND d.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        ),
        array_agg(d.job_id) FILTER (WHERE d.lane_id IS NOT NULL),
        -- A success is a recovery when the job had counted failures before this landing reset them.
        array_agg(d.job_id) FILTER (WHERE d.prior_failure_count > 0)
    INTO v_done, v_children, v_laned, v_recovered
    FROM done d;

    IF cardinality(array_remove(p_final_job_status_codes, NULL)) > 0 THEN
        /* A permanent slot that landed Idle, Failed, or Cancelled settled its firing. Every member schedule parked on
           the slot is woken to evaluate again, and the envelope records the outcome once. The cursors stay where the
           admission left them: completion never computes an occurrence. */
        UPDATE {{schema}}.schedules_state st
        SET
            next_evaluation_at_utc = now(),
            evaluation_status_code = 10 /* EvaluationStatusCode.Scheduled */,
            modified_at_utc = now(),
            version = st.version + 1
        FROM {{schema}}.schedules js
        INNER JOIN unnest(p_job_ids, p_final_job_status_codes) AS c (job_id, final_job_status_code) ON c.job_id = js.job_id
        WHERE
            st.schedule_id = js.schedule_id
            AND c.job_id = ANY (v_done)
            AND c.final_job_status_code IN (40 /* JobStatusCode.Idle */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            AND st.evaluation_status_code IN (20 /* EvaluationStatusCode.WaitingForCapacity */, 50 /* EvaluationStatusCode.SlotUnavailable */);

        UPDATE {{schema}}.checkpoints cp
        SET
            checkpoint_status_code = CASE
                WHEN c.final_job_status_code = 220 /* JobStatusCode.Cancelled */ THEN 130 /* CheckpointStatusCode.Cancelled */
                WHEN c.final_job_status_code = 200 /* JobStatusCode.Failed */ OR NOT c.succeeded THEN 120 /* CheckpointStatusCode.Failed */
                ELSE 110 /* CheckpointStatusCode.Succeeded */
            END,
            modified_at_utc = now(),
            version = cp.version + 1
        FROM unnest(p_job_ids, p_succeeded, p_final_job_status_codes) AS c (job_id, succeeded, final_job_status_code)
        INNER JOIN {{schema}}.jobs j ON j.job_id = c.job_id
        WHERE
            cp.job_id = c.job_id
            AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
            AND cp.checkpoint_name = 'sys.firing'
            AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
            AND c.job_id = ANY (v_done)
            AND c.final_job_status_code IN (40 /* JobStatusCode.Idle */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */;
    END IF;

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
        c.event_code, now(), j.namespace_id,
        50 /* EntityCode.Job */, r.job_id,
        j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, r.job_id),
        p_leased_by_worker_id, r.execution_number, c.duration_ms,
        c.event_message,
        CASE WHEN x.details IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END,
        convert_to(x.details, 'UTF8')
    FROM unnest(
        p_job_ids, p_execution_numbers, p_event_codes, p_reasons, p_event_messages, p_duration_ms,
        p_reschedule_event_codes, p_handler_job_status_codes, p_final_job_status_codes
    ) AS c (
        job_id, execution_number, event_code, reason, event_message, duration_ms,
        reschedule_event_code, handler_job_status_code, final_job_status_code
    )
    INNER JOIN {{schema}}.jobs_state r ON r.job_id = c.job_id AND r.execution_number = c.execution_number
    INNER JOIN {{schema}}.jobs j ON j.job_id = c.job_id
    INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
    CROSS JOIN LATERAL (
        SELECT CASE
            WHEN c.event_code = 41 /* EventCode.JobExecutionFailed */
                THEN ',"to":"' || CASE r.job_status_code
                    WHEN 10 /* JobStatusCode.Ready */ THEN 'ready'
                    WHEN 20 /* JobStatusCode.Suspended */ THEN 'suspended'
                    WHEN 30 /* JobStatusCode.Paused */ THEN 'paused'
                    WHEN 40 /* JobStatusCode.Idle */ THEN 'idle'
                END || '"'
            WHEN c.event_code = 43 /* EventCode.JobExecutionYielded */
                THEN ',"outcome":"' || CASE
                    WHEN c.reschedule_event_code = 60 /* EventCode.JobSuspended */ THEN 'suspended'
                    WHEN c.reschedule_event_code IS NOT NULL THEN 'rescheduled'
                    WHEN c.handler_job_status_code = 30 /* JobStatusCode.Paused */ THEN 'paused'
                    ELSE 'cancelled'
                END || '"'
        END AS head
    ) h
    CROSS JOIN LATERAL (
        SELECT NULLIF('{' || substr(concat(h.head, ',"reason":"' || c.reason || '"'), 2) || '}', '{}') AS details
    ) x
    WHERE
        c.job_id = ANY (v_done)
        AND (
            j.event_recording_code = 20 /* EventRecordingCode.Audit */
            OR (
                j.event_recording_code = 10 /* EventRecordingCode.Failures */
                AND (
                    c.event_code IN (41 /* EventCode.JobExecutionFailed */, 44 /* EventCode.JobFailed */)
                    OR (c.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND c.job_id = ANY (v_recovered))
                )
            )
            OR (
                jd.alert_profile_code_effective <> 0 /* AlertProfileCode.None */
                AND (
                    c.event_code IN (41 /* EventCode.JobExecutionFailed */, 44 /* EventCode.JobFailed */)
                    OR (
                        c.event_code = 40 /* EventCode.JobExecutionSucceeded */
                        AND (c.job_id = ANY (v_recovered) OR (c.execution_number > 1 AND c.final_job_status_code IS NULL))
                    )
                )
            )
        );

    /* At Audit each shape also says what it did: held or cancelled a slot, re-armed, or held by its handler. A suspension lands Ready on a sleep and Suspended on a wait, so its details say where. */
    IF
        cardinality(array_remove(p_reschedule_event_codes, NULL)) > 0
        OR cardinality(array_remove(p_handler_job_status_codes, NULL)) > 0
        OR cardinality(array_remove(p_final_job_status_codes, NULL)) > 0 THEN
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            entity_code, entity_id,
            definition_id, tenant_id, lineage_root_job_id,
            worker_id, execution_number,
            event_message, details_format_id, details
        )
        SELECT
            k.event_code, now(), j.namespace_id,
            50 /* EntityCode.Job */, r.job_id,
            j.definition_id, j.tenant_id, COALESCE(j.lineage_root_job_id, r.job_id),
            p_leased_by_worker_id, r.execution_number,
            c.event_message,
            CASE WHEN x.details IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END,
            convert_to(x.details, 'UTF8')
        FROM unnest(
            p_job_ids, p_execution_numbers, p_reasons, p_event_messages,
            p_reschedule_event_codes, p_wait_signal_names, p_handler_job_status_codes, p_final_job_status_codes
        ) AS c (job_id, execution_number, reason, event_message, reschedule_event_code, wait_signal_name, handler_job_status_code, final_job_status_code)
        INNER JOIN {{schema}}.jobs_state r ON r.job_id = c.job_id AND r.execution_number = c.execution_number
        INNER JOIN {{schema}}.jobs j ON j.job_id = c.job_id
        CROSS JOIN LATERAL (
            VALUES
            (
                1,
                -- A slot held by its handler, or cancelled by it; a settled firing needs no shape event beside the attempt's.
                CASE c.final_job_status_code
                    WHEN 30 /* JobStatusCode.Paused */ THEN 71 /* EventCode.JobPaused */
                    WHEN 220 /* JobStatusCode.Cancelled */ THEN 70 /* EventCode.JobCancelled */
                END
            ),
            (
                2,
                -- A wait whose signal had already arrived landed Ready and re-armed nothing.
                CASE
                    WHEN c.reschedule_event_code IS NOT NULL AND NOT (c.wait_signal_name IS NOT NULL AND r.job_status_code = 10 /* JobStatusCode.Ready */)
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
        ) AS k (ord, event_code)
        CROSS JOIN LATERAL (
            SELECT NULLIF(
                '{'
                || substr(
                    concat(
                        CASE
                            WHEN k.event_code = 60 /* EventCode.JobSuspended */
                                THEN CASE r.job_status_code
                                    WHEN 10 /* JobStatusCode.Ready */ THEN ',"to":"ready"'
                                    WHEN 20 /* JobStatusCode.Suspended */ THEN ',"to":"suspended"'
                                END
                        END,
                        ',"reason":"' || c.reason || '"'
                    ),
                    2
                )
                || '}',
                '{}'
            ) AS details
        ) x
        WHERE c.job_id = ANY (v_done) AND j.event_recording_code = 20 /* EventRecordingCode.Audit */ AND k.event_code IS NOT NULL
        ORDER BY r.job_id, k.ord;
    END IF;

    -- A child that landed terminal sets its latch on the parent and wakes a Suspended parent.
    IF v_children IS NOT NULL THEN
        PERFORM 1
        FROM {{schema}}.checkpoints s
        INNER JOIN {{schema}}.jobs j ON s.job_id = j.parent_job_id AND s.checkpoint_name = 'sys.child.' || j.job_id::text
        WHERE j.job_id = ANY (v_children) AND s.checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
        ORDER BY s.job_id, s.checkpoint_name
        FOR UPDATE OF s;

        PERFORM 1
        FROM {{schema}}.jobs_state pr
        WHERE pr.job_id IN (SELECT j.parent_job_id FROM {{schema}}.jobs j WHERE j.job_id = ANY (v_children))
        ORDER BY pr.job_id
        FOR UPDATE OF pr;

        /* No revival: an Expired latch already resolved the parent's wait TimedOut under this same slot lock, so a
           child landing afterwards writes no latch and releases no parent. The audit event still records that it
           happened. A parent already finished takes nothing. */
        WITH settled AS (
            SELECT
                j.parent_job_id,
                r.job_id AS child_job_id,
                r.job_status_code AS child_status,
                r.execution_number AS child_execution,
                c.reason,
                c.event_message,
                COALESCE(l.checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */, FALSE) AS latch_expired,
                pr.job_status_code AS parent_status,
                pr.execution_number AS parent_execution,
                pj.namespace_id,
                pj.lineage_root_job_id,
                pj.definition_id,
                pj.tenant_id,
                pj.event_recording_code
            FROM unnest(p_job_ids, p_execution_numbers, p_reasons, p_event_messages)
                AS c (job_id, execution_number, reason, event_message)
            INNER JOIN {{schema}}.jobs_state r ON r.job_id = c.job_id AND r.execution_number = c.execution_number
            INNER JOIN {{schema}}.jobs j ON j.job_id = c.job_id
            INNER JOIN {{schema}}.jobs_state pr ON pr.job_id = j.parent_job_id
            INNER JOIN {{schema}}.jobs pj ON pj.job_id = j.parent_job_id
            LEFT JOIN {{schema}}.checkpoints l
                ON
                    l.job_id = j.parent_job_id
                    AND l.checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
                    AND l.checkpoint_name = 'sys.child.' || r.job_id::text
            WHERE
                c.job_id = ANY (v_children)
                AND r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                AND pr.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        ),
        latched AS (
            INSERT INTO {{schema}}.checkpoints (
                job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id, value,
                created_at_utc, modified_at_utc, version
            )
            SELECT
                s.parent_job_id,
                50 /* CheckpointKindCode.ChildLatch */,
                'sys.child.' || s.child_job_id::text,
                20 /* CheckpointStatusCode.Set */,
                1 /* JobPayloadFormat.Json */,
                convert_to(
                    json_build_object('childJobId', s.child_job_id, 'status', s.child_status, 'executionNumber', s.child_execution)::text,
                    'UTF8'
                ),
                now(),
                now(),
                0
            FROM settled s
            WHERE NOT s.latch_expired
            ON CONFLICT ON CONSTRAINT pk_checkpoints DO UPDATE SET
                checkpoint_status_code = 20 /* CheckpointStatusCode.Set */,
                value_format_id = EXCLUDED.value_format_id,
                value = EXCLUDED.value,
                modified_at_utc = now(),
                version = {{schema}}.checkpoints.version + 1
        ),
        -- The child's reason and message ride the parent's event, the message capped because it can already sit at the column width.
        raised AS (
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                definition_id, tenant_id, lineage_root_job_id,
                execution_number,
                event_message, details_format_id, details
            )
            SELECT
                80 /* EventCode.JobSignalRaised */, now(), s.namespace_id,
                50 /* EntityCode.Job */, s.parent_job_id,
                s.definition_id, s.tenant_id, COALESCE(s.lineage_root_job_id, s.parent_job_id),
                s.parent_execution,
                CASE
                    WHEN s.latch_expired
                        THEN LEFT(COALESCE(s.event_message || ' ', '') || 'Child outcome not applied: the wait had already expired.', 512)
                    ELSE s.event_message
                END,
                CASE WHEN s.reason IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END,
                convert_to('{"reason":"' || s.reason || '"}', 'UTF8')
            FROM settled s
            WHERE s.event_recording_code = 20 /* EventRecordingCode.Audit */
            ORDER BY s.parent_job_id, s.child_job_id
        ),
        woken AS (
            UPDATE {{schema}}.jobs_state pr
            SET
                job_status_code = 10 /* JobStatusCode.Ready */,
                next_run_at_utc = now(),
                modified_at_utc = now(),
                version = pr.version + 1
            WHERE
                pr.job_id IN (SELECT s.parent_job_id FROM settled s WHERE s.parent_status = 20 /* JobStatusCode.Suspended */ AND NOT s.latch_expired)
                AND pr.job_status_code = 20 /* JobStatusCode.Suspended */
            RETURNING pr.job_id
        ),
        resumed AS (
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                definition_id, tenant_id, lineage_root_job_id,
                execution_number,
                details_format_id, details
            )
            SELECT
                72 /* EventCode.JobResumed */, now(), w.namespace_id,
                50 /* EntityCode.Job */, w.parent_job_id,
                w.definition_id, w.tenant_id, COALESCE(w.lineage_root_job_id, w.parent_job_id),
                w.parent_execution,
                1 /* JobPayloadFormat.Json */, convert_to('{"reason":"signal-released"}', 'UTF8')
            FROM (
                SELECT DISTINCT s.parent_job_id, s.parent_execution, s.namespace_id, s.lineage_root_job_id, s.definition_id, s.tenant_id
                FROM settled s
                WHERE
                    s.parent_status = 20 /* JobStatusCode.Suspended */
                    AND NOT s.latch_expired
                    AND s.event_recording_code = 20 /* EventRecordingCode.Audit */
            ) w
        )
        SELECT array_agg(s.child_job_id) INTO v_released
        FROM settled s
        WHERE s.parent_status = 20 /* JobStatusCode.Suspended */ AND NOT s.latch_expired;
    END IF;

    /* A laned job that finished, or that its handler paused, leaves the lane with no runner: the lowest-id
       unfinished member becomes Ready at its own due instant or now when it is Blocked, and any other head keeps
       the lane. The lane lock taken above makes the read and the write one step. */
    IF v_laned IS NOT NULL THEN
        WITH promoted AS (
            UPDATE {{schema}}.jobs_state m
            SET
                job_status_code = 10 /* JobStatusCode.Ready */,
                next_run_at_utc = GREATEST(m.next_run_at_utc, now()),
                modified_at_utc = now(),
                version = m.version + 1
            FROM (
                SELECT DISTINCT r.lane_id
                FROM {{schema}}.jobs_state r
                WHERE
                    r.job_id = ANY (v_laned)
                    AND r.job_status_code IN (
                        30 /* JobStatusCode.Paused */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
                    )
            ) settled
            CROSS JOIN LATERAL (
                SELECT h.job_id, h.job_status_code
                FROM {{schema}}.jobs_state h
                WHERE
                    h.lane_id = settled.lane_id
                    AND h.lane_id IS NOT NULL
                    AND h.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                    )
                ORDER BY h.job_id
                LIMIT 1
            ) head
            WHERE m.job_id = head.job_id AND head.job_status_code = 15 /* JobStatusCode.Blocked */ AND m.job_status_code = 15 /* JobStatusCode.Blocked */
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
                r.job_status_code IS NULL
                OR r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
            WHEN r.leased_by_worker_id IS DISTINCT FROM p_leased_by_worker_id THEN 2 /* CompleteExecutionsAction.NotOwner */
            ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
        END AS SMALLINT),
        r.job_status_code,
        r.next_run_at_utc,
        now(),
        CAST(CASE WHEN c.job_id = ANY (v_released) THEN 1 ELSE 0 END AS SMALLINT),
        CAST(CASE WHEN c.job_id = ANY (v_done) AND r.lane_id = ANY (v_promoted) THEN 1 ELSE 0 END AS SMALLINT)
    FROM unnest(p_job_ids, p_execution_numbers) AS c (job_id, execution_number)
    LEFT JOIN {{schema}}.jobs_state r ON r.job_id = c.job_id;
END;
$$;
