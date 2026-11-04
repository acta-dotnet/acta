-- One keyset page of the target's tags by name. The name is ordered and compared under a binary collation, so the pages
-- run in ordinal order whatever the database's collation; the key still seeks the target, whose few tags sort after.
WITH target (entity_id) AS (
    SELECT CONVERT(BIGINT, t.tenant_id)
    FROM {{schema}}.tenants t
    WHERE @p_entity_code = 120 /* EntityCode.Tenant */ AND t.tenant_name = @p_lookup_name
    UNION ALL
    SELECT CONVERT(BIGINT, n.namespace_id)
    FROM {{schema}}.namespaces n
    WHERE @p_entity_code = 80 /* EntityCode.Namespace */ AND n.namespace_name = @p_lookup_name
    UNION ALL
    SELECT CONVERT(BIGINT, d.definition_id)
    FROM {{schema}}.definitions d
    WHERE @p_entity_code = 30 /* EntityCode.Definition */ AND d.definition_id = @p_lookup_id
    UNION ALL
    SELECT j.job_id
    FROM {{schema}}.jobs j
    WHERE @p_entity_code = 50 /* EntityCode.Job */ AND j.job_id = @p_lookup_id
)

SELECT TOP (@p_take)
    tg.tag_name AS name,
    tg.tag_value AS value
FROM target x
INNER JOIN {{schema}}.tags tg ON tg.entity_code = @p_entity_code AND tg.entity_id = x.entity_id
WHERE @p_cursor_tag_name IS NULL OR tg.tag_name COLLATE Latin1_General_100_BIN2_UTF8 > @p_cursor_tag_name
ORDER BY tg.tag_name COLLATE Latin1_General_100_BIN2_UTF8;

WITH target (entity_id) AS (
    SELECT CONVERT(BIGINT, t.tenant_id)
    FROM {{schema}}.tenants t
    WHERE @p_entity_code = 120 /* EntityCode.Tenant */ AND t.tenant_name = @p_lookup_name
    UNION ALL
    SELECT CONVERT(BIGINT, n.namespace_id)
    FROM {{schema}}.namespaces n
    WHERE @p_entity_code = 80 /* EntityCode.Namespace */ AND n.namespace_name = @p_lookup_name
    UNION ALL
    SELECT CONVERT(BIGINT, d.definition_id)
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
