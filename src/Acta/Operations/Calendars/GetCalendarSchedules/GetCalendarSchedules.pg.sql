-- Every schedule bound to the family through the catalog, as the admitter's row so a publication plans it the way a
-- pass evaluates it; orphaned schedules and retired definitions included, since their pins move too. A shared family
-- in sys binds every namespace. No locks; the apply validates the versions it carries.
SELECT
    s.schedule_id,
    s.namespace_id,
    n.namespace_name,
    s.job_id,
    s.definition_id,
    s.schedule_name,
    d.definition_name,
    d.scheduled_delivery_code AS scheduled_delivery,
    s.expression,
    s.time_zone_id,
    s.expression_kind_code AS expression_kind,
    s.misfire_strategy_code AS misfire_strategy,
    s.misfire_grace_seconds,
    s.interval_anchor_at_utc,
    s.timing_fingerprint,
    s.calendar_origin_code AS calendar_origin,
    s.calendar_version,
    s.availability_calendar_name,
    s.availability_calendar_origin_code AS availability_origin,
    s.availability_calendar_version AS availability_version,
    s.max_outstanding,
    s.full_behavior_code AS full_behavior,
    s.expire_after_seconds,
    s.expire_action_code AS expire_action,
    s.schedule_status_code AS status,
    s.paused_until_utc,
    s.version AS schedule_version,
    st.next_occurrence_at_utc,
    st.next_evaluation_at_utc,
    st.evaluation_status_code AS evaluation_status,
    st.outstanding_count,
    st.version AS state_version,
    r.job_status_code AS slot_status,
    r.version AS slot_version,
    cp.checkpoint_status_code AS firing_status
FROM {{schema}}.schedules s
INNER JOIN {{schema}}.schedules_state st ON st.schedule_id = s.schedule_id
INNER JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
INNER JOIN {{schema}}.namespaces n ON n.namespace_id = s.namespace_id
LEFT JOIN {{schema}}.jobs_state r ON r.job_id = s.job_id
LEFT JOIN {{schema}}.checkpoints cp
    ON cp.job_id = s.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */ AND cp.checkpoint_name = 'sys.firing'
WHERE
    (s.namespace_id = @p_namespace_id OR @p_namespace_id = 1 /* NameSyntax.SystemNamespaceId */)
    AND (
        (s.expression_kind_code = 30 /* ScheduleExpressionKindCode.Calendar */ AND s.expression = @p_calendar_name
            AND s.calendar_origin_code = 20 /* CalendarOriginCode.Catalog */)
        OR (s.availability_calendar_name = @p_calendar_name
            AND s.availability_calendar_origin_code = 20 /* CalendarOriginCode.Catalog */)
    )
ORDER BY s.schedule_id;
