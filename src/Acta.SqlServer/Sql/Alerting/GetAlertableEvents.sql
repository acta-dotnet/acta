SELECT TOP (@p_alert_batch_size)
    e.id,
    e.job_id,
    e.definition_id,
    jd.name,
    jd.alert_profile_code_effective AS alert_profile_code,
    jd.alert_channel_name_effective AS alert_channel_name,
    e.execution_status_code,
    e.to_status_code,
    e.reason_code,
    e.reason_message
FROM {{schema}}.events e
INNER JOIN {{schema}}.definitions jd ON jd.id = e.definition_id
WHERE
    e.namespace_id = @p_namespace_id
    AND e.id > @p_cursor_event_id
    AND e.job_id IS NOT NULL
    AND e.event_code = 41 /* JobEventCode.JobExecutionFinished */
    AND (
        e.to_status_code = 200 /* JobStatusCode.Failed */
        OR (
            e.to_status_code = 10 /* JobStatusCode.Ready */
            AND e.reason_code IN (
                20 /* JobEventReasonCode.JobUnhandledException */,
                21 /* JobEventReasonCode.JobLeaseExpired */,
                22 /* JobEventReasonCode.JobExecutionTimeout */
            )
        )
        OR e.execution_status_code = 100 /* ExecutionStatusCode.Succeeded */
    )
ORDER BY e.id;
