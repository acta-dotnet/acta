-- Scoped upsert, last write wins: targets resolve the scope (none = Global, namespace alone =
-- Namespace, namespace + job name = Definition); unregistered targets are NotFound. Parameter ORDER
-- is fixed (positional invocation); the name is validated upstream so the detail JSON stays literal.
CREATE OR REPLACE FUNCTION {{schema}}.set_setting(
    p_name VARCHAR,
    p_value_format_id SMALLINT,
    p_value BYTEA,
    p_description VARCHAR,
    p_namespace_name VARCHAR,
    p_job_name VARCHAR,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_message VARCHAR
)
RETURNS TABLE (action SMALLINT, version INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_scope_code SMALLINT := 10 /* SettingScopeCode.Global */;
    v_namespace_id SMALLINT;
    v_definition_id INT;
    v_scope_id INT;
    v_version INT;
BEGIN
    IF p_namespace_name IS NOT NULL THEN
        SELECT n.id INTO v_namespace_id FROM {{schema}}.namespaces n WHERE n.name = p_namespace_name;
        IF NOT FOUND THEN
            RETURN QUERY SELECT 2 /* AdminControlAction.NotFound */::SMALLINT, NULL::INT;
            RETURN;
        END IF;
        IF p_job_name IS NULL THEN
            v_scope_code := 30 /* SettingScopeCode.Namespace */;
            v_scope_id := v_namespace_id;
        ELSE
            SELECT d.id INTO v_definition_id
            FROM {{schema}}.definitions d
            WHERE
                d.namespace_id = v_namespace_id
                AND d.name = p_job_name;
            IF NOT FOUND THEN
                RETURN QUERY SELECT 2 /* AdminControlAction.NotFound */::SMALLINT, NULL::INT;
                RETURN;
            END IF;
            v_scope_code := 40 /* SettingScopeCode.Definition */;
            v_scope_id := v_definition_id;
        END IF;
    END IF;

    UPDATE {{schema}}.settings s
    SET
        value_format_id = p_value_format_id,
        value = p_value,
        description = p_description,
        modified_at_utc = now(),
        version = s.version + 1
    WHERE
        s.scope_code = v_scope_code
        AND s.scope_id IS NOT DISTINCT FROM v_scope_id
        AND s.name = p_name
    RETURNING s.version INTO v_version;

    IF NOT FOUND THEN
        IF v_scope_id IS NULL THEN
            INSERT INTO {{schema}}.settings AS s (
                scope_code,
                scope_id,
                name,
                value_format_id,
                value,
                description,
                created_at_utc,
                modified_at_utc,
                version)
            VALUES (
                v_scope_code,
                NULL,
                p_name,
                p_value_format_id,
                p_value,
                p_description,
                now(),
                now(),
                0)
            ON CONFLICT (scope_code, name) WHERE scope_id IS NULL DO UPDATE SET
                value_format_id = EXCLUDED.value_format_id,
                value = EXCLUDED.value,
                description = EXCLUDED.description,
                modified_at_utc = now(),
                version = s.version + 1
            RETURNING s.version INTO v_version;
        ELSE
            INSERT INTO {{schema}}.settings AS s (
                scope_code,
                scope_id,
                name,
                value_format_id,
                value,
                description,
                created_at_utc,
                modified_at_utc,
                version)
            VALUES (
                v_scope_code,
                v_scope_id,
                p_name,
                p_value_format_id,
                p_value,
                p_description,
                now(),
                now(),
                0)
            ON CONFLICT (scope_code, scope_id, name) WHERE scope_id IS NOT NULL DO UPDATE SET
                value_format_id = EXCLUDED.value_format_id,
                value = EXCLUDED.value,
                description = EXCLUDED.description,
                modified_at_utc = now(),
                version = s.version + 1
            RETURNING s.version INTO v_version;
        END IF;
    END IF;

    -- namespace_id 1 is the seeded sys namespace (M001); detail identifies the setting by name.
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
        reason_message,
        detail_format_id,
        detail)
    VALUES (
        160 /* EventCode.SettingUpdated */,
        now(),
        COALESCE(v_namespace_id, 1),
        p_actor_code,
        p_actor_key,
        NULL,
        NULL,
        NULL,
        NULL,
        v_definition_id,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        NULL,
        p_reason_message,
        1 /* JobPayloadFormat.Json */,
        convert_to('{"name":"' || p_name || '"}', 'UTF8'));

    RETURN QUERY SELECT 1 /* AdminControlAction.Applied */::SMALLINT, v_version;
END;
$$;
