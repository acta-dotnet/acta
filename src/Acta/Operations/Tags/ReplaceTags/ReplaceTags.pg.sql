-- One batch, one implicit transaction; only the final SELECT returns rows. The target row is locked first,
-- so tag writes on one target run one at a time.
DROP TABLE IF EXISTS pg_temp._replace_tags_target;
CREATE TEMP TABLE _replace_tags_target (scope_id BIGINT NOT NULL, namespace_id INT) ON COMMIT DROP;

INSERT INTO _replace_tags_target (scope_id, namespace_id)
SELECT t.id, NULL
FROM {{schema}}.tenants t
WHERE @p_scope_code = 20 /* TagScopeCode.Tenant */ AND t.tenant_name = @p_lookup_name
FOR UPDATE;

INSERT INTO _replace_tags_target (scope_id, namespace_id)
SELECT n.id, n.id
FROM {{schema}}.namespaces n
WHERE @p_scope_code = 30 /* TagScopeCode.Namespace */ AND n.name = @p_lookup_name
FOR UPDATE;

INSERT INTO _replace_tags_target (scope_id, namespace_id)
SELECT d.id, d.namespace_id
FROM {{schema}}.definitions d
WHERE @p_scope_code = 40 /* TagScopeCode.Definition */ AND d.id = @p_lookup_id
FOR UPDATE;

INSERT INTO _replace_tags_target (scope_id, namespace_id)
SELECT j.id, j.namespace_id
FROM {{schema}}.jobs j
WHERE @p_scope_code = 50 /* TagScopeCode.Job */ AND j.id = @p_lookup_id
FOR UPDATE;

INSERT INTO _replace_tags_target (scope_id, namespace_id)
SELECT s.id, s.namespace_id
FROM {{schema}}.schedules s
WHERE @p_scope_code = 60 /* TagScopeCode.Schedule */ AND s.job_id = @p_lookup_id AND s.name = @p_lookup_name
FOR UPDATE;

INSERT INTO _replace_tags_target (scope_id, namespace_id)
SELECT w.id, w.namespace_id
FROM {{schema}}.workers w
WHERE @p_scope_code = 70 /* TagScopeCode.Worker */ AND w.id = @p_lookup_id
FOR UPDATE;

INSERT INTO _replace_tags_target (scope_id, namespace_id)
SELECT a.id, a.namespace_id
FROM {{schema}}.alerts a
WHERE @p_scope_code = 80 /* TagScopeCode.Alert */ AND a.id = @p_lookup_id
FOR UPDATE;

INSERT INTO _replace_tags_target (scope_id, namespace_id)
SELECT e.id, e.namespace_id
FROM {{schema}}.events e
WHERE @p_scope_code = 90 /* TagScopeCode.Event */ AND e.id = @p_lookup_id
FOR UPDATE;

DELETE FROM {{schema}}.tags t
USING _replace_tags_target x
WHERE t.scope_code = @p_scope_code AND t.scope_id = x.scope_id;

INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
SELECT @p_scope_code, x.scope_id, x.namespace_id, item->>'name', item->>'value', item->>'value_search'
FROM _replace_tags_target x
CROSS JOIN jsonb_array_elements(@p_items_json::JSONB) item;

SELECT
    CASE WHEN EXISTS (SELECT 1 FROM _replace_tags_target) THEN 1 /* TagMutationAction.Applied */ ELSE 2 /* TagMutationAction.NotFound */ END::SMALLINT AS action;
