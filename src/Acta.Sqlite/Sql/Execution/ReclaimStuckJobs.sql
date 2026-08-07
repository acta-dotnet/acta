DROP TABLE IF EXISTS temp._reclaim_jobs;

CREATE TEMP TABLE _reclaim_jobs AS
SELECT r.job_id AS id
FROM {{schema}}.runtimes r
WHERE
    r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
    AND r.lease_expires_at_utc < {{now}}
    AND r.namespace_id = @p_namespace_id;

INSERT INTO {{schema}}.events (
    event_code,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    41 /* JobEventCode.JobExecutionFinished */,
    j.namespace_id,
    10 /* JobActorCode.Sys */,
    NULL,
    j.id,
    j.job_ref,
    r.execution_number,
    COALESCE(j.lineage_root_id, j.id),
    j.definition_id,
    j.tenant_id,
    NULL,
    r.status_code,
    CASE WHEN (r.failure_count + 1) >= jd.max_attempts_effective THEN 200 /* JobStatusCode.Failed */ ELSE 10 /* JobStatusCode.Ready */ END,
    230 /* ExecutionStatusCode.Orphaned */,
    NULL,
    21 /* JobEventReasonCode.JobLeaseExpired */,
    'Worker lease expired; reclaimed by the sys.recovery system job.'
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
WHERE
    j.id IN (SELECT id FROM temp._reclaim_jobs)
    AND j.audit_level_code IN (10 /* JobAuditLevelCode.Failures */, 20 /* JobAuditLevelCode.Audit */);

UPDATE {{schema}}.runtimes
SET
    status_code = CASE WHEN (runtimes.failure_count + 1) >= jd.max_attempts_effective
        THEN 200 /* JobStatusCode.Failed */
        ELSE 10 /* JobStatusCode.Ready */ END,
    failure_count = runtimes.failure_count + 1,
    next_run_at_utc = CASE WHEN (runtimes.failure_count + 1) >= jd.max_attempts_effective
        THEN runtimes.next_run_at_utc
        ELSE {{now}} END,
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = CASE WHEN (runtimes.failure_count + 1) >= jd.max_attempts_effective
        THEN {{now}} + (jd.retention_seconds_effective) * 1000
        ELSE runtimes.retention_until_utc END,
    modified_at_utc = {{now}},
    version = runtimes.version + 1
FROM {{schema}}.jobs j
JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
WHERE
    j.id = runtimes.job_id
    AND runtimes.job_id IN (SELECT id FROM temp._reclaim_jobs)
RETURNING
    runtimes.job_id AS id,
    runtimes.status_code,
    (SELECT parent_id FROM {{schema}}.jobs WHERE id = runtimes.job_id) AS parent_id;
