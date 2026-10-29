DROP TABLE IF EXISTS temp._ces_done;

/* SqliteDialect.BindCompleteExecutions cuts each section no row of the batch needs, since SQLite prepares every
   statement on each call. The immediate transaction is the one writer. The completions arrive as JSON, each result
   as base64, and a result with a format and no bytes is stored as a zero-length blob. */
CREATE TEMP TABLE _ces_done AS
SELECT
    r.job_id,
    r.execution_number,
    r.lane_id,
    j.parent_job_id,
    j.namespace_id,
    j.lineage_root_job_id,
    j.definition_id,
    j.tenant_id,
    j.event_recording_code,
    jd.alert_profile_code_effective,
    r.failure_count AS prior_failure_count,
    json_extract(c.value, '$.succeeded') AS succeeded,
    json_extract(c.value, '$.result_format_id') AS result_format_id,
    CASE
        WHEN json_extract(c.value, '$.result_format_id') <> 0 /* JobPayloadFormat.None */
            THEN COALESCE(acta_blob(json_extract(c.value, '$.result')), X'')
    END AS result,
    json_extract(c.value, '$.event_code') AS event_code,
    json_extract(c.value, '$.reason') AS reason,
    json_extract(c.value, '$.event_message') AS event_message,
    json_extract(c.value, '$.duration_ms') AS duration_ms,
    json_extract(c.value, '$.failure_count') AS failure_count,
    json_extract(c.value, '$.retention_seconds') AS retention_seconds,
    json_extract(c.value, '$.reschedule_event_code') AS reschedule_event_code,
    json_extract(c.value, '$.reschedule_delay_seconds') AS reschedule_delay_seconds,
    json_extract(c.value, '$.reschedule_resume_at_utc') AS reschedule_resume_at_utc,
    json_extract(c.value, '$.wait_signal_name') AS wait_signal_name,
    json_extract(c.value, '$.handler_job_status_code') AS handler_job_status_code,
    json_extract(c.value, '$.final_job_status_code') AS final_job_status_code,
    json_extract(c.value, '$.job_next_run_at_utc') AS job_next_run_at_utc,
    CASE
        WHEN json_extract(c.value, '$.wait_signal_name') IS NOT NULL THEN 20 /* JobStatusCode.Suspended */
        WHEN json_extract(c.value, '$.reschedule_event_code') IS NOT NULL THEN 10 /* JobStatusCode.Ready */
        WHEN json_extract(c.value, '$.handler_job_status_code') IS NOT NULL THEN json_extract(c.value, '$.handler_job_status_code')
        WHEN json_extract(c.value, '$.final_job_status_code') IS NOT NULL THEN json_extract(c.value, '$.final_job_status_code')
        WHEN json_extract(c.value, '$.succeeded') = 1 THEN 100 /* JobStatusCode.Succeeded */
        ELSE 200 /* JobStatusCode.Failed */
    END AS to_status,
    -- One instant for the whole completion, as PostgreSQL's now() and SQL Server's @now give the server providers:
    -- SQLite holds now steady only within a statement, and the script stamps a row across several.
    {{now}} AS now_utc,
    NULL AS sig_state,
    NULL AS sig_due,
    NULL AS parent_status,
    NULL AS latch_state,
    NULL AS head_id
FROM jsonb_each(@p_completions) c
INNER JOIN {{schema}}.jobs_state r ON r.job_id = json_extract(c.value, '$.job_id')
INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
WHERE
    r.execution_number = json_extract(c.value, '$.execution_number')
    AND r.job_status_code = 50 /* JobStatusCode.Executing */
    AND r.leased_by_worker_id = @p_leased_by_worker_id;

-- begin waits
-- The awaited slot is the only place a wait's deadline lives, and a raise that won the race lands the job Ready.
UPDATE temp._ces_done
SET
    sig_state = (
        SELECT s.checkpoint_status_code FROM {{schema}}.checkpoints s
        WHERE
            s.job_id = temp._ces_done.job_id
            AND s.checkpoint_kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
            AND s.checkpoint_name = temp._ces_done.wait_signal_name
    ),
    sig_due = (
        SELECT s.due_at_utc FROM {{schema}}.checkpoints s
        WHERE
            s.job_id = temp._ces_done.job_id
            AND s.checkpoint_kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
            AND s.checkpoint_name = temp._ces_done.wait_signal_name
    )
