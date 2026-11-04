/* Each skip names its job, so the event is sought through ix_events_entity_timeline. A newer success of the same job
   supersedes the skipped event: the incident it would open has closed. */
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
    EXISTS (
        SELECT 1
        FROM {{schema}}.events s
        WHERE
            s.entity_code = 50 /* EntityCode.Job */
            AND s.entity_id = e.entity_id
            AND s.event_code = 40 /* EventCode.JobExecutionSucceeded */
            AND (s.created_at_utc > e.created_at_utc OR (s.created_at_utc = e.created_at_utc AND s.event_id > e.event_id))
    ) AS superseded
FROM json_each(@p_alert_poison_events) p
INNER JOIN {{schema}}.events e
    ON
        e.entity_code = 50 /* EntityCode.Job */
        AND e.entity_id = json_extract(p.value, '$.job_id')
        AND e.event_id = json_extract(p.value, '$.event_id')
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = e.definition_id
WHERE
    e.namespace_id = @p_namespace_id
    AND e.event_code IN (
        40 /* EventCode.JobExecutionSucceeded */, 41 /* EventCode.JobExecutionFailed */,
        42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */
    )
ORDER BY e.created_at_utc, e.event_id;
