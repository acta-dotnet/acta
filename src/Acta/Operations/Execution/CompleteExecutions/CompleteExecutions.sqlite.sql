DROP TABLE IF EXISTS temp._ces_done;

/* SqliteDialect.BindCompleteExecutions cuts each section no row of the batch needs, since SQLite prepares every
   statement on each call. The immediate transaction is the one writer. The completions arrive as JSON, each result
   as base64, and an empty result is stored as a zero-length blob. */
CREATE TEMP TABLE _ces_done AS
SELECT
    r.job_id,
    r.execution_number,
    r.lane_id,
    j.parent_id,
    j.namespace_id,
    j.lineage_root_id,
    j.definition_id,
    j.tenant_id,
    j.audit_level_code,
    j.job_ref,
    json_extract(c.value, '$.succeeded') AS succeeded,
    json_extract(c.value, '$.result_format_id') AS result_format_id,
    COALESCE(acta_blob(json_extract(c.value, '$.result')), X'') AS result,
    json_extract(c.value, '$.reason_code') AS reason_code,
    json_extract(c.value, '$.reason_message') AS reason_message,
    json_extract(c.value, '$.duration_ms') AS duration_ms,
    json_extract(c.value, '$.failure_count') AS failure_count,
    json_extract(c.value, '$.retention_seconds') AS retention_seconds,
    json_extract(c.value, '$.reschedule_status_code') AS reschedule_status_code,
    json_extract(c.value, '$.reschedule_delay_seconds') AS reschedule_delay_seconds,
    json_extract(c.value, '$.reschedule_resume_at_utc') AS reschedule_resume_at_utc,
    json_extract(c.value, '$.wait_signal_name') AS wait_signal_name,
    json_extract(c.value, '$.handler_status_code') AS handler_status_code,
    json_extract(c.value, '$.final_status') AS final_status,
    json_extract(c.value, '$.job_next_run_at_utc') AS job_next_run_at_utc,
    json_extract(c.value, '$.recurring_result_cap') AS recurring_result_cap,
    CASE
        WHEN json_extract(c.value, '$.wait_signal_name') IS NOT NULL THEN 20 /* JobStatusCode.Suspended */
        WHEN json_extract(c.value, '$.reschedule_status_code') IS NOT NULL THEN 10 /* JobStatusCode.Ready */
        WHEN json_extract(c.value, '$.handler_status_code') IS NOT NULL THEN json_extract(c.value, '$.handler_status_code')
        WHEN json_extract(c.value, '$.final_status') IS NOT NULL THEN json_extract(c.value, '$.final_status')
        WHEN json_extract(c.value, '$.succeeded') = 1 THEN 100 /* JobStatusCode.Succeeded */
        ELSE 200 /* JobStatusCode.Failed */
    END AS to_status,
    NULL AS sig_state,
    NULL AS sig_due,
    NULL AS slot_wake,
    NULL AS parent_status,
    NULL AS latch_state,
    NULL AS head_id
FROM jsonb_each(@p_completions) c
INNER JOIN {{schema}}.runtimes r ON r.job_id = json_extract(c.value, '$.job_id')
INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
WHERE
    r.execution_number = json_extract(c.value, '$.execution_number')
    AND r.status_code = 50 /* JobStatusCode.Executing */
    AND r.leased_by_worker_id = @p_leased_by_worker_id;

-- begin waits
-- The awaited slot is the only place a wait's deadline lives, and a raise that won the race lands the job Ready.
UPDATE temp._ces_done
SET
    sig_state = (
        SELECT s.status_code FROM {{schema}}.checkpoints s
        WHERE
            s.job_id = temp._ces_done.job_id
            AND s.kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
            AND s.name = temp._ces_done.wait_signal_name
    ),
    sig_due = (
        SELECT s.due_at_utc FROM {{schema}}.checkpoints s
        WHERE
            s.job_id = temp._ces_done.job_id
            AND s.kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
            AND s.name = temp._ces_done.wait_signal_name
    )
WHERE wait_signal_name IS NOT NULL;

