SELECT
    {{decode:tag-scope:t.scope_code}} AS scope,
    t.scope_code,
    t.scope_id,
    ns.name AS namespace,
    t.namespace_id,
    t.name AS tag_name,
    t.value AS tag_value,
    t.value_search AS tag_value_search
FROM {{schema}}.tags AS t
LEFT JOIN {{schema}}.namespaces AS ns ON ns.id = t.namespace_id
