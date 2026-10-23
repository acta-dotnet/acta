-- Explain read: three result sets for one Job, read at one instant so the diagnostic reasons about a
-- consistent snapshot. No now(); the caller supplies the DB clock.

-- 1) Header: job + jobs_state snapshot, the leasing worker's liveness (LEFT JOIN on the lease owner), the
--    newest failure on the timeline (a failed, orphaned, or terminally failed attempt), and the worker of the
--    newest event that names one. Each walks the job's timeline index newest first and stops at its first match.
SELECT
    j.job_id,
    j.job_ref,
    ns.namespace_name AS job_namespace,
    jd.definition_name AS job_name,
    r.job_status_code AS status,
    r.failure_count,
    jd.max_attempts_effective,
    r.next_run_at_utc,
    r.leased_by_worker_id,
    r.lease_expires_at_utc,
    w.deployment_version,
    w.worker_status_code,
    w.last_seen_at_utc,
    le.event_message,
    le.details_format_id,
    le.details,
    lxw.deployment_version AS last_executed_worker_name,
    w.worker_ref AS leased_by_worker_ref,
    lxw.worker_ref AS last_executed_worker_ref
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
INNER JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
LEFT JOIN {{schema}}.workers w ON w.worker_id = r.leased_by_worker_id
LEFT JOIN LATERAL (
    SELECT e.event_message, e.details_format_id, e.details
    FROM {{schema}}.events e
    WHERE
        e.entity_code = 50 /* EntityCode.Job */
        AND e.entity_id = j.job_id
        AND e.event_code IN (41 /* EventCode.JobExecutionFailed */, 42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */)
    ORDER BY e.created_at_utc DESC, e.event_id DESC
    LIMIT 1
) le ON true
LEFT JOIN LATERAL (
    SELECT e.worker_id
    FROM {{schema}}.events e
    WHERE e.entity_code = 50 /* EntityCode.Job */ AND e.entity_id = j.job_id AND e.worker_id <> 0
    ORDER BY e.created_at_utc DESC, e.event_id DESC
    LIMIT 1
) lx ON true
LEFT JOIN {{schema}}.workers lxw ON lxw.worker_id = lx.worker_id
WHERE j.job_id = @p_job_id;

-- 2) Steps: every step slot for the Job, in creation order.
SELECT s.step_name, s.step_status_code AS status, s.attempt_number, s.next_retry_at_utc, s.failure_message
FROM {{schema}}.steps s
WHERE s.job_id = @p_job_id
ORDER BY s.created_at_utc, s.step_name;

-- 3) Checkpoints: signal / timer / variable / progress / child-latch slots, ordered by kind then name.
SELECT c.checkpoint_kind_code AS kind, c.checkpoint_name, c.checkpoint_status_code AS status, c.due_at_utc
FROM {{schema}}.checkpoints c
WHERE c.job_id = @p_job_id
ORDER BY c.checkpoint_kind_code, c.checkpoint_name;
