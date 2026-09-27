CREATE OR REPLACE FUNCTION {{schema}}.reschedule_job(
    p_id BIGINT,
    p_next_run_at_utc TIMESTAMPTZ,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_code SMALLINT,
    p_reason_message VARCHAR,
    p_expected_version INT DEFAULT NULL
)
RETURNS TABLE (action SMALLINT, status_code SMALLINT, version INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_from_status SMALLINT;
    v_version INT;
    v_namespace_id INT;
    v_lineage BIGINT;
    v_definition INT;
    v_tenant INT;
    v_en INT;
    v_audit SMALLINT;
    v_job_ref UUID;
    v_lane_id BIGINT;
    v_to_status SMALLINT := 10 /* JobStatusCode.Ready */;
BEGIN
    -- Lock order: the lane, then the job's rows (docs/internals/sql-execution-policy.md, "Lane lock
    -- order"). lane_id never changes, so the unlocked read is safe.
    SELECT r.lane_id INTO v_lane_id
    FROM {{schema}}.runtimes r
    WHERE r.job_id = p_id;

    IF v_lane_id IS NOT NULL THEN
        PERFORM 1
        FROM {{schema}}.lanes l
        WHERE l.id = v_lane_id
        FOR UPDATE;
    END IF;

    SELECT r.status_code, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number, j.audit_level_code, j.job_ref, r.version
    INTO v_from_status, v_namespace_id, v_lineage, v_definition, v_tenant, v_en, v_audit, v_job_ref, v_version
    FROM {{schema}}.jobs j
    JOIN {{schema}}.runtimes r ON r.job_id = j.id
    WHERE j.id = p_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* ControlAction.NotFound */::SMALLINT, NULL::SMALLINT, NULL::INT;
        RETURN;
    END IF;

    IF p_expected_version IS NOT NULL AND v_version <> p_expected_version THEN
        RETURN QUERY SELECT 5 /* ControlAction.VersionConflict */::SMALLINT, v_from_status, v_version;
        RETURN;
    END IF;

    IF v_from_status NOT IN (
        30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */
    ) THEN
        RETURN QUERY SELECT 3 /* ControlAction.Rejected */::SMALLINT, v_from_status, v_version;
        RETURN;
    END IF;

    -- A laned job is rescheduled Ready unless another member runs, or it was not running and an older member
    -- is unfinished (docs/internals/sql-execution-policy.md, "Lane lock order").
    IF v_lane_id IS NOT NULL AND EXISTS (
        SELECT 1
        FROM {{schema}}.runtimes o
        WHERE
            o.lane_id = v_lane_id
            AND o.lane_id IS NOT NULL
            AND o.status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
            )
            AND o.job_id <> p_id
            AND (
                o.status_code IN (
                    10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                    40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                )
                OR (
                    v_from_status NOT IN (
                        10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */, 40 /* JobStatusCode.Dispatched */
                    )
                    AND o.job_id < p_id
                )
            )
    ) THEN
        v_to_status := 15 /* JobStatusCode.Blocked */;
    END IF;

    UPDATE {{schema}}.runtimes AS r
    SET
        next_run_at_utc = p_next_run_at_utc,
        status_code = v_to_status,
        modified_at_utc = now(),
        version = r.version + 1
    WHERE r.job_id = p_id;

    IF v_audit = 20 /* JobAuditLevelCode.Audit */ THEN
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
        VALUES (
            61 /* EventCode.JobRescheduled */,
            now(),
            v_namespace_id,
            p_actor_code,
            p_actor_key,
            p_id,
            v_job_ref,
            v_en,
            COALESCE(v_lineage, p_id),
            v_definition,
            v_tenant,
            NULL,
            v_from_status,
            v_to_status,
            NULL,
            NULL,
            p_reason_code,
            p_reason_message);
    END IF;

    RETURN QUERY SELECT 1 /* ControlAction.Applied */::SMALLINT, v_to_status, v_version + 1;
END;
$$;
