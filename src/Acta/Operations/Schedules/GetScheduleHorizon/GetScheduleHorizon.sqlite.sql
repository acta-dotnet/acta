-- The earliest wake in the namespace, one seek on ix_schedules_state_due and one on the namespace row; NULL when no
-- schedule has one or the namespace is suspended.
SELECT MIN(st.next_evaluation_at_utc) AS next_evaluation_at_utc
FROM {{schema}}.schedules_state st
INNER JOIN {{schema}}.namespaces n ON n.namespace_id = st.namespace_id
WHERE
    st.namespace_id = @p_namespace_id
    AND st.next_evaluation_at_utc IS NOT NULL
    AND n.namespace_status_code = 10 /* NamespaceStatusCode.Active */;
