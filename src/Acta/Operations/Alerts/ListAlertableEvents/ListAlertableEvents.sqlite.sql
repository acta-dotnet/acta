SELECT
    e.event_id,
    e.created_at_utc,
    e.entity_id AS job_id,
    e.definition_id,
    jd.definition_name AS job_name,
    jd.alert_profile_code_effective AS alert_profile,
    jd.alert_channel_name_effective AS alert_channel_name,
    e.event_code,
    e.event_message,
    e.details_format_id,
    e.details,
    0 AS superseded
FROM {{schema}}.events e
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = e.definition_id
WHERE
    e.namespace_id = @p_namespace_id
    /* Cursored by (created_at_utc, event_id), never by id alone: one writer at a time makes SQLite immune to the
       commit-order race the other two providers have, but the walk is written once for all three.
       created_at_utc, and both instant parameters the dialect binds against it, are epoch milliseconds. */
    AND e.created_at_utc <= @p_horizon_utc
    AND (
        e.created_at_utc > @p_cursor_utc
        OR (e.created_at_utc = @p_cursor_utc AND e.event_id > @p_cursor_event_id)
    )
    AND e.entity_code = 50 /* EntityCode.Job */
    AND e.event_code IN (
        40 /* EventCode.JobExecutionSucceeded */, 41 /* EventCode.JobExecutionFailed */,
        42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */
    )
    /* A success resolves only its own job's failures, and a first attempt follows none, so only a retry's
       success is read; at Audit every other success would cost the projector a resolve that closes nothing. */
    AND (e.event_code <> 40 /* EventCode.JobExecutionSucceeded */ OR e.execution_number > 1)
ORDER BY e.created_at_utc, e.event_id
LIMIT @p_alert_batch_size;
