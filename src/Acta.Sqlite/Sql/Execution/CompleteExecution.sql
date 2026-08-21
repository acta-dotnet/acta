DROP TABLE IF EXISTS temp._ce_pre;
DROP TABLE IF EXISTS temp._ce_sig;
DROP TABLE IF EXISTS temp._ce_done;
DROP TABLE IF EXISTS temp._ce_parent;

CREATE TEMP TABLE _ce_pre AS
SELECT
    j.id,
    r.status_code AS cur_status,
    r.leased_by_worker_id AS cur_worker,
    r.next_run_at_utc AS cur_next_run,
    j.namespace_id,
    j.lineage_root_id,
    j.definition_id,
    j.tenant_id,
    r.execution_number,
    j.audit_level_code,
    j.parent_id,
    j.job_ref,
    CASE
        WHEN @p_wait_signal_name IS NOT NULL AND @p_reschedule_status_code IS NOT NULL THEN
            (
                SELECT s.status_code
                FROM {{schema}}.checkpoints s
                WHERE
                    s.job_id = j.id
                    AND s.kind_code IN (20 /* JobCheckpointKindCode.Signal */, 50 /* JobCheckpointKindCode.ChildLatch */)
                    AND s.name = @p_wait_signal_name
            )
        ELSE NULL
    END AS sig_state,
    -- The awaited slot is the only place the deadline lives; a suspend carries it into
    -- next_run_at_utc so the claim wakes the job at its expiration (NULL on an unbounded wait).
    CASE
        WHEN @p_wait_signal_name IS NOT NULL AND @p_reschedule_status_code IS NOT NULL THEN
            (
                SELECT s.due_at_utc
                FROM {{schema}}.checkpoints s
                WHERE
                    s.job_id = j.id
                    AND s.kind_code IN (20 /* JobCheckpointKindCode.Signal */, 50 /* JobCheckpointKindCode.ChildLatch */)
                    AND s.name = @p_wait_signal_name
            )
        ELSE NULL
    END AS sig_due
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_id;

CREATE TEMP TABLE _ce_done AS
SELECT
    CASE
        WHEN @p_wait_signal_name IS NOT NULL AND @p_reschedule_status_code IS NOT NULL AND p.sig_state = 20 /* JobCheckpointStatusCode.Set */ THEN 10 /* JobStatusCode.Ready */
        WHEN @p_wait_signal_name IS NOT NULL AND @p_reschedule_status_code IS NOT NULL THEN 20 /* JobStatusCode.Suspended */
        WHEN @p_reschedule_status_code IS NOT NULL THEN 10 /* JobStatusCode.Ready */
        WHEN @p_handler_status_code IS NOT NULL THEN @p_handler_status_code
        WHEN @p_final_status IS NOT NULL THEN @p_final_status
        WHEN @p_execution_succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */
        ELSE 200 /* JobStatusCode.Failed */
    END AS to_status,
    p.id,
    p.namespace_id,
    p.lineage_root_id,
    p.definition_id,
    p.tenant_id,
    p.execution_number,
    p.audit_level_code,
    p.parent_id,
    p.job_ref,
    p.sig_state,
    p.sig_due
FROM _ce_pre p
WHERE
    p.cur_status = 50 /* JobStatusCode.Executing */
    AND p.cur_worker = @p_leased_by_worker_id
    AND p.execution_number = @p_execution_number;

