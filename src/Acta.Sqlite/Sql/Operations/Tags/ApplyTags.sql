-- Apply one normalized tag mutation atomically to the resolved target.
DROP TABLE IF EXISTS temp._tag_target;

CREATE TEMP TABLE _tag_target AS
SELECT t.id AS scope_id, CAST(NULL AS INTEGER) AS namespace_id
FROM {{schema}}.tenants t
WHERE @p_scope_code = 20 /* TagScopeCode.Tenant */ AND t.tenant_key = @p_lookup_name
UNION ALL
SELECT n.id, n.id
FROM {{schema}}.namespaces n
WHERE @p_scope_code = 30 /* TagScopeCode.Namespace */ AND n.name = @p_lookup_name
UNION ALL
SELECT d.id, d.namespace_id
FROM {{schema}}.definitions d
WHERE @p_scope_code = 40 /* TagScopeCode.Definition */ AND d.id = @p_lookup_id
UNION ALL
SELECT j.id, j.namespace_id
FROM {{schema}}.jobs j
WHERE @p_scope_code = 50 /* TagScopeCode.Job */ AND j.id = @p_lookup_id
UNION ALL
SELECT s.id, s.namespace_id
FROM {{schema}}.schedules s
WHERE @p_scope_code = 60 /* TagScopeCode.Schedule */ AND s.job_id = @p_lookup_id AND s.name = @p_lookup_name
UNION ALL
SELECT w.id, w.namespace_id
FROM {{schema}}.workers w
WHERE @p_scope_code = 70 /* TagScopeCode.Worker */ AND w.id = @p_lookup_id
UNION ALL
SELECT a.id, a.namespace_id
FROM {{schema}}.alerts a
WHERE @p_scope_code = 80 /* TagScopeCode.Alert */ AND a.id = @p_lookup_id
UNION ALL
SELECT e.id, e.namespace_id
FROM {{schema}}.events e
WHERE @p_scope_code = 90 /* TagScopeCode.Event */ AND e.id = @p_lookup_id;

SELECT acta_error('A target may carry at most 32 tags.')
WHERE
    @p_mutation = 2 /* TagMutationKind.Upsert */
    AND EXISTS (SELECT 1 FROM temp._tag_target)
    AND EXISTS (
        SELECT 1
        FROM json_each(@p_items_json) i
        WHERE NOT EXISTS (
            SELECT 1
            FROM {{schema}}.tags t, temp._tag_target x
            WHERE
                t.scope_code = @p_scope_code
                AND t.scope_id = x.scope_id
                AND t.name = json_extract(i.value, '$.name')))
    AND (
        SELECT COUNT(*)
        FROM {{schema}}.tags t, temp._tag_target x
        WHERE t.scope_code = @p_scope_code AND t.scope_id = x.scope_id
    ) >= 32;

DELETE FROM {{schema}}.tags
WHERE
    @p_mutation = 1 /* TagMutationKind.Replace */
    AND scope_code = @p_scope_code
    AND scope_id = (SELECT scope_id FROM temp._tag_target);

INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
SELECT
    @p_scope_code,
    x.scope_id,
    x.namespace_id,
    json_extract(i.value, '$.name'),
    json_extract(i.value, '$.value'),
    json_extract(i.value, '$.value_search')
FROM temp._tag_target x, json_each(@p_items_json) i
WHERE @p_mutation = 1 /* TagMutationKind.Replace */;

INSERT INTO {{schema}}.tags (scope_code, scope_id, namespace_id, name, value, value_search)
SELECT
    @p_scope_code,
    x.scope_id,
    x.namespace_id,
    json_extract(i.value, '$.name'),
    json_extract(i.value, '$.value'),
    json_extract(i.value, '$.value_search')
FROM temp._tag_target x, json_each(@p_items_json) i
WHERE @p_mutation = 2 /* TagMutationKind.Upsert */
ON CONFLICT (scope_code, scope_id, name) DO UPDATE SET
    namespace_id = excluded.namespace_id,
    value = excluded.value,
    value_search = excluded.value_search;

DELETE FROM {{schema}}.tags
WHERE
    @p_mutation = 3 /* TagMutationKind.Remove */
    AND scope_code = @p_scope_code
    AND scope_id = (SELECT scope_id FROM temp._tag_target)
    AND name IN (SELECT json_extract(value, '$.name') FROM json_each(@p_items_json));

SELECT
    CASE
        WHEN EXISTS (SELECT 1 FROM temp._tag_target) THEN 1 /* TagMutationAction.Applied */
        ELSE 2 /* TagMutationAction.NotFound */
    END AS action;
