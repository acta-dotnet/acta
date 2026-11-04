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

SELECT acta_error('A target may carry at most 32 tags.')
WHERE
    EXISTS (SELECT 1 FROM temp._tag_target)
    AND EXISTS (
        SELECT 1
        FROM json_each(@p_items_json) i
        WHERE NOT EXISTS (
            SELECT 1
            FROM {{schema}}.tags t, temp._tag_target x
            WHERE
                t.entity_code = @p_entity_code
                AND t.entity_id = x.entity_id
                AND t.tag_name = json_extract(i.value, '$.tag_name')))
    AND (
        SELECT COUNT(*)
        FROM {{schema}}.tags t, temp._tag_target x
        WHERE t.entity_code = @p_entity_code AND t.entity_id = x.entity_id
    ) >= 32;

INSERT INTO {{schema}}.tags (entity_code, entity_id, tag_name, tag_value, tag_value_search)
SELECT
    @p_entity_code,
    x.entity_id,
    json_extract(i.value, '$.tag_name'),
    json_extract(i.value, '$.tag_value'),
    json_extract(i.value, '$.tag_value_search')
FROM temp._tag_target x, json_each(@p_items_json) i
WHERE true
ON CONFLICT (entity_code, entity_id, tag_name) DO UPDATE SET
    tag_value = excluded.tag_value,
    tag_value_search = excluded.tag_value_search;

SELECT
    CASE
        WHEN EXISTS (SELECT 1 FROM temp._tag_target) THEN 1 /* ControlAction.Applied */
        ELSE 2 /* ControlAction.NotFound */
    END AS action;
