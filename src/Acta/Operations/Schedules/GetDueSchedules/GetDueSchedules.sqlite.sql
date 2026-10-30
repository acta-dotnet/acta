-- Every member of each slot with a schedule due for evaluation, so a plan sees the whole slot: the slot's state and
-- envelope ride every member row. A due schedule with no slot is a group of its own, its slot columns NULL. No locks;
-- the apply validates the versions it carries.
WITH due AS (
    SELECT dst.schedule_id, ds.job_id
    FROM {{schema}}.schedules_state dst
    INNER JOIN {{schema}}.schedules ds ON ds.schedule_id = dst.schedule_id
    INNER JOIN {{schema}}.definitions dd ON dd.definition_id = ds.definition_id
    WHERE
        dst.namespace_id = @p_namespace_id
        AND dst.next_evaluation_at_utc IS NOT NULL
        AND dst.next_evaluation_at_utc <= @p_now_utc
        AND ds.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
        AND dd.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
    ORDER BY dst.next_evaluation_at_utc, dst.schedule_id
    LIMIT @p_take
)
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
    s.availability_calendar_name,
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
    (s.job_id IN (SELECT due.job_id FROM due) OR s.schedule_id IN (SELECT due.schedule_id FROM due))
    AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
ORDER BY s.job_id, s.schedule_name;
