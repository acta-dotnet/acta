-- Apply one normalized tag write atomically to the resolved target.
DROP TABLE IF EXISTS temp._tag_target;

CREATE TEMP TABLE _tag_target AS
SELECT t.id AS scope_id
FROM {{schema}}.tenants t
WHERE @p_scope_code = 20 /* TagScopeCode.Tenant */ AND t.tenant_name = @p_lookup_name
UNION ALL
SELECT n.id
FROM {{schema}}.namespaces n
WHERE @p_scope_code = 30 /* TagScopeCode.Namespace */ AND n.name = @p_lookup_name
UNION ALL
SELECT d.id
FROM {{schema}}.definitions d
WHERE @p_scope_code = 40 /* TagScopeCode.Definition */ AND d.id = @p_lookup_id
UNION ALL
SELECT j.id
FROM {{schema}}.jobs j
WHERE @p_scope_code = 50 /* TagScopeCode.Job */ AND j.id = @p_lookup_id;

DELETE FROM {{schema}}.tags
WHERE
    scope_code = @p_scope_code
    AND scope_id = (SELECT scope_id FROM temp._tag_target)
    AND name IN (SELECT json_extract(value, '$.name') FROM json_each(@p_items_json));

SELECT
    CASE
        WHEN EXISTS (SELECT 1 FROM temp._tag_target) THEN 1 /* ControlAction.Applied */
        ELSE 2 /* ControlAction.NotFound */
    END AS action;
