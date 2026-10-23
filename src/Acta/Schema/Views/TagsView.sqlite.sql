SELECT
    {{decode:entity:t.entity_code}} AS entity_kind,
    t.entity_code,
    t.entity_id,
    t.tag_name,
    t.tag_value,
    t.tag_value_search
FROM {{schema}}.tags AS t
