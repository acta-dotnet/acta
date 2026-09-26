SELECT
    j.id,
    j.job_ref,
    j.lineage_root_id,
    lroot.job_ref AS lineage_root_job_ref,
    j.parent_id,
    pjob.job_ref AS parent_job_ref,
    j.deduplication_key,
    j.correlation_key,
    ns.name AS namespace_name,
    jd.name AS job_name,
    r.status_code,
    r.priority_code,
    r.execution_number,
    r.failure_count,
    j.input_format_id,
    r.next_run_at_utc,
    r.leased_by_worker_id,
    r.lease_expires_at_utc,
    j.concurrency_key,
    r.retention_until_utc,
    j.created_at_utc,
    r.modified_at_utc,
    j.tenant_id,
    t.tenant_key,
    j.definition_id,
    lw.worker_ref AS leased_by_worker_ref,
    r.version,
    lane.name AS lane,
    hjob.id AS blocked_behind_id,
    hjob.job_ref AS blocked_behind_job_ref
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.runtimes r ON r.job_id = j.id
INNER JOIN {{schema}}.namespaces ns ON ns.id = j.namespace_id
INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
LEFT JOIN {{schema}}.jobs pjob ON pjob.id = j.parent_id
LEFT JOIN {{schema}}.jobs lroot ON lroot.id = j.lineage_root_id
LEFT JOIN {{schema}}.tenants t ON t.id = j.tenant_id
LEFT JOIN {{schema}}.workers lw ON lw.id = r.leased_by_worker_id
LEFT JOIN {{schema}}.lanes lane ON lane.id = r.lane_id
-- A Blocked job waits behind its lane's lowest-id unfinished member, read through ix_runtimes_lane.
LEFT JOIN {{schema}}.jobs hjob ON hjob.id = (
    SELECT m.job_id
    FROM {{schema}}.runtimes m
    WHERE
        r.status_code = 15 /* JobStatusCode.Blocked */
        AND m.lane_id = r.lane_id
        AND m.lane_id IS NOT NULL
        AND m.status_code IN (
            10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
            30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
        )
        AND m.job_id < r.job_id
    ORDER BY m.job_id
    LIMIT 1
)
WHERE j.id = @p_id;
