-- Apply one normalized tag write atomically to the resolved target.
DROP TABLE IF EXISTS temp._tag_target;

CREATE TEMP TABLE _tag_target AS
SELECT t.tenant_id AS entity_id
FROM {{schema}}.tenants t
WHERE @p_entity_code = 120 /* EntityCode.Tenant */ AND t.tenant_name = @p_lookup_name
UNION ALL
SELECT n.namespace_id
FROM {{schema}}.namespaces n
WHERE @p_entity_code = 80 /* EntityCode.Namespace */ AND n.namespace_name = @p_lookup_name
UNION ALL
SELECT d.definition_id
FROM {{schema}}.definitions d
WHERE @p_entity_code = 30 /* EntityCode.Definition */ AND d.definition_id = @p_lookup_id
UNION ALL
SELECT j.job_id
FROM {{schema}}.jobs j
WHERE @p_entity_code = 50 /* EntityCode.Job */ AND j.job_id = @p_lookup_id;

DELETE FROM {{schema}}.tags
WHERE
    entity_code = @p_entity_code
    AND entity_id = (SELECT entity_id FROM temp._tag_target)
    AND tag_name IN (SELECT json_extract(value, '$.tag_name') FROM json_each(@p_items_json));

SELECT
    CASE
        WHEN EXISTS (SELECT 1 FROM temp._tag_target) THEN 1 /* ControlAction.Applied */
        ELSE 2 /* ControlAction.NotFound */
    END AS action;
