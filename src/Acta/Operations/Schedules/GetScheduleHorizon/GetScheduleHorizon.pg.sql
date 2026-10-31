-- The earliest wake in the namespace, walked in ix_schedules_state_due's order; NULL when no schedule has one or the
-- namespace is suspended. An orphaned schedule and a retired definition's are left out, as GetDueSchedules leaves them
-- out, so a wake one carries never keeps the admitter polling. The database clock comes back with it, so an idle
-- admitter makes one read.
SELECT MIN(st.next_evaluation_at_utc) AS next_evaluation_at_utc, (now() AT TIME ZONE 'UTC') AS db_now_utc
FROM {{schema}}.schedules_state st
INNER JOIN {{schema}}.namespaces n ON n.namespace_id = st.namespace_id
INNER JOIN {{schema}}.schedules s ON s.schedule_id = st.schedule_id
INNER JOIN {{schema}}.definitions d ON d.definition_id = s.definition_id
WHERE
    st.namespace_id = @p_namespace_id
    AND st.next_evaluation_at_utc IS NOT NULL
    AND n.namespace_status_code = 10 /* NamespaceStatusCode.Active */
    AND s.schedule_status_code <> 230 /* ScheduleStatusCode.Orphaned */
    AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;
