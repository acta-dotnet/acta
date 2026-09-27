CREATE OR REPLACE FUNCTION {{schema}}.restart_job(
    p_id BIGINT,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_code SMALLINT,
    p_reason_message VARCHAR,
    p_next_run_at_utc TIMESTAMPTZ DEFAULT NULL,
    p_expected_version INT DEFAULT NULL
)
RETURNS TABLE (action SMALLINT, status_code SMALLINT, version INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_from_status SMALLINT;
    v_version INT;
    v_namespace_id INT;
    v_lineage_root_id BIGINT;
    v_definition_id INT;
    v_tenant_id INT;
    v_execution_number INT;
    v_audit_level SMALLINT;
    v_job_ref UUID;
    v_parent_id BIGINT;
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

    SELECT
        r.status_code, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number,
        j.audit_level_code, j.job_ref, r.version, j.parent_id
    INTO
        v_from_status, v_namespace_id, v_lineage_root_id, v_definition_id, v_tenant_id, v_execution_number,
        v_audit_level, v_job_ref, v_version, v_parent_id
    FROM {{schema}}.runtimes r
    JOIN {{schema}}.jobs j ON j.id = r.job_id
    WHERE r.job_id = p_id
    FOR UPDATE OF r;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* ControlAction.NotFound */::SMALLINT, NULL::SMALLINT, NULL::INT;
        RETURN;
    END IF;

    IF p_expected_version IS NOT NULL AND v_version <> p_expected_version THEN
        RETURN QUERY SELECT 5 /* ControlAction.VersionConflict */::SMALLINT, v_from_status, v_version;
        RETURN;
    END IF;

    IF v_from_status = 50 /* JobStatusCode.Executing */ THEN
        RETURN QUERY SELECT 3 /* ControlAction.Rejected */::SMALLINT, v_from_status, v_version;
        RETURN;
    END IF;

    -- A finished laned job is not reactivated while an unfinished ancestor or descendant sits in its lane,
    -- the state the enqueue ancestor guard refuses (docs/internals/sql-execution-policy.md, "Lane lock order").
    IF v_lane_id IS NOT NULL
        AND v_from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        AND EXISTS (
            WITH RECURSIVE
                ancestors AS (
                    SELECT a.id, a.parent_id
                    FROM {{schema}}.jobs a
                    WHERE a.id = v_parent_id
                    UNION ALL
                    SELECT a.id, a.parent_id
                    FROM {{schema}}.jobs a
                    INNER JOIN ancestors c ON a.id = c.parent_id
                ),
                descendants AS (
                    SELECT d.id
                    FROM {{schema}}.jobs d
                    WHERE d.parent_id = p_id
                    UNION ALL
                    SELECT d.id
                    FROM {{schema}}.jobs d
                    INNER JOIN descendants c ON d.parent_id = c.id
                ),
                lineage AS (
                    SELECT a.id FROM ancestors a
                    UNION ALL
                    SELECT d.id FROM descendants d
                )
            SELECT 1
            FROM lineage c
            INNER JOIN {{schema}}.runtimes lr ON lr.job_id = c.id
            WHERE
                lr.lane_id = v_lane_id
                AND lr.status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                )
        ) THEN
        RETURN QUERY SELECT 3 /* ControlAction.Rejected */::SMALLINT, v_from_status, v_version;
        RETURN;
    END IF;

    -- A laned job restarts Ready unless another member runs, or it was not running and an older member
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
        status_code = v_to_status,
        failure_count = 0,
        next_run_at_utc = COALESCE(p_next_run_at_utc, now()),
        leased_by_worker_id = NULL,
        lease_expires_at_utc = NULL,
        retention_until_utc = NULL,
        modified_at_utc = now(),
        version = r.version + 1
    WHERE r.job_id = p_id;

    IF v_audit_level = 20 /* JobAuditLevelCode.Audit */ THEN
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
            73 /* EventCode.JobRestarted */,
            now(),
            v_namespace_id,
            p_actor_code,
            p_actor_key,
            p_id,
            v_job_ref,
            v_execution_number,
            COALESCE(v_lineage_root_id, p_id),
            v_definition_id,
            v_tenant_id,
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
