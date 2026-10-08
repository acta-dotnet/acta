-- One statement: the job's runtimes row is locked first (every delete reads the locked target), then its
-- checkpoints, steps, and results go, and an audited definition records the reset.
WITH target AS (
    SELECT
        r.job_id, r.status_code, r.execution_number, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id,
        j.audit_level_code, j.job_ref
    FROM {{schema}}.runtimes r
    JOIN {{schema}}.jobs j ON j.id = r.job_id
    WHERE r.job_id = @p_id
    FOR UPDATE OF r
),
cleared_checkpoints AS (
    DELETE FROM {{schema}}.checkpoints WHERE job_id IN (SELECT t.job_id FROM target t)
),
cleared_steps AS (
    DELETE FROM {{schema}}.steps WHERE job_id IN (SELECT t.job_id FROM target t)
),
cleared_results AS (
    DELETE FROM {{schema}}.results WHERE job_id IN (SELECT t.job_id FROM target t)
)
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, from_status_code, to_status_code)
SELECT
    81 /* EventCode.JobStateReset */, now(), t.namespace_id, 50 /* ActorCode.Job */, t.job_id::VARCHAR, t.job_id,
    t.job_ref, t.execution_number, COALESCE(t.lineage_root_id, t.job_id), t.definition_id, t.tenant_id,
    t.status_code, t.status_code
FROM target t
WHERE t.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */;