UPDATE {{schema}}.runtimes
SET
    status_code = (SELECT to_status FROM _ce_done),
    next_run_at_utc = CASE
        WHEN @p_wait_signal_name IS NOT NULL AND @p_reschedule_status_code IS NOT NULL AND (SELECT sig_state FROM _ce_done) = 20 THEN {{now}}
        WHEN @p_wait_signal_name IS NOT NULL AND @p_reschedule_status_code IS NOT NULL THEN (SELECT sig_due FROM _ce_done)
        WHEN @p_reschedule_status_code IS NOT NULL THEN COALESCE(@p_reschedule_resume_at_utc, {{now}} + (@p_reschedule_delay_seconds) * 1000)
        WHEN @p_handler_status_code IS NOT NULL THEN NULL
        WHEN @p_final_status IS NOT NULL THEN @p_job_next_run_at_utc
        ELSE next_run_at_utc END,
    failure_count = COALESCE(@p_failure_count, failure_count),
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = CASE
        WHEN (SELECT to_status FROM _ce_done) IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            AND @p_retention_seconds IS NOT NULL
        THEN {{now}} + (@p_retention_seconds) * 1000
        ELSE retention_until_utc END,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_id
    AND EXISTS (SELECT 1 FROM _ce_done);

INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
SELECT @p_id, d.execution_number, @p_result_format_id, @p_result, {{now}}
FROM _ce_done d
WHERE @p_result_format_id <> 0 /* JobPayloadFormat.None */;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    102 /* EventCode.SchedulePauseExpired */,
    {{now}},
    d.namespace_id,
    10 /* ActorCode.Sys */,
    NULL,
    @p_id,
    d.job_ref,
    d.execution_number,
    COALESCE(d.lineage_root_id, @p_id),
    d.definition_id,
    d.tenant_id,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    NULL,
    js.name
FROM _ce_done d
JOIN {{schema}}.schedules js
    ON js.id IN (SELECT json_extract(a.value, '$.schedule_id') FROM json_each(@p_schedule_advances) a)
WHERE
    @p_final_status IS NOT NULL
    AND d.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND js.status_code = 30 /* ScheduleStatusCode.Paused */
    AND js.paused_until_utc IS NOT NULL
    AND js.paused_until_utc <= {{now}};

UPDATE {{schema}}.schedules
SET
    next_run_at_utc = (SELECT json_extract(a.value, '$.next_run_at_utc') FROM json_each(@p_schedule_advances) a WHERE json_extract(a.value, '$.schedule_id') = {{schema}}.schedules.id),
    last_occurrence_at_utc = next_run_at_utc,
    status_code = 10 /* ScheduleStatusCode.Active */,
    paused_until_utc = NULL,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    @p_final_status IS NOT NULL
    AND EXISTS (SELECT 1 FROM _ce_done)
    AND id IN (SELECT json_extract(a.value, '$.schedule_id') FROM json_each(@p_schedule_advances) a)
    -- An operator pause inside the fire window wins; only an elapsed TIMED pause auto-resumes here.
    -- Same predicate as the audit insert above, so the two cannot disagree.
    AND (
        status_code <> 30 /* ScheduleStatusCode.Paused */
        OR (paused_until_utc IS NOT NULL AND paused_until_utc <= {{now}})
    );

DELETE FROM {{schema}}.results
WHERE
    @p_final_status IS NOT NULL
    AND @p_recurring_result_cap > 0
    AND EXISTS (SELECT 1 FROM _ce_done)
    AND job_id = @p_id
    AND execution_number NOT IN (
        SELECT execution_number
        FROM {{schema}}.results
        WHERE job_id = @p_id
        ORDER BY execution_number DESC
        LIMIT @p_recurring_result_cap);

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    41 /* EventCode.JobExecutionFinished */,
    {{now}},
    d.namespace_id,
    70 /* ActorCode.Worker */,
    NULL,
    @p_id,
    d.job_ref,
    d.execution_number,
    COALESCE(d.lineage_root_id, @p_id),
    d.definition_id,
    d.tenant_id,
    @p_leased_by_worker_id,
    50 /* JobStatusCode.Executing */,
    d.to_status,
    CASE
        WHEN @p_reschedule_status_code IS NOT NULL THEN @p_reschedule_status_code
        WHEN @p_handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 220 /* ExecutionStatusCode.Cancelled */
        WHEN @p_handler_status_code = 30 /* JobStatusCode.Paused */ THEN 152 /* ExecutionStatusCode.Paused */
        WHEN @p_execution_succeeded = 1 THEN 100 /* ExecutionStatusCode.Succeeded */
        ELSE 200 /* ExecutionStatusCode.Failed */ END,
    @p_duration_ms,
    @p_reason_code,
    @p_reason_message
