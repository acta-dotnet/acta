CREATE OR REPLACE FUNCTION {{schema}}.complete_execution(
    p_id                    BIGINT,
    p_leased_by_worker_id   INT,
    p_execution_number      INT,
    p_reason_code           SMALLINT,
    p_reason_message        VARCHAR,
    p_result_format_id      SMALLINT,
    p_result                BYTEA,
    p_execution_succeeded   BOOLEAN,
    p_duration_ms           INT,
    p_reschedule_status_code   SMALLINT DEFAULT NULL,
    p_reschedule_delay_seconds INT DEFAULT NULL,
    p_reschedule_resume_at_utc TIMESTAMPTZ DEFAULT NULL,
    p_wait_signal_name      VARCHAR DEFAULT NULL,
    p_handler_status_code   SMALLINT DEFAULT NULL,
    p_retention_seconds     INT DEFAULT NULL,
    p_final_status          SMALLINT DEFAULT NULL,
    p_job_next_run_at_utc   TIMESTAMPTZ DEFAULT NULL,
    p_failure_count         SMALLINT DEFAULT NULL,
    p_recurring_result_cap  INT DEFAULT 0,
    p_advance_schedule_ids  BIGINT[] DEFAULT NULL,
    p_advance_next_runs     TIMESTAMPTZ[] DEFAULT NULL
)
RETURNS TABLE(
    action               SMALLINT,
    final_status_code    SMALLINT,
    final_next_run_at_utc TIMESTAMPTZ,
    db_now               TIMESTAMPTZ,
    parent_released      SMALLINT
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_recurring BOOLEAN := p_final_status IS NOT NULL;

    v_rearm BOOLEAN := p_reschedule_status_code IS NOT NULL;

    v_signal_suspend BOOLEAN := (p_reschedule_status_code IS NOT NULL AND p_wait_signal_name IS NOT NULL);

    v_handler BOOLEAN := p_handler_status_code IS NOT NULL;
    v_sig_state SMALLINT;
    v_to_status SMALLINT;
    v_ns        SMALLINT;
    v_lineage   BIGINT;
    v_def       INT;
    v_tenant    INT;
    v_en        INT;
    v_audit     SMALLINT;
    v_next_run  TIMESTAMPTZ;
    v_parent_id BIGINT;
    v_cur_status SMALLINT;
    v_cur_worker INT;
    v_cur_next_run TIMESTAMPTZ;
    v_sig       VARCHAR;
    v_psig      SMALLINT;
    v_pstatus   SMALLINT;
    v_pns       SMALLINT;
    v_plineage  BIGINT;
    v_pdef      INT;
    v_ptenant   INT;
    v_pexec     INT;
    v_paudit    SMALLINT;
    v_parent_released SMALLINT := 0;
    v_job_ref    uuid;
    v_parent_ref uuid;
BEGIN

    IF v_signal_suspend THEN
        SELECT state_code INTO v_sig_state
          FROM {{schema}}.checkpoints
         WHERE job_id = p_id
           AND kind_code IN (20 /* JobCheckpointKindCode.Signal */, 50 /* JobCheckpointKindCode.ChildLatch */)
           AND name = p_wait_signal_name
           FOR UPDATE;
    END IF;

    v_to_status := CASE
        WHEN v_signal_suspend AND v_sig_state = 20 /* JobCheckpointStateCode.Set */ THEN 10 /* JobStatusCode.Ready */
        WHEN v_signal_suspend THEN 20 /* JobStatusCode.Suspended */
        WHEN p_reschedule_status_code IS NOT NULL THEN 10 /* JobStatusCode.Ready */
        WHEN v_handler THEN p_handler_status_code
        WHEN p_final_status IS NOT NULL THEN p_final_status
        WHEN p_execution_succeeded THEN 100 /* JobStatusCode.Done */
        ELSE 200 /* JobStatusCode.Failed */
    END;

    UPDATE {{schema}}.runtimes r
       SET status_code          = v_to_status,
           next_run_at_utc      = CASE
                                      WHEN v_signal_suspend AND v_sig_state = 20 /* JobCheckpointStateCode.Set */ THEN now()
                                      WHEN v_signal_suspend THEN NULL
                                      WHEN v_rearm THEN COALESCE(p_reschedule_resume_at_utc, now() + make_interval(secs => p_reschedule_delay_seconds))
                                      WHEN v_handler THEN NULL
                                      WHEN v_recurring THEN p_job_next_run_at_utc
                                      ELSE r.next_run_at_utc END,
           failure_count        = COALESCE(p_failure_count, r.failure_count),
           leased_by_worker_id  = NULL,
           lease_expires_at_utc = NULL,
           retention_until_utc  = CASE
                                      WHEN v_to_status IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                                           AND p_retention_seconds IS NOT NULL
                                      THEN now() + make_interval(secs => p_retention_seconds)
                                      ELSE r.retention_until_utc END,
           modified_at_utc      = now(),
           version              = r.version + 1
      FROM {{schema}}.jobs j
     WHERE r.job_id             = p_id
       AND j.id                 = p_id
       AND r.execution_number   = p_execution_number
       AND r.status_code        = 50 /* JobStatusCode.Executing */
       AND r.leased_by_worker_id = p_leased_by_worker_id
    RETURNING j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number, j.audit_level_code, r.next_run_at_utc, j.parent_id, j.job_ref
      INTO v_ns, v_lineage, v_def, v_tenant, v_en, v_audit, v_next_run, v_parent_id, v_job_ref;

    IF NOT FOUND THEN
        SELECT r.status_code, r.leased_by_worker_id, r.next_run_at_utc INTO v_cur_status, v_cur_worker, v_cur_next_run
          FROM {{schema}}.runtimes r
         WHERE r.job_id = p_id;
        IF v_cur_status IS NULL OR v_cur_status IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN
            RETURN QUERY SELECT CAST(3 /* CompleteExecutionAction.AlreadyTerminal */ AS SMALLINT), v_cur_status, v_cur_next_run, now(), CAST(0 AS SMALLINT);
        ELSIF v_cur_worker IS DISTINCT FROM p_leased_by_worker_id OR v_cur_worker IS NULL THEN
            RETURN QUERY SELECT CAST(2 /* CompleteExecutionAction.NotOwner */ AS SMALLINT), v_cur_status, v_cur_next_run, now(), CAST(0 AS SMALLINT);
        ELSE
            RETURN QUERY SELECT CAST(3 /* CompleteExecutionAction.AlreadyTerminal */ AS SMALLINT), v_cur_status, v_cur_next_run, now(), CAST(0 AS SMALLINT);
        END IF;
        RETURN;
    END IF;

    IF p_result_format_id <> 0 /* JobPayloadFormat.None */ THEN
        INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
        VALUES (p_id, v_en, p_result_format_id, p_result, now());
    END IF;

    IF v_recurring THEN

        IF p_advance_schedule_ids IS NOT NULL AND cardinality(p_advance_schedule_ids) > 0 THEN
            IF v_audit = 20 /* JobAuditLevelCode.Audit */ THEN
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor_key,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    reason_code, reason_message)
                SELECT
                    102 /* JobEventCode.SchedulePauseExpired */, now(), v_ns,
                    10 /* JobActorCode.Sys */, NULL,
                    p_id, v_job_ref, v_en,
                    COALESCE(v_lineage, p_id), v_def, v_tenant,
                    NULL,
                    NULL, NULL,
                    NULL, NULL,
                    NULL, js.name
                  FROM {{schema}}.schedules js
                  JOIN unnest(p_advance_schedule_ids) AS a(schedule_id) ON a.schedule_id = js.id
                 WHERE js.status_code = 30 /* ScheduleStatusCode.Paused */;
            END IF;

            UPDATE {{schema}}.schedules js
               SET next_run_at_utc  = adv.next_run,
                   status_code      = 10 /* ScheduleStatusCode.Active */,
                   paused_until_utc = NULL,
                   modified_at_utc  = now(),
                   version          = version + 1
              FROM unnest(p_advance_schedule_ids, p_advance_next_runs) AS adv(schedule_id, next_run)
             WHERE js.id = adv.schedule_id;
        END IF;

        IF p_recurring_result_cap > 0 THEN
            DELETE FROM {{schema}}.results r
             WHERE r.job_id = p_id
               AND r.execution_number NOT IN (
                   SELECT execution_number FROM {{schema}}.results
                    WHERE job_id = p_id ORDER BY execution_number DESC LIMIT p_recurring_result_cap);
        END IF;
    END IF;

    IF v_audit = 20 /* JobAuditLevelCode.Audit */
       OR (v_audit = 10 /* JobAuditLevelCode.Failures */ AND NOT p_execution_succeeded AND NOT v_rearm
                       AND NOT (v_handler AND p_handler_status_code IN (220 /* JobStatusCode.Cancelled */, 30 /* JobStatusCode.Paused */))) THEN
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message)
        VALUES (
            41 /* JobEventCode.JobExecutionFinished */, now(), v_ns,
            70 /* JobActorCode.Worker */, NULL,
            p_id, v_job_ref, v_en,
            COALESCE(v_lineage, p_id), v_def, v_tenant,
            p_leased_by_worker_id,
            50 /* JobStatusCode.Executing */, v_to_status,
            CASE WHEN v_rearm THEN p_reschedule_status_code
                 WHEN v_handler AND p_handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 220 /* ExecutionStatusCode.Cancelled */
                 WHEN v_handler AND p_handler_status_code = 30 /* JobStatusCode.Paused */ THEN 152 /* ExecutionStatusCode.Paused */
                 WHEN p_execution_succeeded THEN 100 /* ExecutionStatusCode.Succeeded */
                 ELSE 200 /* ExecutionStatusCode.Failed */ END, p_duration_ms,
            p_reason_code, p_reason_message);
    END IF;

    IF v_recurring AND v_audit = 20 /* JobAuditLevelCode.Audit */ AND p_final_status IN (10 /* JobStatusCode.Ready */, 30 /* JobStatusCode.Paused */) THEN
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message)
        VALUES (
            CASE
                WHEN p_final_status = 10 /* JobStatusCode.Ready */ THEN 50 /* JobEventCode.JobRecurringRolledOver */
                ELSE 71 /* JobEventCode.JobPaused */
            END,
            now(), v_ns,
            70 /* JobActorCode.Worker */, NULL,
            p_id, v_job_ref, v_en,
            COALESCE(v_lineage, p_id), v_def, v_tenant,
            p_leased_by_worker_id,
            50 /* JobStatusCode.Executing */, v_to_status,
            NULL, NULL,
            p_reason_code, p_reason_message);
    END IF;

    IF v_rearm AND v_audit = 20 /* JobAuditLevelCode.Audit */ AND NOT (v_signal_suspend AND v_to_status = 10 /* JobStatusCode.Ready */) THEN
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message)
        VALUES (
            CASE WHEN p_reschedule_status_code = 151 /* ExecutionStatusCode.Suspended */ THEN 60 /* JobEventCode.JobSuspended */ ELSE 61 /* JobEventCode.JobRescheduled */ END,
            now(), v_ns,
            70 /* JobActorCode.Worker */, NULL,
            p_id, v_job_ref, v_en,
            COALESCE(v_lineage, p_id), v_def, v_tenant,
            p_leased_by_worker_id,
            50 /* JobStatusCode.Executing */, v_to_status,
            NULL, NULL,
            p_reason_code, p_reason_message);
    END IF;

    IF v_handler AND v_audit = 20 /* JobAuditLevelCode.Audit */ AND p_handler_status_code IN (220 /* JobStatusCode.Cancelled */, 30 /* JobStatusCode.Paused */) THEN
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message)
        VALUES (
            CASE WHEN p_handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 70 /* JobEventCode.JobCancelled */ ELSE 71 /* JobEventCode.JobPaused */ END,
            now(), v_ns,
            70 /* JobActorCode.Worker */, NULL,
            p_id, v_job_ref, v_en,
            COALESCE(v_lineage, p_id), v_def, v_tenant,
            p_leased_by_worker_id,
            50 /* JobStatusCode.Executing */, v_to_status,
            NULL, NULL,
            p_reason_code, p_reason_message);
    END IF;

    IF v_to_status IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) AND v_parent_id IS NOT NULL THEN
        v_sig := 'sys.child.' || p_id::text;

        SELECT js.state_code INTO v_psig
          FROM {{schema}}.checkpoints js
         WHERE js.job_id = v_parent_id AND js.kind_code = 50 /* JobCheckpointKindCode.ChildLatch */ AND js.name = v_sig
           FOR UPDATE;

        SELECT pr.status_code, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, pr.execution_number, j.audit_level_code, j.job_ref
          INTO v_pstatus, v_pns, v_plineage, v_pdef, v_ptenant, v_pexec, v_paudit, v_parent_ref
          FROM {{schema}}.jobs j
          JOIN {{schema}}.runtimes pr ON pr.job_id = j.id
         WHERE j.id = v_parent_id
           FOR UPDATE OF pr;

        IF v_pstatus IS NOT NULL AND v_pstatus NOT IN (100 /* JobStatusCode.Done */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN
            INSERT INTO {{schema}}.checkpoints (job_id, kind_code, name, state_code, value_format_id, value, created_at_utc, modified_at_utc, version)
            VALUES (v_parent_id, 50 /* JobCheckpointKindCode.ChildLatch */, v_sig, 20 /* JobCheckpointStateCode.Set */, 1 /* JobPayloadFormat.Json */,
                    convert_to(json_build_object(
                        'childJobId', p_id,
                        'status', v_to_status)::text, 'UTF8'),
                    now(), now(), 0)
            ON CONFLICT (job_id, kind_code, name) DO UPDATE
                SET state_code      = 20 /* JobCheckpointStateCode.Set */,
                    value_format_id = EXCLUDED.value_format_id,
                    value           = EXCLUDED.value,
                    modified_at_utc = now(),
                    version         = {{schema}}.checkpoints.version + 1;

            IF v_paudit = 20 /* JobAuditLevelCode.Audit */ THEN
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id, worker_id, from_status_code, to_status_code,
                    execution_status_code, duration_ms, reason_code, reason_message)
                VALUES (
                    80 /* JobEventCode.JobSignalRaised */, now(), v_pns, 10 /* JobActorCode.Sys */, NULL,
                    v_parent_id, v_parent_ref, v_pexec, COALESCE(v_plineage, v_parent_id), v_pdef, v_ptenant, NULL,
                    v_pstatus, v_pstatus, NULL, NULL, p_reason_code, p_reason_message);
            END IF;

            IF v_pstatus = 20 /* JobStatusCode.Suspended */ THEN
                UPDATE {{schema}}.runtimes
                   SET status_code = 10 /* JobStatusCode.Ready */, next_run_at_utc = now(),
                       modified_at_utc = now(), version = version + 1
                 WHERE job_id = v_parent_id;

                IF v_paudit = 20 /* JobAuditLevelCode.Audit */ THEN
                    INSERT INTO {{schema}}.events (
                        event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, job_ref, execution_number,
                        lineage_root_id, definition_id, tenant_id, worker_id, from_status_code, to_status_code,
                        execution_status_code, duration_ms, reason_code, reason_message)
                    VALUES (
                        72 /* JobEventCode.JobResumed */, now(), v_pns, 10 /* JobActorCode.Sys */, NULL,
                        v_parent_id, v_parent_ref, v_pexec, COALESCE(v_plineage, v_parent_id), v_pdef, v_ptenant, NULL,
                        20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */,
                        NULL, NULL, 60 /* JobEventReasonCode.JobSignalReleased */, NULL);
                END IF;

                v_parent_released := 1;
            END IF;
        END IF;
    END IF;

    RETURN QUERY SELECT CAST(1 /* CompleteExecutionAction.Completed */ AS SMALLINT), v_to_status, v_next_run, now(), v_parent_released;
END;
$$;