WHERE wait_signal_name IS NOT NULL;

UPDATE temp._ces_done
SET to_status = 10 /* JobStatusCode.Ready */
WHERE wait_signal_name IS NOT NULL AND sig_state = 20 /* CheckpointStatusCode.Set */;
-- end waits

-- begin shapes
-- The next run goes first, while the row is still Executing: a timer consumed this attempt left it NULL, and
-- ck_jobs_state_ready_due holds a Ready row to one.
UPDATE {{schema}}.jobs_state AS r
SET next_run_at_utc = CASE
    WHEN d.wait_signal_name IS NOT NULL AND d.sig_state = 20 /* CheckpointStatusCode.Set */ THEN (SELECT now_utc FROM temp._ces_done LIMIT 1)
    WHEN d.wait_signal_name IS NOT NULL THEN d.sig_due
    WHEN d.reschedule_event_code IS NOT NULL THEN COALESCE(d.reschedule_resume_at_utc, (SELECT now_utc FROM temp._ces_done LIMIT 1) + d.reschedule_delay_seconds * 1000)
    -- A handler's pause keeps a next run, which marks it held (ScheduleWalker, "held").
    WHEN d.handler_job_status_code = 30 /* JobStatusCode.Paused */ THEN (SELECT now_utc FROM temp._ces_done LIMIT 1)
    WHEN d.handler_job_status_code IS NOT NULL THEN NULL
    ELSE d.job_next_run_at_utc
END
FROM temp._ces_done d
WHERE
    r.job_id = d.job_id
    AND (d.reschedule_event_code IS NOT NULL OR d.handler_job_status_code IS NOT NULL OR d.final_job_status_code IS NOT NULL);
-- end shapes

UPDATE {{schema}}.jobs_state AS r
SET
    job_status_code = d.to_status,
    failure_count = COALESCE(d.failure_count, r.failure_count),
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = CASE
        WHEN d.to_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN COALESCE((SELECT now_utc FROM temp._ces_done LIMIT 1) + d.retention_seconds * 1000, r.retention_until_utc)
        ELSE r.retention_until_utc
    END,
    result_format_id = d.result_format_id,
    result = d.result,
    modified_at_utc = (SELECT now_utc FROM temp._ces_done LIMIT 1),
    version = r.version + 1
FROM temp._ces_done d
WHERE r.job_id = d.job_id;

-- begin recurring
/* A permanent slot that landed Idle, Failed, or Cancelled settled its firing. Every member schedule parked on the slot
   is woken to evaluate again, and the envelope records the outcome once. The cursors stay where the admission left
   them: completion never computes an occurrence. */
UPDATE {{schema}}.schedules_state AS st
SET
    next_evaluation_at_utc = (SELECT now_utc FROM temp._ces_done LIMIT 1),
    evaluation_status_code = 10 /* EvaluationStatusCode.Scheduled */,
    modified_at_utc = (SELECT now_utc FROM temp._ces_done LIMIT 1),
    version = st.version + 1