FROM _ce_done d
WHERE
    d.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    OR (d.audit_level_code = 10 /* JobAuditLevelCode.Failures */
        AND @p_execution_succeeded = 0
        AND @p_reschedule_status_code IS NULL
        AND NOT (@p_handler_status_code IN (220 /* JobStatusCode.Cancelled */, 30 /* JobStatusCode.Paused */)));

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    CASE WHEN @p_final_status = 10 /* JobStatusCode.Ready */ THEN 50 /* EventCode.JobRecurringRolledOver */ ELSE 71 /* EventCode.JobPaused */ END,
    {{now}},
    d.namespace_id,
    70 /* ActorCode.Worker */,
    NULL,
    @p_id,
    d.job_ref,
    d.execution_number,
    COALESCE(d.lineage_root_id, @p_id),
    d.definition_id,
    d.tenant_id,
    @p_leased_by_worker_id,
    50 /* JobStatusCode.Executing */,
    d.to_status,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM _ce_done d
WHERE
    @p_final_status IS NOT NULL
    AND d.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND @p_final_status IN (10 /* JobStatusCode.Ready */, 30 /* JobStatusCode.Paused */);

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    CASE WHEN @p_reschedule_status_code = 151 /* ExecutionStatusCode.Suspended */ THEN 60 /* EventCode.JobSuspended */ ELSE 61 /* EventCode.JobRescheduled */ END,
    {{now}},
    d.namespace_id,
    70 /* ActorCode.Worker */,
    NULL,
    @p_id,
    d.job_ref,
    d.execution_number,
    COALESCE(d.lineage_root_id, @p_id),
    d.definition_id,
    d.tenant_id,
    @p_leased_by_worker_id,
    50 /* JobStatusCode.Executing */,
    d.to_status,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM _ce_done d
WHERE
    @p_reschedule_status_code IS NOT NULL
    AND d.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND NOT (@p_wait_signal_name IS NOT NULL AND d.to_status = 10 /* JobStatusCode.Ready */);

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    CASE WHEN @p_handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 70 /* EventCode.JobCancelled */ ELSE 71 /* EventCode.JobPaused */ END,
    {{now}},
    d.namespace_id,
    70 /* ActorCode.Worker */,
    NULL,
    @p_id,
    d.job_ref,
    d.execution_number,
    COALESCE(d.lineage_root_id, @p_id),
    d.definition_id,
    d.tenant_id,
    @p_leased_by_worker_id,
    50 /* JobStatusCode.Executing */,
    d.to_status,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM _ce_done d
WHERE
    @p_handler_status_code IS NOT NULL
    AND d.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND @p_handler_status_code IN (220 /* JobStatusCode.Cancelled */, 30 /* JobStatusCode.Paused */);

CREATE TEMP TABLE _ce_parent AS
SELECT
    d.parent_id,
    d.to_status,
    pr.status_code AS parent_status,
    pj.namespace_id AS parent_ns,
    pj.lineage_root_id AS parent_lineage,
    pj.definition_id AS parent_def,
    pj.tenant_id AS parent_tenant,
    pr.execution_number AS parent_exec,
    pj.audit_level_code AS parent_audit,
    pj.job_ref AS parent_ref
FROM _ce_done d
JOIN {{schema}}.jobs pj ON pj.id = d.parent_id
JOIN {{schema}}.runtimes pr ON pr.job_id = pj.id
WHERE
    d.to_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND d.parent_id IS NOT NULL
    AND pr.status_code IS NOT NULL
    AND pr.status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

