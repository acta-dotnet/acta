SELECT
    j.job_id,
    j.job_ref,
    j.lineage_root_job_id,
    lroot.job_ref AS lineage_root_job_ref,
    j.parent_job_id,
    pjob.job_ref AS parent_job_ref,
    j.deduplication_key,
    j.correlation_key,
    ns.namespace_name AS job_namespace,
    jd.definition_name AS job_name,
    r.job_status_code AS status,
    r.job_priority_code AS priority,
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
    t.tenant_name,
    j.definition_id,
    lw.worker_ref AS leased_by_worker_ref,
    r.version,
    lane.lane_name AS lane,
    hjob.job_id AS blocked_behind_job_id,
    hjob.job_ref AS blocked_behind_job_ref
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
INNER JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
LEFT JOIN {{schema}}.jobs pjob ON pjob.job_id = j.parent_job_id
LEFT JOIN {{schema}}.jobs lroot ON lroot.job_id = j.lineage_root_job_id
LEFT JOIN {{schema}}.tenants t ON t.tenant_id = j.tenant_id
LEFT JOIN {{schema}}.workers lw ON lw.worker_id = r.leased_by_worker_id
LEFT JOIN {{schema}}.lanes lane ON lane.lane_id = r.lane_id
-- A Blocked job waits behind its lane's running member, else behind its lowest-id older unfinished
-- member, read through ix_jobs_state_lane.
LEFT JOIN {{schema}}.jobs hjob ON hjob.job_id = COALESCE(
    (
        SELECT m.job_id
        FROM {{schema}}.jobs_state m
        WHERE
            r.job_status_code = 15 /* JobStatusCode.Blocked */
            AND m.lane_id = r.lane_id
            AND m.lane_id IS NOT NULL
            AND m.job_status_code IN (
                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                50 /* JobStatusCode.Executing */
            )
        ORDER BY m.job_id
        LIMIT 1
    ),
    (
        SELECT m.job_id
        FROM {{schema}}.jobs_state m
        WHERE
            r.job_status_code = 15 /* JobStatusCode.Blocked */
            AND m.lane_id = r.lane_id
            AND m.lane_id IS NOT NULL
            AND m.job_status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
            )
            AND m.job_id < r.job_id
        ORDER BY m.job_id
        LIMIT 1
    )
)
WHERE j.job_id = @p_job_id;
