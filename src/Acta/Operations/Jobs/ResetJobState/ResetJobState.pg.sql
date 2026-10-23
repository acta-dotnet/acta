-- One statement: the job's jobs_state row is locked first (every delete reads the locked target), then its
-- checkpoints and steps go, and an audited definition records the reset. The job's own handler asked for the
-- reset, so the job id is the actor; digits need no JSON escaping.
WITH target AS (
    SELECT
        r.job_id, r.execution_number, j.namespace_id, j.lineage_root_job_id, j.definition_id, j.tenant_id,
        j.event_recording_code
    FROM {{schema}}.jobs_state r
    JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    WHERE r.job_id = @p_job_id
    FOR UPDATE OF r
),
cleared_checkpoints AS (
    DELETE FROM {{schema}}.checkpoints WHERE job_id IN (SELECT t.job_id FROM target t)
),
cleared_steps AS (
    DELETE FROM {{schema}}.steps WHERE job_id IN (SELECT t.job_id FROM target t)
)
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
WHERE t.event_recording_code = 20 /* EventRecordingCode.Audit */;
