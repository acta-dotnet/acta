SELECT
    s.schedule_id,
    ns.namespace_name AS namespace,
    s.job_id,
    j.job_ref,
    s.definition_id,
    d.definition_name AS job_name,
    s.schedule_name,
    {{decode:schedule-origin:s.schedule_origin_code}} AS origin,
    s.schedule_origin_code,
    {{decode:schedule-status:s.schedule_status_code}} AS status,
    s.schedule_status_code,
    s.expression,
    s.time_zone_id,
    {{decode:schedule-expression-kind:s.expression_kind_code}} AS expression_kind,
    s.expression_kind_code,
    {{decode:misfire-strategy:s.misfire_strategy_code}} AS misfire_strategy,
    s.misfire_strategy_code,
    st.next_occurrence_at_utc,
    st.decided_through_at_utc,
    st.next_evaluation_at_utc,
    {{decode:schedule-evaluation-status:st.evaluation_status_code}} AS evaluation_status,
    st.evaluation_status_code,
    s.paused_until_utc,
    s.description,
    s.pause_message,
    s.created_at_utc,
    s.modified_at_utc,
    s.version
FROM {{schema}}.schedules AS s
JOIN {{schema}}.schedules_state AS st ON st.schedule_id = s.schedule_id
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = s.namespace_id
JOIN {{schema}}.jobs AS j ON j.job_id = s.job_id
JOIN {{schema}}.definitions AS d ON d.definition_id = s.definition_id
