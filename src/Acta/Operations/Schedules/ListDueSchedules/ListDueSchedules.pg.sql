-- Every member of each slot with a schedule due for evaluation, so a plan sees the whole slot: the slot's state and
-- envelope ride every member row. A due schedule with no slot is a group of its own, its slot columns NULL. A suspended
-- namespace has nothing due: its schedules hold until a resume. No locks; the apply validates the versions it carries.
WITH due AS (
    SELECT dst.schedule_id, ds.job_id
    FROM {{schema}}.schedules_state dst
    INNER JOIN {{schema}}.namespaces dn ON dn.namespace_id = dst.namespace_id
    INNER JOIN {{schema}}.schedules ds ON ds.schedule_id = dst.schedule_id
    INNER JOIN {{schema}}.definitions dd ON dd.definition_id = ds.definition_id
    WHERE
        dst.namespace_id = @p_namespace_id
        AND dn.namespace_status_code = 10 /* NamespaceStatusCode.Active */
        AND dst.next_evaluation_at_utc IS NOT NULL
        AND dst.next_evaluation_at_utc <= @p_now_utc
        -- An orphaned schedule is left out, and so is one whose status, kind, strategy, full behavior, or expire action
        -- is not listed here: a newer build stored that code and evaluates the schedule, and this one leaves its row alone.
        AND ds.schedule_status_code IN (10 /* ScheduleStatusCode.Active */, 30 /* ScheduleStatusCode.Paused */)
        AND ds.expression_kind_code IN (
            10 /* ScheduleExpressionKindCode.Cron */,
            20 /* ScheduleExpressionKindCode.Interval */,
            30 /* ScheduleExpressionKindCode.Calendar */
        )
        AND ds.misfire_strategy_code IN (
            10 /* MisfireStrategyCode.CatchUpOnce */, 20 /* MisfireStrategyCode.Skip */,
            30 /* MisfireStrategyCode.CatchUpLatest */, 40 /* MisfireStrategyCode.CatchUpAll */
        )
        AND ds.full_behavior_code IN (10 /* ScheduleFullBehaviorCode.Wait */, 20 /* ScheduleFullBehaviorCode.Drop */)
        AND (
            ds.expire_action_code IS NULL
            OR ds.expire_action_code IN (10 /* ScheduleExpireActionCode.Discard */, 20 /* ScheduleExpireActionCode.Pause */)
        )
        AND dd.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
    ORDER BY dst.next_evaluation_at_utc, dst.schedule_id
    LIMIT @p_take
)
SELECT
    s.schedule_id,
    s.namespace_id,
    n.namespace_name,
    s.job_id,
    s.schedule_name,
    d.definition_name,
    s.expression,
    s.time_zone_id,
    s.expression_kind_code AS expression_kind,
    s.misfire_strategy_code AS misfire_strategy,
    s.interval_anchor_at_utc,
    s.timing_fingerprint,
    s.availability_calendar_name,
    s.start_at_utc,
    s.end_before_utc,
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
    AND s.schedule_status_code IN (10 /* ScheduleStatusCode.Active */, 30 /* ScheduleStatusCode.Paused */)
    AND s.expression_kind_code IN (
        10 /* ScheduleExpressionKindCode.Cron */,
        20 /* ScheduleExpressionKindCode.Interval */,
        30 /* ScheduleExpressionKindCode.Calendar */
    )
    AND s.misfire_strategy_code IN (
        10 /* MisfireStrategyCode.CatchUpOnce */, 20 /* MisfireStrategyCode.Skip */,
        30 /* MisfireStrategyCode.CatchUpLatest */, 40 /* MisfireStrategyCode.CatchUpAll */
    )
    AND s.full_behavior_code IN (10 /* ScheduleFullBehaviorCode.Wait */, 20 /* ScheduleFullBehaviorCode.Drop */)
    AND (
        s.expire_action_code IS NULL
        OR s.expire_action_code IN (10 /* ScheduleExpireActionCode.Discard */, 20 /* ScheduleExpireActionCode.Pause */)
    )
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
ORDER BY s.job_id, s.schedule_name;