UPDATE temp._ces_done
SET to_status = 10 /* JobStatusCode.Ready */
WHERE wait_signal_name IS NOT NULL AND sig_state = 20 /* CheckpointStatusCode.Set */;
-- end waits

-- begin shapes
-- The next run goes first, while the row is still Executing: a timer consumed this attempt left it NULL, and
-- ck_runtimes_ready_due holds a Ready row to one.
UPDATE {{schema}}.runtimes AS r
SET next_run_at_utc = CASE
    WHEN d.wait_signal_name IS NOT NULL AND d.sig_state = 20 /* CheckpointStatusCode.Set */ THEN {{now}}
    WHEN d.wait_signal_name IS NOT NULL THEN d.sig_due
    WHEN d.reschedule_status_code IS NOT NULL THEN COALESCE(d.reschedule_resume_at_utc, {{now}} + d.reschedule_delay_seconds * 1000)
    -- A handler's pause keeps a next run, which marks it held (ScheduleWalker, "held").
    WHEN d.handler_status_code = 30 /* JobStatusCode.Paused */ THEN {{now}}
    WHEN d.handler_status_code IS NOT NULL THEN NULL
    ELSE d.job_next_run_at_utc
END
FROM temp._ces_done d
WHERE
    r.job_id = d.job_id
    AND (d.reschedule_status_code IS NOT NULL OR d.handler_status_code IS NOT NULL OR d.final_status IS NOT NULL);
-- end shapes

UPDATE {{schema}}.runtimes AS r
SET
    status_code = d.to_status,
    failure_count = COALESCE(d.failure_count, r.failure_count),
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = CASE
        WHEN d.to_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN COALESCE({{now}} + d.retention_seconds * 1000, r.retention_until_utc)
        ELSE r.retention_until_utc
    END,
    modified_at_utc = {{now}},
    version = r.version + 1
FROM temp._ces_done d
WHERE r.job_id = d.job_id;

INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
SELECT d.job_id, d.execution_number, d.result_format_id, d.result, {{now}}
FROM temp._ces_done d
WHERE d.result_format_id <> 0 /* JobPayloadFormat.None */;

-- begin recurring
/* A fire advances its schedules: an edit made while it ran wins over the older plan, an operator pause wins unless it
   was timed and has elapsed, and an orphaned schedule is left alone. The audit insert shares the update's predicate. */
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, worker_id, from_status_code, to_status_code,
    execution_status_code, duration_ms, reason_code, reason_message)
SELECT
    102 /* EventCode.SchedulePauseExpired */, {{now}}, d.namespace_id, 10 /* ActorCode.Sys */, NULL,
    d.job_id, d.job_ref, d.execution_number, COALESCE(d.lineage_root_id, d.job_id), d.definition_id, d.tenant_id,
    NULL, NULL, NULL, NULL, NULL, NULL, js.name
FROM json_each(@p_schedule_advances) a
INNER JOIN {{schema}}.schedules js ON js.id = json_extract(a.value, '$.schedule_id')
INNER JOIN temp._ces_done d ON d.job_id = js.job_id
WHERE
    (json_extract(a.value, '$.expected_version') IS NULL OR json_extract(a.value, '$.expected_version') = js.version)
    AND d.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
    AND js.status_code = 30 /* ScheduleStatusCode.Paused */
    AND js.paused_until_utc IS NOT NULL
    AND js.paused_until_utc <= {{now}};

UPDATE {{schema}}.schedules AS js
SET
    next_run_at_utc = json_extract(a.value, '$.next_run_at_utc'),
    last_occurrence_at_utc = js.next_run_at_utc,
    status_code = 10 /* ScheduleStatusCode.Active */,
    paused_until_utc = NULL,
    modified_at_utc = {{now}},
    version = js.version + 1
FROM json_each(@p_schedule_advances) a
WHERE
    js.id = json_extract(a.value, '$.schedule_id')
    AND js.job_id IN (SELECT d.job_id FROM temp._ces_done d)
    AND (json_extract(a.value, '$.expected_version') IS NULL OR json_extract(a.value, '$.expected_version') = js.version)
    AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND (
        js.status_code <> 30 /* ScheduleStatusCode.Paused */
        OR (js.paused_until_utc IS NOT NULL AND js.paused_until_utc <= {{now}})
    );

