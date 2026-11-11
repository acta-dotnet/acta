-- Every member of each slot with a schedule due for evaluation, so a plan sees the whole slot: the slot's state and
-- envelope ride every member row. A due schedule with no slot is a group of its own, its slot columns NULL. A suspended
-- namespace has nothing due: its schedules hold until a resume. No locks; the apply validates the versions it carries.
WITH due AS (
    -- Every table is sought by key from the namespace's due index (docs/sql-internals.md, "Locking reads seek").
    SELECT TOP (@p_take) dst.schedule_id, ds.job_id
    FROM {{schema}}.schedules_state dst WITH (FORCESEEK (ix_schedules_state_due (namespace_id)))
    INNER JOIN {{schema}}.namespaces dn WITH (FORCESEEK) ON dn.namespace_id = dst.namespace_id
    INNER JOIN {{schema}}.schedules ds WITH (FORCESEEK) ON ds.schedule_id = dst.schedule_id
    INNER JOIN {{schema}}.definitions dd WITH (FORCESEEK) ON dd.definition_id = ds.definition_id
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
),
members AS (
    SELECT m.schedule_id
    FROM due
    INNER JOIN {{schema}}.schedules m WITH (FORCESEEK (ix_schedules_job (job_id))) ON m.job_id = due.job_id
    UNION
    SELECT due.schedule_id
    FROM due
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
    s.misfire_grace_seconds,
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
FROM members
INNER JOIN {{schema}}.schedules s WITH (FORCESEEK) ON s.schedule_id = members.schedule_id
INNER JOIN {{schema}}.schedules_state st WITH (FORCESEEK) ON st.schedule_id = s.schedule_id
INNER JOIN {{schema}}.definitions d WITH (FORCESEEK) ON d.definition_id = s.definition_id
INNER JOIN {{schema}}.namespaces n WITH (FORCESEEK) ON n.namespace_id = s.namespace_id
LEFT JOIN {{schema}}.jobs_state r WITH (FORCESEEK) ON r.job_id = s.job_id
LEFT JOIN {{schema}}.checkpoints cp WITH (FORCESEEK)
    ON cp.job_id = s.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */ AND cp.checkpoint_name = 'sys.firing'
WHERE
    s.schedule_status_code IN (10 /* ScheduleStatusCode.Active */, 30 /* ScheduleStatusCode.Paused */)
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
