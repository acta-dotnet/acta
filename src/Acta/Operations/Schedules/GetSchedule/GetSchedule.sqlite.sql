-- The schedule a path names, in any status, projected as the schedules list projects its rows.
SELECT
    s.schedule_id,
    s.job_id,
    s.definition_id,
    ns.namespace_name AS job_namespace,
    jd.definition_name AS job_name,
    s.schedule_name,
    s.schedule_origin_code AS origin,
    s.expression_kind_code AS expression_kind,
    s.expression,
    s.time_zone_id,
    s.misfire_strategy_code AS misfire_strategy,
    st.next_occurrence_at_utc,
    st.next_evaluation_at_utc,
    st.evaluation_status_code AS evaluation_status,
    s.schedule_status_code AS status,
    s.paused_until_utc,
    s.created_at_utc,
    s.modified_at_utc,
    s.version,
    sj.job_ref,
    s.max_outstanding,
    st.outstanding_count,
    s.full_behavior_code AS full_behavior,
    jd.scheduled_delivery_code AS scheduled_delivery,
    s.expire_after_seconds,
    s.expire_action_code AS expire_action,
    s.availability_calendar_name
FROM {{schema}}.namespaces ns
JOIN {{schema}}.definitions jd ON jd.namespace_id = ns.namespace_id AND jd.definition_name = @p_job_name
JOIN {{schema}}.schedules s ON s.definition_id = jd.definition_id AND s.schedule_name = @p_schedule_name
LEFT JOIN {{schema}}.schedules_state st ON st.schedule_id = s.schedule_id
LEFT JOIN {{schema}}.jobs sj ON sj.job_id = s.job_id
WHERE ns.namespace_name = @p_namespace_name;