-- The slot's next state is read from the schedules as they stand now, not from the plan made before the handler ran.
UPDATE temp._ces_done
SET slot_wake = (
    SELECT MIN(CASE WHEN js.status_code = 30 /* ScheduleStatusCode.Paused */ THEN js.paused_until_utc ELSE js.next_run_at_utc END)
    FROM {{schema}}.schedules js
    WHERE js.job_id = temp._ces_done.job_id AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
)
WHERE final_status IS NOT NULL;

UPDATE temp._ces_done
SET to_status = CASE WHEN slot_wake IS NULL THEN 30 /* JobStatusCode.Paused */ ELSE 10 /* JobStatusCode.Ready */ END
WHERE final_status IS NOT NULL;

UPDATE {{schema}}.runtimes AS r
SET status_code = d.to_status, next_run_at_utc = d.slot_wake
FROM temp._ces_done d
WHERE r.job_id = d.job_id AND d.final_status IS NOT NULL;

DELETE FROM {{schema}}.results
WHERE
    (job_id, execution_number) IN (
        SELECT k.job_id, k.execution_number
        FROM (
            SELECT
                res.job_id,
                res.execution_number,
                d.recurring_result_cap AS cap,
                ROW_NUMBER() OVER (PARTITION BY res.job_id ORDER BY res.execution_number DESC) AS newest
            FROM temp._ces_done d
            INNER JOIN {{schema}}.results res ON res.job_id = d.job_id
            WHERE d.final_status IS NOT NULL AND d.recurring_result_cap > 0
        ) k
        WHERE k.newest > k.cap
    );
-- end recurring

/* At Failures a failure carrying no re-arm or handler hold is written, and a success only when it answers a recorded
   failure, which closes the incident: the job's newest finished event is not a success. One seek on the timeline
   index per row. */
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, worker_id, from_status_code, to_status_code,
    execution_status_code, duration_ms, reason_code, reason_message)
SELECT
    41 /* EventCode.JobExecutionFinished */, {{now}}, d.namespace_id, 70 /* ActorCode.Worker */, NULL,
    d.job_id, d.job_ref, d.execution_number, COALESCE(d.lineage_root_id, d.job_id), d.definition_id, d.tenant_id,
    @p_leased_by_worker_id, 50 /* JobStatusCode.Executing */, d.to_status,
    CASE
        WHEN d.reschedule_status_code IS NOT NULL THEN d.reschedule_status_code
        WHEN d.handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 220 /* ExecutionStatusCode.Cancelled */
        WHEN d.handler_status_code = 30 /* JobStatusCode.Paused */ THEN 152 /* ExecutionStatusCode.Paused */
        WHEN d.succeeded = 1 THEN 100 /* ExecutionStatusCode.Succeeded */
        ELSE 200 /* ExecutionStatusCode.Failed */
    END,
    d.duration_ms, d.reason_code, d.reason_message
FROM temp._ces_done d
WHERE
    d.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
    OR (
        d.audit_level_code = 10 /* DefinitionAuditLevelCode.Failures */
        AND d.reschedule_status_code IS NULL
        AND (d.handler_status_code IS NULL OR d.handler_status_code NOT IN (220 /* JobStatusCode.Cancelled */, 30 /* JobStatusCode.Paused */))
        AND (
            d.succeeded = 0
            OR COALESCE((
                SELECT e.execution_status_code
                FROM {{schema}}.events e
                WHERE e.job_id = d.job_id AND e.event_code = 41 /* EventCode.JobExecutionFinished */
                ORDER BY e.created_at_utc DESC, e.id DESC
                LIMIT 1), 100) <> 100 /* ExecutionStatusCode.Succeeded */
        )
    );

-- begin shapes
-- At Audit each shape also says what it did: rolled over or paused a recurring slot, re-armed, or held by its handler.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, worker_id, from_status_code, to_status_code,
    execution_status_code, duration_ms, reason_code, reason_message)
