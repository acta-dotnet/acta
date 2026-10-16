WITH target (scope_id, namespace_id) AS (
    SELECT t.id::bigint, NULL::int
    FROM {{schema}}.tenants t
    WHERE @p_scope_code = 20 /* TagScopeCode.Tenant */ AND t.tenant_name = @p_lookup_name
    UNION ALL
    SELECT n.id::bigint, n.id
    FROM {{schema}}.namespaces n
    WHERE @p_scope_code = 30 /* TagScopeCode.Namespace */ AND n.name = @p_lookup_name
    UNION ALL
    SELECT d.id::bigint, d.namespace_id
    FROM {{schema}}.definitions d
    WHERE @p_scope_code = 40 /* TagScopeCode.Definition */ AND d.id = @p_lookup_id
    UNION ALL
    SELECT j.id, j.namespace_id
    FROM {{schema}}.jobs j
    WHERE @p_scope_code = 50 /* TagScopeCode.Job */ AND j.id = @p_lookup_id
)
SELECT tg.name, tg.value
FROM target x
LEFT JOIN {{schema}}.tags tg
    ON tg.scope_code = @p_scope_code AND tg.scope_id = x.scope_id
ORDER BY tg.name;