INSERT INTO {{schema}}.checkpoints (job_id, kind_code, name, status_code, value_format_id, value, modified_at_utc, version)
SELECT
    p.parent_id,
    50 /* JobCheckpointKindCode.ChildLatch */,
    'sys.child.' || @p_id,
    20 /* JobCheckpointStatusCode.Set */,
    1 /* JobPayloadFormat.Json */,
    CAST(json_object('childJobId', @p_id, 'status', p.to_status) AS BLOB),
    {{now}},
    0
FROM _ce_parent p
WHERE true
ON CONFLICT (job_id, kind_code, name) DO UPDATE SET
    status_code = 20 /* JobCheckpointStatusCode.Set */,
    value_format_id = excluded.value_format_id,
    value = excluded.value,
    modified_at_utc = {{now}},
    version = {{schema}}.checkpoints.version + 1;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    80 /* EventCode.JobSignalRaised */,
    {{now}},
    p.parent_ns,
    10 /* ActorCode.Sys */,
    NULL,
    p.parent_id,
    p.parent_ref,
    p.parent_exec,
    COALESCE(p.parent_lineage, p.parent_id),
    p.parent_def,
    p.parent_tenant,
    NULL,
    p.parent_status,
    p.parent_status,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM _ce_parent p
WHERE p.parent_audit = 20 /* JobAuditLevelCode.Audit */;

UPDATE {{schema}}.runtimes
SET
    status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = {{now}},
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id IN (SELECT parent_id FROM _ce_parent WHERE parent_status = 20 /* JobStatusCode.Suspended */);

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    72 /* EventCode.JobResumed */,
    {{now}},
    p.parent_ns,
    10 /* ActorCode.Sys */,
    NULL,
    p.parent_id,
    p.parent_ref,
    p.parent_exec,
    COALESCE(p.parent_lineage, p.parent_id),
    p.parent_def,
    p.parent_tenant,
    NULL,
    20 /* JobStatusCode.Suspended */,
    10 /* JobStatusCode.Ready */,
    NULL,
    NULL,
    60 /* JobEventReasonCode.JobSignalReleased */,
    NULL
FROM _ce_parent p
WHERE
    p.parent_status = 20 /* JobStatusCode.Suspended */
    AND p.parent_audit = 20 /* JobAuditLevelCode.Audit */;

SELECT
    CASE
        WHEN NOT EXISTS (SELECT 1 FROM _ce_pre) THEN 3 /* CompleteExecutionAction.AlreadyTerminal */
        WHEN EXISTS (SELECT 1 FROM _ce_done) THEN 1 /* CompleteExecutionAction.Completed */
        WHEN (SELECT cur_status FROM _ce_pre) IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN 3 /* CompleteExecutionAction.AlreadyTerminal */
        WHEN (SELECT cur_worker FROM _ce_pre) IS NULL OR (SELECT cur_worker FROM _ce_pre) <> @p_leased_by_worker_id THEN 2 /* CompleteExecutionAction.NotOwner */
        ELSE 3 /* CompleteExecutionAction.AlreadyTerminal */
    END AS action,
    CASE
        WHEN EXISTS (SELECT 1 FROM _ce_done) THEN (SELECT to_status FROM _ce_done)
        ELSE (SELECT cur_status FROM _ce_pre)
    END AS final_status_code,
    CASE
        WHEN EXISTS (SELECT 1 FROM _ce_done) THEN (SELECT next_run_at_utc FROM {{schema}}.runtimes WHERE job_id = @p_id)
        ELSE (SELECT cur_next_run FROM _ce_pre)
    END AS final_next_run_at_utc,
    {{now}} AS db_now,
    CASE WHEN EXISTS (SELECT 1 FROM _ce_parent WHERE parent_status = 20 /* JobStatusCode.Suspended */) THEN 1 ELSE 0 END AS parent_released;
