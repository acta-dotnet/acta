-- Explain read: three result sets for one Job, read at one instant so the diagnostic reasons about a
-- consistent snapshot. Cross-dialect (no now(), no LIMIT/TOP); the caller supplies the DB clock.

-- 1) Header: job + runtime snapshot, the leasing worker's liveness (LEFT JOIN on the lease owner), and
--    the latest reason recorded on the timeline (the most recent reasoned event, joined by MAX(id)).
SELECT
    j.id,
    j.job_ref,
    ns.name AS namespace_name,
    jd.name AS job_name,
    r.status_code,
    r.execution_number,
    r.failure_count,
    jd.max_attempts_effective,
    r.next_run_at_utc,
    r.leased_by_worker_id,
    r.lease_expires_at_utc,
    w.deployment_version,
    w.status_code AS worker_status_code,
    w.last_seen_at_utc,
    le.reason_code,
    le.reason_message,
    lx.worker_id AS last_executed_worker_id,
    lxw.deployment_version AS last_executed_worker_name,
    w.worker_ref AS leased_by_worker_ref,
    lxw.worker_ref AS last_executed_worker_ref
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.runtimes r ON r.job_id = j.id
INNER JOIN {{schema}}.namespaces ns ON ns.id = j.namespace_id
INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
LEFT JOIN {{schema}}.workers w ON w.id = r.leased_by_worker_id
LEFT JOIN {{schema}}.events le
    ON le.id = (SELECT MAX(e.id) FROM {{schema}}.events e WHERE e.job_id = j.id AND e.reason_code IS NOT NULL)
LEFT JOIN {{schema}}.events lx
    ON lx.id = (SELECT MAX(e.id) FROM {{schema}}.events e WHERE e.job_id = j.id AND e.worker_id IS NOT NULL)
LEFT JOIN {{schema}}.workers lxw ON lxw.id = lx.worker_id
WHERE j.id = @p_id;

-- 2) Steps: every step slot for the Job, in creation order.
SELECT s.name, s.status_code, s.attempt_number, s.next_retry_at_utc, s.reason_message
FROM {{schema}}.steps s
WHERE s.job_id = @p_id
ORDER BY s.id;

-- 3) Checkpoints: signal / timer / variable / progress / child-latch slots, ordered by kind then name.
SELECT c.kind_code, c.name, c.status_code, c.due_at_utc
FROM {{schema}}.checkpoints c
WHERE c.job_id = @p_id
ORDER BY c.kind_code, c.name;
