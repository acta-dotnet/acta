SELECT j.id
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.namespaces ns ON ns.id = j.namespace_id
WHERE
    ns.name = @p_namespace_name
    AND j.deduplication_key = @p_deduplication_key
    AND j.parent_id IS NULL;
