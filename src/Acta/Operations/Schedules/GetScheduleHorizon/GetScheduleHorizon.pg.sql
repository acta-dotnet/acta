-- The earliest wake in the namespace, one seek on ix_schedules_state_due; NULL when no schedule has one.
SELECT MIN(st.next_evaluation_at_utc) AS next_evaluation_at_utc
FROM {{schema}}.schedules_state st
WHERE st.namespace_id = @p_namespace_id AND st.next_evaluation_at_utc IS NOT NULL;