SELECT
    k.event_code, {{now}}, k.namespace_id, 70 /* ActorCode.Worker */, NULL,
    k.job_id, k.job_ref, k.execution_number, COALESCE(k.lineage_root_id, k.job_id), k.definition_id, k.tenant_id,
    @p_leased_by_worker_id, 50 /* JobStatusCode.Executing */, k.to_status, NULL, NULL, k.reason_code, k.reason_message
FROM (
    SELECT
        d.*,
        n.ord,
        CASE n.ord
            WHEN 1 THEN CASE
                WHEN d.final_status IN (10 /* JobStatusCode.Ready */, 30 /* JobStatusCode.Paused */)
                    THEN CASE WHEN d.to_status = 10 /* JobStatusCode.Ready */ THEN 50 /* EventCode.JobRecurringRolledOver */ ELSE 71 /* EventCode.JobPaused */ END
            END
            -- A wait whose signal had already arrived landed Ready and re-armed nothing.
            WHEN 2 THEN CASE
                WHEN d.reschedule_status_code IS NOT NULL AND NOT (d.wait_signal_name IS NOT NULL AND d.to_status = 10 /* JobStatusCode.Ready */)
                    THEN CASE WHEN d.reschedule_status_code = 151 /* ExecutionStatusCode.Suspended */ THEN 60 /* EventCode.JobSuspended */ ELSE 61 /* EventCode.JobRescheduled */ END
            END
            ELSE CASE
                WHEN d.handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 70 /* EventCode.JobCancelled */
                WHEN d.handler_status_code = 30 /* JobStatusCode.Paused */ THEN 71 /* EventCode.JobPaused */
            END
        END AS event_code
    FROM temp._ces_done d
    CROSS JOIN (SELECT 1 AS ord UNION ALL SELECT 2 UNION ALL SELECT 3) n
    WHERE d.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
) k
WHERE k.event_code IS NOT NULL
ORDER BY k.job_id, k.ord;
-- end shapes

-- begin children
/* A terminal child sets its latch on the parent and wakes a Suspended parent. An Expired latch already timed the wait
   out, so a later child writes no latch and wakes no one, though the audit event records it; a finished parent takes nothing. */
UPDATE temp._ces_done
SET
    parent_status = (SELECT pr.status_code FROM {{schema}}.runtimes pr WHERE pr.job_id = temp._ces_done.parent_id),
    latch_state = (
        SELECT l.status_code FROM {{schema}}.checkpoints l
        WHERE
            l.job_id = temp._ces_done.parent_id
            AND l.kind_code = 50 /* CheckpointKindCode.ChildLatch */
            AND l.name = 'sys.child.' || temp._ces_done.job_id
    )
WHERE
    parent_id IS NOT NULL
    AND to_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

INSERT INTO {{schema}}.checkpoints (job_id, kind_code, name, status_code, value_format_id, value, modified_at_utc, version)
SELECT
    d.parent_id,
    50 /* CheckpointKindCode.ChildLatch */,
    'sys.child.' || d.job_id,
    20 /* CheckpointStatusCode.Set */,
    1 /* JobPayloadFormat.Json */,
    CAST(json_object('childJobId', d.job_id, 'status', d.to_status) AS BLOB),
    {{now}},
    0
FROM temp._ces_done d
WHERE
    d.parent_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND (d.latch_state IS NULL OR d.latch_state <> 30 /* CheckpointStatusCode.Expired */)
ON CONFLICT (job_id, kind_code, name) DO UPDATE SET
    status_code = 20 /* CheckpointStatusCode.Set */,
    value_format_id = excluded.value_format_id,
    value = excluded.value,
    modified_at_utc = {{now}},
    version = {{schema}}.checkpoints.version + 1;

-- The resumed event goes once per parent; the raised event once per child, its message capped at the column width.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, worker_id, from_status_code, to_status_code,
    execution_status_code, duration_ms, reason_code, reason_message)
SELECT
    80 /* EventCode.JobSignalRaised */, {{now}}, pj.namespace_id, 10 /* ActorCode.Sys */, NULL,
    d.parent_id, pj.job_ref, pr.execution_number, COALESCE(pj.lineage_root_id, d.parent_id), pj.definition_id, pj.tenant_id,
    NULL, d.parent_status, d.parent_status, NULL, NULL, d.reason_code,
    CASE
        WHEN d.latch_state = 30 /* CheckpointStatusCode.Expired */
            THEN substr(COALESCE(d.reason_message || ' ', '') || 'Child outcome not applied: the wait had already expired.', 1, 512)
        ELSE d.reason_message
    END