FROM {{schema}}.schedules js
INNER JOIN temp._ces_done d ON d.job_id = js.job_id
INNER JOIN {{schema}}.jobs j ON j.job_id = d.job_id
WHERE
    st.schedule_id = js.schedule_id
    AND d.to_status IN (40 /* JobStatusCode.Idle */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */
    AND st.evaluation_status_code IN (20 /* EvaluationStatusCode.WaitingForCapacity */, 50 /* EvaluationStatusCode.SlotUnavailable */);

UPDATE {{schema}}.checkpoints AS cp
SET
    checkpoint_status_code = CASE
        WHEN d.to_status = 220 /* JobStatusCode.Cancelled */ THEN 130 /* CheckpointStatusCode.Cancelled */
        WHEN d.to_status = 200 /* JobStatusCode.Failed */ OR d.succeeded = 0 THEN 120 /* CheckpointStatusCode.Failed */
        ELSE 110 /* CheckpointStatusCode.Succeeded */
    END,
    modified_at_utc = (SELECT now_utc FROM temp._ces_done LIMIT 1),
    version = cp.version + 1
FROM temp._ces_done d
INNER JOIN {{schema}}.jobs j ON j.job_id = d.job_id
WHERE
    cp.job_id = d.job_id
    AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
    AND cp.checkpoint_name = 'sys.firing'
    AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
    AND d.to_status IN (40 /* JobStatusCode.Idle */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */;
-- end recurring

/* The attempt's own event under the recording gate (EventRecordingCode) plus the alert overlay, which also writes a one-shot's
   success past attempt 1, since a restart zeroes the count yet the success must resolve what the failure opened; a recurring
   fire keeps the recovery rule, or every idle tick would write. Details are fixed kebab words, so the JSON needs no escaping. */
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id, worker_id, execution_number, duration_ms,
    event_message, details_format_id, details)
SELECT
    x.event_code, (SELECT now_utc FROM temp._ces_done LIMIT 1), x.namespace_id, 50 /* EntityCode.Job */, x.job_id,
    x.definition_id, x.tenant_id, COALESCE(x.lineage_root_job_id, x.job_id), @p_leased_by_worker_id, x.execution_number, x.duration_ms,
    x.event_message,
    CASE WHEN x.details IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END,
    CAST(x.details AS BLOB)
FROM (
    SELECT
        d.*,
        NULLIF(
            '{'
            || substr(
                COALESCE(
                    CASE
                        WHEN d.event_code = 41 /* EventCode.JobExecutionFailed */
                            THEN ',"to":"' || CASE d.to_status
                                WHEN 10 /* JobStatusCode.Ready */ THEN 'ready'
                                WHEN 20 /* JobStatusCode.Suspended */ THEN 'suspended'
                                WHEN 30 /* JobStatusCode.Paused */ THEN 'paused'
                                WHEN 40 /* JobStatusCode.Idle */ THEN 'idle'
                            END || '"'
                        WHEN d.event_code = 43 /* EventCode.JobExecutionYielded */
                            THEN ',"outcome":"' || CASE
                                WHEN d.reschedule_event_code = 60 /* EventCode.JobSuspended */ THEN 'suspended'
                                WHEN d.reschedule_event_code IS NOT NULL THEN 'rescheduled'
                                WHEN d.handler_job_status_code = 30 /* JobStatusCode.Paused */ THEN 'paused'
                                ELSE 'cancelled'
                            END || '"'
                    END,
                    ''
                )
                || COALESCE(',"reason":"' || d.reason || '"', ''),
                2
            )
            || '}',
            '{}'
        ) AS details
    FROM temp._ces_done d
) x
WHERE
    x.event_recording_code = 20 /* EventRecordingCode.Audit */
    OR (
        x.event_recording_code = 10 /* EventRecordingCode.Failures */
        AND (
            x.event_code IN (41 /* EventCode.JobExecutionFailed */, 44 /* EventCode.JobFailed */)
            OR (x.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND x.prior_failure_count > 0)
        )
    )
    OR (
        x.alert_profile_code_effective <> 0 /* AlertProfileCode.None */
        AND (
            x.event_code IN (41 /* EventCode.JobExecutionFailed */, 44 /* EventCode.JobFailed */)
            OR (
                x.event_code = 40 /* EventCode.JobExecutionSucceeded */
                AND (x.prior_failure_count > 0 OR (x.execution_number > 1 AND x.final_job_status_code IS NULL))
            )
        )
    );

-- begin shapes
/* At Audit each shape also says what it did: held or cancelled a slot, re-armed, or held by its handler.
   A suspension lands Ready on a sleep and Suspended on a wait, so its details say where. */
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id, worker_id, execution_number,
    event_message, details_format_id, details)
SELECT
    k.shape_code, (SELECT now_utc FROM temp._ces_done LIMIT 1), k.namespace_id, 50 /* EntityCode.Job */, k.job_id,
    k.definition_id, k.tenant_id, COALESCE(k.lineage_root_job_id, k.job_id), @p_leased_by_worker_id, k.execution_number,
    k.event_message,
    CASE WHEN k.details IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END,
    CAST(k.details AS BLOB)
