-- Appends one application-authored job.note-recorded event, written by ctx.NoteAsync, only while the attempt holds the
-- job's row, share-locked to commit (docs/sql-internals.md, "Execution fence").

WITH owner AS (
    SELECT r.job_id
    FROM {{schema}}.jobs_state r
    WHERE
        r.job_id = @p_job_id
        AND r.job_status_code = 50 /* JobStatusCode.Executing */
        AND r.execution_number = @p_execution_number
        AND r.leased_by_worker_id IS NOT DISTINCT FROM @p_worker_id
    FOR SHARE
),
inserted AS (
INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    worker_id,
    entity_code,
    entity_id,
    definition_id,
    tenant_id,
    lineage_root_job_id,
    execution_number,
    event_message,
    details_format_id,
    details)
SELECT
    90 /* EventCode.JobNoteRecorded */,
    now(),
    j.namespace_id,
    @p_worker_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    @p_execution_number,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM {{schema}}.jobs j
INNER JOIN owner o ON o.job_id = j.job_id
RETURNING 1
)
SELECT count(*)::INT AS inserted FROM inserted;
