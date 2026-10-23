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

SELECT
    tg.tag_name,
    tg.tag_value
FROM target x
LEFT JOIN {{schema}}.tags tg
    ON tg.entity_code = @p_entity_code AND tg.entity_id = x.entity_id
ORDER BY tg.tag_name;
