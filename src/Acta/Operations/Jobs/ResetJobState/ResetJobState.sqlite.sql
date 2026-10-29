-- The reset clears nothing unless the attempt still holds the job's row (docs/sql-internals.md, "Execution fence").
SELECT ACTA_ERROR('ACTA:EXECUTION_FENCED:reset_job_state: the job is no longer executing this attempt.')
WHERE NOT EXISTS (
    SELECT 1
    FROM {{schema}}.jobs_state r
    WHERE
        r.job_id = @p_job_id
        AND r.job_status_code = 50 /* JobStatusCode.Executing */
        AND r.execution_number = @p_execution_number
        AND r.leased_by_worker_id = @p_leased_by_worker_id
);

-- The job's own handler asked for the reset, so the job id is the actor; digits need no JSON escaping.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    details_format_id, details
)
SELECT
    81 /* EventCode.JobStateReset */,
    {{now}},
    j.namespace_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    1 /* JobPayloadFormat.Json */,
    CAST('{"actor":"' || j.job_id || '"}' AS BLOB)
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */;

-- The scheduler's firing envelope is not the handler's state and stays.
DELETE FROM {{schema}}.checkpoints
WHERE job_id = @p_job_id AND checkpoint_kind_code <> 60 /* CheckpointKindCode.Firing */;
DELETE FROM {{schema}}.steps
WHERE job_id = @p_job_id;

SELECT 1 AS applied;
