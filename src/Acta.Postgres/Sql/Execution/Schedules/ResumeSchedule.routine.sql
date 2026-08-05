CREATE OR REPLACE FUNCTION {{schema}}.resume_schedule(
    p_job_id               BIGINT,
    p_name                 VARCHAR,
    p_next_run_at_utc      TIMESTAMPTZ,
    p_job_next_run_at_utc  TIMESTAMPTZ,
    p_actor_code           SMALLINT,
    p_actor_key             VARCHAR,
    p_note                 VARCHAR
)
RETURNS TABLE(out_action SMALLINT, out_status_code SMALLINT, out_paused_until_utc TIMESTAMPTZ, out_next_run_at_utc TIMESTAMPTZ, out_version INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_schedule_id   BIGINT;
    v_status        SMALLINT;
    v_paused        TIMESTAMPTZ;
    v_next          TIMESTAMPTZ;
    v_version       INT;
    v_ns            SMALLINT;
    v_def           INT;
    v_lineage       BIGINT;
    v_en            INT;
    v_audit         SMALLINT;
    v_job_ref       UUID;
BEGIN
    /* Lock the slot's runtimes row before the schedules row: register_scheduled_jobs writes
       runtimes then schedules, so every writer of both must take runtimes first. */
    SELECT r.execution_number
      INTO v_en
      FROM {{schema}}.runtimes r
     WHERE r.job_id = p_job_id
     FOR UPDATE;

    SELECT js.id
      INTO v_schedule_id
      FROM {{schema}}.schedules js
     WHERE js.job_id = p_job_id
       AND js.name = p_name
       AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */
     FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* JobControlAction.NotFound */::SMALLINT, NULL::SMALLINT, NULL::TIMESTAMPTZ, NULL::TIMESTAMPTZ, NULL::INT;
        RETURN;
    END IF;

    SELECT j.namespace_id, j.definition_id, j.lineage_root_id, j.audit_level_code, j.job_ref
      INTO v_ns, v_def, v_lineage, v_audit, v_job_ref
      FROM {{schema}}.jobs j
     WHERE j.id = p_job_id;

    UPDATE {{schema}}.schedules
       SET status_code      = 10 /* ScheduleStatusCode.Active */,
           paused_until_utc = NULL,
           next_run_at_utc  = p_next_run_at_utc,
           note             = p_note,
           modified_at_utc  = now(),
           version          = version + 1
     WHERE id = v_schedule_id
    RETURNING status_code, paused_until_utc, next_run_at_utc, version
      INTO v_status, v_paused, v_next, v_version;

    UPDATE {{schema}}.runtimes
       SET next_run_at_utc = p_job_next_run_at_utc,
           status_code     = CASE WHEN p_job_next_run_at_utc IS NULL
                                  THEN 30 /* JobStatusCode.Paused */
                                  ELSE 10 /* JobStatusCode.Ready */ END,
           modified_at_utc = now(),
           version         = version + 1
     WHERE job_id = p_job_id
       AND status_code IN (30 /* JobStatusCode.Paused */, 10 /* JobStatusCode.Ready */);

    IF v_audit = 20 /* JobAuditLevelCode.Audit */ THEN
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message)
        VALUES (
            101 /* JobEventCode.ScheduleResumed */, now(), v_ns,
            p_actor_code, p_actor_key,
            p_job_id, v_job_ref, v_en,
            COALESCE(v_lineage, p_job_id), v_def,
            NULL,
            NULL, NULL,
            NULL, NULL,
            NULL, p_name);
    END IF;

    RETURN QUERY SELECT 1 /* JobControlAction.Applied */::SMALLINT, v_status, v_paused, v_next, v_version;
END;
$$;
