SELECT
    s.id AS schedule_id,
    ns.name AS namespace,
    s.job_id,
    j.job_ref,
    s.definition_id,
    d.name AS job_name,
    s.name AS schedule_name,
    {{decode:schedule-origin:s.origin_code}} AS origin,
    s.origin_code,
    {{decode:schedule-status:s.status_code}} AS status,
    s.status_code,
    s.expression_effective,
    s.expression,
    s.expression_override,
    s.time_zone_id_effective,
    s.time_zone_id,
    s.time_zone_id_override,
    {{decode:schedule-expression-kind:s.expression_kind_code}} AS expression_kind,
    s.expression_kind_code,
    {{decode:misfire-strategy:s.misfire_strategy_code}} AS misfire_strategy,
    s.misfire_strategy_code,
    s.next_run_at_utc,
    s.last_occurrence_at_utc,
    s.paused_until_utc,
    s.description,
    s.reason_message,
    s.created_at_utc,
    s.modified_at_utc,
    s.version
FROM {{schema}}.schedules AS s
JOIN {{schema}}.namespaces AS ns ON ns.id = s.namespace_id
JOIN {{schema}}.jobs AS j ON j.id = s.job_id
JOIN {{schema}}.definitions AS d ON d.id = s.definition_id