FROM (
    SELECT
        s.*,
        NULLIF(
            '{'
            || substr(
                COALESCE(
                    CASE
                        WHEN s.shape_code = 60 /* EventCode.JobSuspended */
                            THEN CASE s.to_status
                                WHEN 10 /* JobStatusCode.Ready */ THEN ',"to":"ready"'
                                WHEN 20 /* JobStatusCode.Suspended */ THEN ',"to":"suspended"'
                            END
                    END,
                    ''
                )
                || COALESCE(',"reason":"' || s.reason || '"', ''),
                2
            )
            || '}',
            '{}'
        ) AS details
    FROM (
        SELECT
            d.*,
            n.ord,
            CASE n.ord
                -- A slot held by its handler, or cancelled by it; a settled firing needs no shape event beside the attempt's.
                WHEN 1 THEN CASE d.final_job_status_code
                    WHEN 30 /* JobStatusCode.Paused */ THEN 71 /* EventCode.JobPaused */
                    WHEN 220 /* JobStatusCode.Cancelled */ THEN 70 /* EventCode.JobCancelled */
                END
                -- A wait whose signal had already arrived landed Ready and re-armed nothing.
                WHEN 2 THEN CASE
                    WHEN d.reschedule_event_code IS NOT NULL AND NOT (d.wait_signal_name IS NOT NULL AND d.to_status = 10 /* JobStatusCode.Ready */)
                        THEN d.reschedule_event_code
                END
                ELSE CASE
                    WHEN d.handler_job_status_code = 220 /* JobStatusCode.Cancelled */ THEN 70 /* EventCode.JobCancelled */
                    WHEN d.handler_job_status_code = 30 /* JobStatusCode.Paused */ THEN 71 /* EventCode.JobPaused */
                END
            END AS shape_code
        FROM temp._ces_done d
        CROSS JOIN (SELECT 1 AS ord UNION ALL SELECT 2 UNION ALL SELECT 3) n
        WHERE d.event_recording_code = 20 /* EventRecordingCode.Audit */
    ) s
) k
WHERE k.shape_code IS NOT NULL
ORDER BY k.job_id, k.ord;
-- end shapes

-- begin children
/* A terminal child sets its latch on the parent and wakes a Suspended parent. An Expired latch already timed the wait
   out, so a later child writes no latch and wakes no one, though the audit event records it; a finished parent takes nothing. */
UPDATE temp._ces_done
SET
    parent_status = (SELECT pr.job_status_code FROM {{schema}}.jobs_state pr WHERE pr.job_id = temp._ces_done.parent_job_id),
    latch_state = (
        SELECT l.checkpoint_status_code FROM {{schema}}.checkpoints l
        WHERE
            l.job_id = temp._ces_done.parent_job_id
            AND l.checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
            AND l.checkpoint_name = 'sys.child.' || temp._ces_done.job_id
    )
WHERE
    parent_job_id IS NOT NULL
    AND to_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id, value, modified_at_utc, version
)
SELECT
    d.parent_job_id,
    50 /* CheckpointKindCode.ChildLatch */,
    'sys.child.' || d.job_id,
    20 /* CheckpointStatusCode.Set */,
    1 /* JobPayloadFormat.Json */,
    CAST(json_object('childJobId', d.job_id, 'status', d.to_status, 'executionNumber', d.execution_number) AS BLOB),
    (SELECT now_utc FROM temp._ces_done LIMIT 1),
    0
FROM temp._ces_done d
WHERE
    d.parent_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND (d.latch_state IS NULL OR d.latch_state <> 30 /* CheckpointStatusCode.Expired */)
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
    checkpoint_status_code = 20 /* CheckpointStatusCode.Set */,
    value_format_id = excluded.value_format_id,
    value = excluded.value,
    modified_at_utc = (SELECT now_utc FROM temp._ces_done LIMIT 1),
    version = {{schema}}.checkpoints.version + 1;