FROM temp._ces_done d
INNER JOIN {{schema}}.jobs pj ON pj.id = d.parent_id
INNER JOIN {{schema}}.runtimes pr ON pr.job_id = d.parent_id
WHERE
    d.parent_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND pj.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
UNION ALL
SELECT DISTINCT
    72 /* EventCode.JobResumed */, {{now}}, pj.namespace_id, 10 /* ActorCode.Sys */, NULL,
    d.parent_id, pj.job_ref, pr.execution_number, COALESCE(pj.lineage_root_id, d.parent_id), pj.definition_id, pj.tenant_id,
    NULL, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, NULL, NULL, 60 /* EventReasonCode.JobSignalReleased */, NULL
FROM temp._ces_done d
INNER JOIN {{schema}}.jobs pj ON pj.id = d.parent_id
INNER JOIN {{schema}}.runtimes pr ON pr.job_id = d.parent_id
WHERE
    d.parent_status = 20 /* JobStatusCode.Suspended */
    AND (d.latch_state IS NULL OR d.latch_state <> 30 /* CheckpointStatusCode.Expired */)
    AND pj.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */;

UPDATE {{schema}}.runtimes
SET
    status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = {{now}},
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    status_code = 20 /* JobStatusCode.Suspended */
    AND job_id IN (
        SELECT d.parent_id
        FROM temp._ces_done d
        WHERE
            d.parent_status = 20 /* JobStatusCode.Suspended */
            AND (d.latch_state IS NULL OR d.latch_state <> 30 /* CheckpointStatusCode.Expired */)
    );
-- end children

-- begin lanes
/* A laned job that finished, or that its handler paused, leaves the lane with no runner: the lowest-id unfinished
   member becomes Ready at its own due instant or now when it is Blocked, and any other head keeps the lane. */
UPDATE temp._ces_done
SET head_id = (
    SELECT CASE WHEN h.status_code = 15 /* JobStatusCode.Blocked */ THEN h.job_id END
    FROM {{schema}}.runtimes h
    WHERE
        h.job_id = (
            SELECT m.job_id
            FROM {{schema}}.runtimes m
            WHERE
                m.lane_id = temp._ces_done.lane_id
                AND m.status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1
        )
)
WHERE
    lane_id IS NOT NULL
    AND to_status IN (30 /* JobStatusCode.Paused */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

UPDATE {{schema}}.runtimes
SET
    status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id IN (SELECT d.head_id FROM temp._ces_done d);
-- end lanes

SELECT
    json_extract(c.value, '$.job_id') AS job_id,
    json_extract(c.value, '$.execution_number') AS execution_number,
    CASE
        WHEN d.job_id IS NOT NULL THEN 1 /* CompleteExecutionsAction.Completed */
        WHEN
            r.status_code IS NULL
            OR r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
        WHEN r.leased_by_worker_id IS NULL OR r.leased_by_worker_id <> @p_leased_by_worker_id
            THEN 2 /* CompleteExecutionsAction.NotOwner */
        ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
    END AS action,
    r.status_code AS final_status_code,
    r.next_run_at_utc AS final_next_run_at_utc,
    {{now}} AS db_now,
    CASE
        WHEN d.parent_status = 20 /* JobStatusCode.Suspended */ AND (d.latch_state IS NULL OR d.latch_state <> 30 /* CheckpointStatusCode.Expired */)
            THEN 1
        ELSE 0
    END AS parent_released,
    CASE WHEN d.head_id IS NOT NULL THEN 1 ELSE 0 END AS lane_promoted
FROM jsonb_each(@p_completions) c
LEFT JOIN temp._ces_done d
    ON d.job_id = json_extract(c.value, '$.job_id') AND d.execution_number = json_extract(c.value, '$.execution_number')
LEFT JOIN {{schema}}.runtimes r ON r.job_id = json_extract(c.value, '$.job_id');
