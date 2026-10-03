CREATE OR REPLACE FUNCTION {{schema}}.pause_job(
    p_id BIGINT,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_code SMALLINT,
    p_reason_message VARCHAR,
    p_expected_version INT DEFAULT NULL
)
RETURNS TABLE (action SMALLINT, status_code SMALLINT, version INT, lane_promoted SMALLINT)
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
    v_lane_id BIGINT;
    v_head_id BIGINT;
    v_head_status SMALLINT;
    v_lane_promoted SMALLINT := 0;
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
    INTO v_from_status, v_namespace_id, v_lineage_root_id, v_definition_id, v_tenant_id, v_execution_number, v_audit_level, v_job_ref, v_version
    FROM {{schema}}.runtimes r
    JOIN {{schema}}.jobs j ON j.id = r.job_id
    WHERE r.job_id = p_id
    FOR UPDATE OF r;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* ControlAction.NotFound */::SMALLINT, NULL::SMALLINT, NULL::INT, 0::SMALLINT;
        RETURN;
    END IF;

    IF p_expected_version IS NOT NULL AND v_version <> p_expected_version THEN
        RETURN QUERY SELECT 5 /* ControlAction.VersionConflict */::SMALLINT, v_from_status, v_version, 0::SMALLINT;
        RETURN;
    END IF;

    -- A Blocked follower may be paused too; a paused member still holds its place in the lane.
    IF v_from_status NOT IN (
        30 /* JobStatusCode.Paused */,
        20 /* JobStatusCode.Suspended */,
        10 /* JobStatusCode.Ready */,
        15 /* JobStatusCode.Blocked */
    ) THEN
        RETURN QUERY SELECT 3 /* ControlAction.Rejected */::SMALLINT, v_from_status, v_version, 0::SMALLINT;
        RETURN;
    END IF;

    -- Aliased and qualified: the RETURNS TABLE output column named version would otherwise make the
    -- bare column reference ambiguous. Same in every job control routine that returns the version.
    UPDATE {{schema}}.runtimes AS r
    SET
        status_code = 30 /* JobStatusCode.Paused */,
        -- Always a next run, which is what marks this pause as held (ScheduleWalker, "held").
        next_run_at_utc = COALESCE(r.next_run_at_utc, now()),
        modified_at_utc = now(),
        version = r.version + 1
    WHERE r.job_id = p_id;

    -- A paused running member leaves its lane with no runner, so the lane hands on as a settle would: to
    -- an older Blocked member, the one a restart left waiting (docs/internals/sql-execution-policy.md, "Lane lock order").
    IF v_lane_id IS NOT NULL AND v_from_status IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */) THEN
        LOOP
            v_head_id := NULL;
            v_head_status := NULL;

            SELECT m.job_id, m.status_code INTO v_head_id, v_head_status
            FROM {{schema}}.runtimes m
            WHERE
                m.lane_id = v_lane_id
                AND m.lane_id IS NOT NULL
                AND m.status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1;

            EXIT WHEN v_head_status IS DISTINCT FROM 15 /* JobStatusCode.Blocked */;
            EXIT WHEN EXISTS (
                SELECT 1
                FROM {{schema}}.runtimes o
                WHERE
                    o.lane_id = v_lane_id
                    AND o.lane_id IS NOT NULL
                    AND o.status_code IN (
                        10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                        40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                    )
            );

            UPDATE {{schema}}.runtimes pr
            SET
                status_code = 10 /* JobStatusCode.Ready */,
                next_run_at_utc = GREATEST(pr.next_run_at_utc, now()),
                modified_at_utc = now(),
                version = pr.version + 1
            WHERE
                pr.job_id = v_head_id
                AND pr.status_code = 15 /* JobStatusCode.Blocked */;

            IF FOUND THEN
                v_lane_promoted := 1;
                EXIT;
            END IF;
        END LOOP;
    END IF;

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
            71 /* EventCode.JobPaused */,
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
            30 /* JobStatusCode.Paused */,
            NULL,
            NULL,
            p_reason_code,
            p_reason_message);
    END IF;

    RETURN QUERY SELECT 1 /* ControlAction.Applied */::SMALLINT, 30 /* JobStatusCode.Paused */::SMALLINT, v_version + 1, v_lane_promoted;
END;
$$;