/* The resumed event goes once per parent; the raised event once per child, carrying the child's reason and message,
   the message capped at the column width. */
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id, execution_number,
    event_message, details_format_id, details)
SELECT
    80 /* EventCode.JobSignalRaised */, (SELECT now_utc FROM temp._ces_done LIMIT 1), pj.namespace_id, 50 /* EntityCode.Job */, d.parent_job_id,
    pj.definition_id, pj.tenant_id, COALESCE(pj.lineage_root_job_id, d.parent_job_id), pr.execution_number,
    CASE
        WHEN d.latch_state = 30 /* CheckpointStatusCode.Expired */
            THEN substr(COALESCE(d.event_message || ' ', '') || 'Child outcome not applied: the wait had already expired.', 1, 512)
        ELSE d.event_message
    END,
    CASE WHEN d.reason IS NULL THEN 0 /* JobPayloadFormat.None */ ELSE 1 /* JobPayloadFormat.Json */ END,
    CAST('{"reason":"' || d.reason || '"}' AS BLOB)
FROM temp._ces_done d
INNER JOIN {{schema}}.jobs pj ON pj.job_id = d.parent_job_id
INNER JOIN {{schema}}.jobs_state pr ON pr.job_id = d.parent_job_id
WHERE
    d.parent_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND pj.event_recording_code = 20 /* EventRecordingCode.Audit */
UNION ALL
SELECT DISTINCT
    72 /* EventCode.JobResumed */, (SELECT now_utc FROM temp._ces_done LIMIT 1), pj.namespace_id, 50 /* EntityCode.Job */, d.parent_job_id,
    pj.definition_id, pj.tenant_id, COALESCE(pj.lineage_root_job_id, d.parent_job_id), pr.execution_number,
    NULL, 1 /* JobPayloadFormat.Json */, CAST('{"reason":"signal-released"}' AS BLOB)
FROM temp._ces_done d
INNER JOIN {{schema}}.jobs pj ON pj.job_id = d.parent_job_id
INNER JOIN {{schema}}.jobs_state pr ON pr.job_id = d.parent_job_id
WHERE
    d.parent_status = 20 /* JobStatusCode.Suspended */
    AND (d.latch_state IS NULL OR d.latch_state <> 30 /* CheckpointStatusCode.Expired */)
    AND pj.event_recording_code = 20 /* EventRecordingCode.Audit */;

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = (SELECT now_utc FROM temp._ces_done LIMIT 1),
    modified_at_utc = (SELECT now_utc FROM temp._ces_done LIMIT 1),
    version = version + 1
WHERE
    job_status_code = 20 /* JobStatusCode.Suspended */
    AND job_id IN (
        SELECT d.parent_job_id
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
    SELECT CASE WHEN h.job_status_code = 15 /* JobStatusCode.Blocked */ THEN h.job_id END
    FROM {{schema}}.jobs_state h
    WHERE
        h.job_id = (
            SELECT m.job_id
            FROM {{schema}}.jobs_state m
            WHERE
                m.lane_id = temp._ces_done.lane_id
                AND m.job_status_code IN (
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

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, (SELECT now_utc FROM temp._ces_done LIMIT 1)),
    modified_at_utc = (SELECT now_utc FROM temp._ces_done LIMIT 1),
    version = version + 1
WHERE job_id IN (SELECT d.head_id FROM temp._ces_done d);
-- end lanes

SELECT
    json_extract(c.value, '$.job_id') AS job_id,
    json_extract(c.value, '$.execution_number') AS execution_number,
    CASE
        WHEN d.job_id IS NOT NULL THEN 1 /* CompleteExecutionsAction.Completed */
        WHEN
            r.job_status_code IS NULL
            OR r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
        WHEN r.leased_by_worker_id IS NULL OR r.leased_by_worker_id <> @p_leased_by_worker_id
            THEN 2 /* CompleteExecutionsAction.NotOwner */
        ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
    END AS action,
    r.job_status_code AS final_job_status_code,
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
LEFT JOIN {{schema}}.jobs_state r ON r.job_id = json_extract(c.value, '$.job_id');
