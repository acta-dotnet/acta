CREATE OR REPLACE FUNCTION {{schema}}.purge_job(
    p_id BIGINT,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_code SMALLINT
)
RETURNS TABLE (action SMALLINT, status_code SMALLINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_from_status SMALLINT;
    v_namespace_id SMALLINT;
    v_definition INT;
    v_tenant INT;
    v_job_ref UUID;
    v_job_name VARCHAR;
BEGIN
    SELECT r.status_code, j.namespace_id, j.definition_id, j.tenant_id, j.job_ref, d.name
    INTO v_from_status, v_namespace_id, v_definition, v_tenant, v_job_ref, v_job_name
    FROM {{schema}}.jobs j
    JOIN {{schema}}.runtimes r ON r.job_id = j.id
    JOIN {{schema}}.definitions d ON d.id = j.definition_id
    WHERE j.id = p_id
    FOR UPDATE OF j, r;

    IF NOT FOUND THEN
        RETURN QUERY SELECT 2 /* JobControlAction.NotFound */::SMALLINT, NULL::SMALLINT;
        RETURN;
    END IF;

    IF v_from_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN
        RETURN QUERY SELECT 3 /* JobControlAction.Rejected */::SMALLINT, v_from_status;
        RETURN;
    END IF;

    -- parent_id carries no DB FK/cascade; purging a job that has child jobs would orphan the child's
    -- lineage (parent_id / lineage_root_id would point at a row that no longer exists), so reject.
    IF EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_id = p_id) THEN
        RETURN QUERY SELECT 3 /* JobControlAction.Rejected */::SMALLINT, v_from_status;
        RETURN;
    END IF;

    -- Canonical deletion lock order: job (above), schedules, alerts, events, tags, then targets.
    PERFORM 1 FROM {{schema}}.schedules s WHERE s.job_id = p_id ORDER BY s.id FOR UPDATE;
    PERFORM 1 FROM {{schema}}.alerts a WHERE a.job_id = p_id ORDER BY a.id FOR UPDATE;
    PERFORM 1 FROM {{schema}}.events e WHERE e.job_id = p_id ORDER BY e.id FOR UPDATE;

    DELETE FROM {{schema}}.tags t
    WHERE
        (t.scope_code = 50 /* TagScopeCode.Job */ AND t.scope_id = p_id)
        OR (t.scope_code = 60 /* TagScopeCode.Schedule */ AND t.scope_id IN (
            SELECT s.id FROM {{schema}}.schedules s WHERE s.job_id = p_id))
        OR (t.scope_code = 80 /* TagScopeCode.Alert */ AND t.scope_id IN (
            SELECT a.id FROM {{schema}}.alerts a WHERE a.job_id = p_id))
        OR (t.scope_code = 90 /* TagScopeCode.Event */ AND t.scope_id IN (
            SELECT e.id FROM {{schema}}.events e WHERE e.job_id = p_id));

    DELETE FROM {{schema}}.events WHERE job_id = p_id;
    DELETE FROM {{schema}}.alerts WHERE job_id = p_id;
    DELETE FROM {{schema}}.jobs WHERE id = p_id;

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
        75 /* JobEventCode.JobPurged */,
        now(),
        v_namespace_id,
        p_actor_code,
        p_actor_key,
        NULL,
        NULL,
        NULL,
        NULL,
        v_definition,
        v_tenant,
        NULL,
        v_from_status,
        NULL,
        NULL,
        NULL,
        p_reason_code,
        'purged ' || v_job_ref::text || ' (' || v_job_name || ')');

    RETURN QUERY SELECT 1 /* JobControlAction.Applied */::SMALLINT, NULL::SMALLINT;
END;
$$;
