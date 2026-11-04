-- One keyset page of the target's tags by name. SQLite compares text bytewise, so the name orders ordinally as it stands.
WITH target (entity_id) AS (
    SELECT t.tenant_id
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
    WHERE @p_entity_code = 50 /* EntityCode.Job */ AND j.job_id = @p_lookup_id
)

SELECT
    tg.tag_name AS name,
    tg.tag_value AS value
FROM target x
INNER JOIN {{schema}}.tags tg ON tg.entity_code = @p_entity_code AND tg.entity_id = x.entity_id
WHERE @p_cursor_tag_name IS NULL OR tg.tag_name > @p_cursor_tag_name
ORDER BY tg.tag_name
LIMIT @p_take;

WITH target (entity_id) AS (
    SELECT t.tenant_id
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
    WHERE @p_entity_code = 50 /* EntityCode.Job */ AND j.job_id = @p_lookup_id
)

SELECT CASE WHEN @p_include_total IS NOT NULL THEN (
    SELECT COUNT(*)
    FROM target x
    INNER JOIN {{schema}}.tags tg ON tg.entity_code = @p_entity_code AND tg.entity_id = x.entity_id
) END;
