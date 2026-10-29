-- One statement: the jobs_state row is locked first, only while the attempt holds it (docs/sql-internals.md, "Execution
-- fence"), then every delete reads that locked target, an audited definition records the reset, and the locked count
-- answers. The job's own handler asked for the reset, so the job id is the actor; digits need no JSON escaping.
WITH target AS (
    SELECT
        r.job_id, r.execution_number, j.namespace_id, j.lineage_root_job_id, j.definition_id, j.tenant_id,
        j.event_recording_code
    FROM {{schema}}.jobs_state r
    JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    WHERE
        r.job_id = @p_job_id
        AND r.job_status_code = 50 /* JobStatusCode.Executing */
        AND r.execution_number = @p_execution_number
        AND r.leased_by_worker_id IS NOT DISTINCT FROM @p_leased_by_worker_id
    FOR UPDATE OF r
),
cleared_checkpoints AS (
    -- The scheduler's firing envelope is not the handler's state and stays.
    DELETE FROM {{schema}}.checkpoints
    WHERE job_id IN (SELECT t.job_id FROM target t) AND checkpoint_kind_code <> 60 /* CheckpointKindCode.Firing */
),
cleared_steps AS (
    DELETE FROM {{schema}}.steps WHERE job_id IN (SELECT t.job_id FROM target t)
),
recorded AS (
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        definition_id, tenant_id, lineage_root_job_id,
        execution_number,
        details_format_id, details
    )
    SELECT
        81 /* EventCode.JobStateReset */, now(), t.namespace_id,
        50 /* EntityCode.Job */, t.job_id,
        t.definition_id, t.tenant_id, COALESCE(t.lineage_root_job_id, t.job_id),
        t.execution_number,
        1 /* JobPayloadFormat.Json */, convert_to('{"actor":"' || t.job_id::text || '"}', 'UTF8')
    FROM target t
    WHERE t.event_recording_code = 20 /* EventRecordingCode.Audit */
)
SELECT count(*)::INT AS applied FROM target;
