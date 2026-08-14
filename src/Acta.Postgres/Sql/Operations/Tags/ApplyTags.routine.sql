CREATE OR REPLACE FUNCTION {{schema}}.apply_tags(
    p_scope_code SMALLINT,
    p_lookup_id BIGINT,
    p_lookup_name VARCHAR,
    p_mutation SMALLINT,
    p_items_json VARCHAR
)
RETURNS TABLE (action SMALLINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_scope_id BIGINT;
    v_namespace_id SMALLINT;
BEGIN
    CASE p_scope_code
        WHEN 20 /* TagScopeCode.Tenant */ THEN
            SELECT t.id::BIGINT, NULL::SMALLINT INTO v_scope_id, v_namespace_id
            FROM {{schema}}.tenants t
            WHERE t.tenant_key = p_lookup_name
            FOR UPDATE;
        WHEN 30 /* TagScopeCode.Namespace */ THEN
            SELECT n.id::BIGINT, n.id INTO v_scope_id, v_namespace_id
            FROM {{schema}}.namespaces n
            WHERE n.name = p_lookup_name
            FOR UPDATE;
        WHEN 40 /* TagScopeCode.Definition */ THEN
            SELECT d.id::BIGINT, d.namespace_id INTO v_scope_id, v_namespace_id
            FROM {{schema}}.definitions d
            WHERE d.id = p_lookup_id
            FOR UPDATE;
        WHEN 50 /* TagScopeCode.Job */ THEN
            SELECT j.id, j.namespace_id INTO v_scope_id, v_namespace_id
            FROM {{schema}}.jobs j
            WHERE j.id = p_lookup_id
            FOR UPDATE;
        WHEN 60 /* TagScopeCode.Schedule */ THEN
            SELECT s.id, s.namespace_id INTO v_scope_id, v_namespace_id
            FROM {{schema}}.schedules s
            WHERE
                s.job_id = p_lookup_id
                AND s.name = p_lookup_name
            FOR UPDATE;
        WHEN 70 /* TagScopeCode.Worker */ THEN
            SELECT w.id::BIGINT, w.namespace_id INTO v_scope_id, v_namespace_id
            FROM {{schema}}.workers w
            WHERE w.id = p_lookup_id
            FOR UPDATE;
        WHEN 80 /* TagScopeCode.Alert */ THEN
            SELECT a.id, a.namespace_id INTO v_scope_id, v_namespace_id
            FROM {{schema}}.alerts a
            WHERE a.id = p_lookup_id
            FOR UPDATE;
        WHEN 90 /* TagScopeCode.Event */ THEN
            SELECT e.id, e.namespace_id INTO v_scope_id, v_namespace_id
            FROM {{schema}}.events e
            WHERE e.id = p_lookup_id
            FOR UPDATE;
        ELSE
            RAISE EXCEPTION 'Unsupported tag scope code %.', p_scope_code;
    END CASE;

    IF v_scope_id IS NULL THEN
        RETURN QUERY SELECT 2 /* TagMutationAction.NotFound */::SMALLINT;
        RETURN;
    END IF;

    IF p_mutation = 1 /* TagMutationKind.Replace */ THEN
        DELETE FROM {{schema}}.tags
        WHERE
            scope_code = p_scope_code
            AND scope_id = v_scope_id;

        INSERT INTO {{schema}}.tags(scope_code, scope_id, namespace_id, name, value, value_search)
        SELECT
            p_scope_code,
            v_scope_id,
            v_namespace_id,
            item->>'name',
            item->>'value',
            item->>'value_search'
        FROM jsonb_array_elements(p_items_json::JSONB) item;
    ELSIF p_mutation = 2 /* TagMutationKind.Upsert */ THEN
        IF EXISTS (
            SELECT 1
            FROM jsonb_array_elements(p_items_json::JSONB) item
            WHERE NOT EXISTS (
                SELECT 1 FROM {{schema}}.tags t
                WHERE
                    t.scope_code = p_scope_code
                    AND t.scope_id = v_scope_id
                    AND t.name = item->>'name'))
            AND (SELECT COUNT(*) FROM {{schema}}.tags t
                WHERE t.scope_code = p_scope_code AND t.scope_id = v_scope_id) >= 32 THEN
            RAISE EXCEPTION 'A target may carry at most 32 tags.';
        END IF;

        INSERT INTO {{schema}}.tags(scope_code, scope_id, namespace_id, name, value, value_search)
        SELECT
            p_scope_code,
            v_scope_id,
            v_namespace_id,
            item->>'name',
            item->>'value',
            item->>'value_search'
        FROM jsonb_array_elements(p_items_json::JSONB) item
        ON CONFLICT (scope_code, scope_id, name) DO UPDATE SET
            namespace_id = EXCLUDED.namespace_id,
            value = EXCLUDED.value,
            value_search = EXCLUDED.value_search;
    ELSIF p_mutation = 3 /* TagMutationKind.Remove */ THEN
        DELETE FROM {{schema}}.tags t
        USING jsonb_array_elements(p_items_json::JSONB) item
        WHERE
            t.scope_code = p_scope_code
            AND t.scope_id = v_scope_id
            AND t.name = item->>'name';
    ELSE
        RAISE EXCEPTION 'Unsupported tag mutation code %.', p_mutation;
    END IF;

    RETURN QUERY SELECT 1 /* TagMutationAction.Applied */::SMALLINT;
END;
$$;
