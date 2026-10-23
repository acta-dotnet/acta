/* No revival: an Expired slot already resolved the wait TimedOut, so a late raise writes no slot and
   releases no job, but the raise still happened and the audit event still records it with a message
   saying why it changed nothing. The pre-state is captured first because the upsert overwrites it. */
DROP TABLE IF EXISTS temp._raise_expired;

CREATE TEMP TABLE _raise_expired AS
SELECT 1 AS expired
FROM {{schema}}.checkpoints c
WHERE
    c.job_id = @p_job_id
    AND c.checkpoint_kind_code = @p_checkpoint_kind_code
    AND c.checkpoint_name = @p_checkpoint_name
    AND c.checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */;

INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id, value, modified_at_utc, version
)
SELECT
    @p_job_id,
    @p_checkpoint_kind_code,
    @p_checkpoint_name,
    20 /* CheckpointStatusCode.Set */,
    @p_value_format_id,
    @p_value,
    {{now}},
    0
WHERE
    EXISTS (
        SELECT 1 FROM {{schema}}.jobs j
        JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
        WHERE
            j.job_id = @p_job_id
            AND r.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    )
    AND NOT EXISTS (SELECT 1 FROM temp._raise_expired)
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE
SET
    checkpoint_status_code = 20 /* CheckpointStatusCode.Set */,
    value_format_id = @p_value_format_id,
    value = @p_value,
    modified_at_utc = {{now}},
    version = {{schema}}.checkpoints.version + 1;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    definition_id,
    tenant_id,
    lineage_root_job_id,
    execution_number,
    event_message,
    details_format_id,
    details
)
SELECT
    80 /* EventCode.JobSignalRaised */,
    {{now}},
    j.namespace_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    CASE
        WHEN EXISTS (SELECT 1 FROM temp._raise_expired)
            THEN substr(COALESCE(@p_event_message || ' ', '') || 'Signal not applied: the wait had already expired.', 1, 512)
        ELSE @p_event_message
    END,
    @p_details_format_id,
    @p_details
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND r.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    definition_id,
    tenant_id,
    lineage_root_job_id,
    execution_number,
    event_message,
    details_format_id,
    details
)
SELECT
    72 /* EventCode.JobResumed */,
    {{now}},
    j.namespace_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND r.job_status_code = 20 /* JobStatusCode.Suspended */
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND NOT EXISTS (SELECT 1 FROM temp._raise_expired);

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = {{now}},
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND job_status_code = 20 /* JobStatusCode.Suspended */
    AND NOT EXISTS (SELECT 1 FROM temp._raise_expired);

SELECT
    CASE
        WHEN r.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN
            r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    r.job_status_code AS status
FROM (SELECT @p_job_id AS job_id) probe
LEFT JOIN {{schema}}.jobs_state r ON r.job_id = @p_job_id;
