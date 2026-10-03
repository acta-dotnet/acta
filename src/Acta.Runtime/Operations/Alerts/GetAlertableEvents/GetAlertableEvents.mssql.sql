SELECT TOP (@p_alert_batch_size)
    e.id,
    e.created_at_utc,
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
    /* Cursored by (created_at_utc, id), never by id alone: the stamp is set inside the writing transaction
       and the id at insert, so a slow writer commits a low stamp under a high id and an id cursor would
       step over its neighbour for good. Every alertable row is visible once its stamp is behind the horizon. */
    AND e.created_at_utc <= @p_horizon_utc
    AND (
        e.created_at_utc > @p_cursor_utc
        OR (e.created_at_utc = @p_cursor_utc AND e.id > @p_cursor_event_id)
    )
    AND e.job_id IS NOT NULL
    AND e.event_code = 41 /* EventCode.JobExecutionFinished */
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
        /* Reclaim's uncharged arm lands Suspended rather than Ready, and charges nothing, so an
           operator hears about a worker dying on the same wait over and over here or nowhere. */
        OR (
            e.to_status_code = 20 /* JobStatusCode.Suspended */
            AND e.reason_code = 21 /* JobEventReasonCode.JobLeaseExpired */
        )
        OR e.execution_status_code = 100 /* ExecutionStatusCode.Succeeded */
    )
ORDER BY e.created_at_utc, e.id;
