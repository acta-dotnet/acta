DROP TABLE IF EXISTS temp._reclaim_jobs;

CREATE TEMP TABLE _reclaim_jobs AS
SELECT
    r.job_id AS id,
    /* The job was parked on THIS slot: the suspend copied the slot's due into next_run_at_utc, so the
       equality names the wait this attempt woke for, and an unbounded wait (NULL due) matches nothing.
       Expired means the timeout had already resolved durably. */
    CASE WHEN EXISTS (
        SELECT 1
        FROM {{schema}}.checkpoints c
        WHERE
            c.job_id = r.job_id
            AND c.kind_code IN (20 /* JobCheckpointKindCode.Signal */, 50 /* JobCheckpointKindCode.ChildLatch */)
            AND c.status_code = 30 /* JobCheckpointStatusCode.Expired */
            AND c.due_at_utc = r.next_run_at_utc
    ) THEN 1 ELSE 0 END AS wait_resolved
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
    41 /* EventCode.JobExecutionFinished */,
    j.namespace_id,
    10 /* ActorCode.Sys */,
    NULL,
    j.id,
    j.job_ref,
    r.execution_number,
    COALESCE(j.lineage_root_id, j.id),
    j.definition_id,
    j.tenant_id,
    NULL,
    r.status_code,
    CASE
        WHEN rj.wait_resolved = 1 THEN 20 /* JobStatusCode.Suspended */
        WHEN (r.failure_count + 1) >= jd.max_attempts_effective THEN 200 /* JobStatusCode.Failed */
        ELSE 10 /* JobStatusCode.Ready */ END,
    230 /* ExecutionStatusCode.Orphaned */,
    NULL,
    21 /* JobEventReasonCode.JobLeaseExpired */,
    CASE WHEN rj.wait_resolved = 1
        THEN 'Worker lease expired while an expired wait was resolving; re-armed on the same deadline with no attempt charged.'
        ELSE 'Worker lease expired; reclaimed by the sys.recovery system job.' END
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
INNER JOIN temp._reclaim_jobs rj ON rj.id = j.id
WHERE
    j.audit_level_code IN (10 /* JobAuditLevelCode.Failures */, 20 /* JobAuditLevelCode.Audit */);

/* Budget-neutral for a resolved wait: the surviving path would have ended this attempt at no cost, so
   the job goes back to Suspended on the same past deadline, unclaimed and uncharged, and the replay
   lands whatever outcome the waiting overload chooses. */
UPDATE {{schema}}.runtimes
SET
    status_code = CASE
        WHEN rj.wait_resolved = 1 THEN 20 /* JobStatusCode.Suspended */
        WHEN (runtimes.failure_count + 1) >= jd.max_attempts_effective THEN 200 /* JobStatusCode.Failed */
        ELSE 10 /* JobStatusCode.Ready */ END,
    failure_count = CASE WHEN rj.wait_resolved = 1
        THEN runtimes.failure_count
        ELSE runtimes.failure_count + 1 END,
    next_run_at_utc = CASE
        WHEN rj.wait_resolved = 1 THEN runtimes.next_run_at_utc
        WHEN (runtimes.failure_count + 1) >= jd.max_attempts_effective THEN runtimes.next_run_at_utc
        ELSE {{now}} END,
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = CASE WHEN rj.wait_resolved = 0 AND (runtimes.failure_count + 1) >= jd.max_attempts_effective
        THEN {{now}} + (jd.retention_seconds_effective) * 1000
        ELSE runtimes.retention_until_utc END,
    modified_at_utc = {{now}},
    version = runtimes.version + 1
FROM {{schema}}.jobs j
JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
JOIN temp._reclaim_jobs rj ON rj.id = j.id
WHERE
    j.id = runtimes.job_id
RETURNING
    runtimes.job_id AS id,
    runtimes.status_code,
    (SELECT parent_id FROM {{schema}}.jobs WHERE id = runtimes.job_id) AS parent_id;
