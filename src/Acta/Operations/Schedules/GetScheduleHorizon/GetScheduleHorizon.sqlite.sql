-- The earliest wake in the namespace, in ix_schedules_state_due's order; NULL when none has one or it is suspended.
-- A retired definition's schedules are left out, and any ListDueSchedules leaves out for a code not listed here, so
-- their wakes never keep the admitter polling. The database clock comes back with it, so an idle admitter makes one read.
SELECT MIN(st.next_evaluation_at_utc) AS next_evaluation_at_utc, {{now}} AS db_now_utc
FROM {{schema}}.schedules_state st
INNER JOIN {{schema}}.namespaces n ON n.namespace_id = st.namespace_id
INNER JOIN {{schema}}.schedules s ON s.schedule_id = st.schedule_id
INNER JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
WHERE
    st.namespace_id = @p_namespace_id
    AND st.next_evaluation_at_utc IS NOT NULL
    AND n.namespace_status_code = 10 /* NamespaceStatusCode.Active */
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
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;
