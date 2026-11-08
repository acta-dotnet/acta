-- Explain read: the header, the newest steps, and every checkpoint of one Job in one statement, so the diagnostic reads one
-- snapshot. row_kind tells them apart (1 the header, 2 a step, 3 a checkpoint); the columns of the other kinds are
-- null, typed in the header branch, which the later branches follow. No now(); the caller supplies the DB clock.
SELECT
    x.row_kind, x.job_id, x.job_ref, x.job_namespace, x.job_name, x.kind, x.status, x.failure_count,
    x.max_attempts_effective, x.next_run_at_utc, x.leased_by_worker_id, x.lease_expires_at_utc,
    x.worker_deployment_version, x.worker_status, x.worker_last_seen_at_utc, x.latest_failure_message,
    x.latest_failure_details_format_id, x.latest_failure_details, x.last_executed_by_worker_version, x.leased_by_worker_ref,
    x.last_executed_by_worker_ref, x.definition_status, x.step_name, x.step_status, x.failure_message,
    x.checkpoint_kind, x.checkpoint_name, x.checkpoint_status, x.due_at_utc
FROM (
    -- The header: job + jobs_state snapshot, the leasing worker's liveness (LEFT JOIN on the lease owner), the
    -- newest failure on the timeline (a failed, orphaned, or terminally failed attempt), and the worker of the
    -- newest event that names one. Each walks the job's timeline index newest first and stops at its first match.
    SELECT
        1 AS row_kind,
        NULL::timestamptz AS sort_at,
        NULL::smallint AS sort_kind,
        NULL::varchar(128) AS sort_name,
        j.job_id,
        j.job_ref,
        ns.namespace_name AS job_namespace,
        jd.definition_name AS job_name,
        j.job_kind_code AS kind,
        r.job_status_code AS status,
        r.failure_count,
        jd.max_attempts_effective,
        r.next_run_at_utc,
        r.leased_by_worker_id,
        r.lease_expires_at_utc,
        w.deployment_version AS worker_deployment_version,
        w.worker_status_code AS worker_status,
        w.last_seen_at_utc AS worker_last_seen_at_utc,
        le.event_message AS latest_failure_message,
        le.details_format_id AS latest_failure_details_format_id,
        le.details AS latest_failure_details,
        lxw.deployment_version AS last_executed_by_worker_version,
        w.worker_ref AS leased_by_worker_ref,
        lxw.worker_ref AS last_executed_by_worker_ref,
        jd.definition_status_code AS definition_status,
        NULL::varchar(128) AS step_name,
        NULL::smallint AS step_status,
        NULL::varchar(512) AS failure_message,
        NULL::smallint AS checkpoint_kind,
        NULL::varchar(128) AS checkpoint_name,
        NULL::smallint AS checkpoint_status,
        NULL::timestamptz AS due_at_utc
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
    WHERE j.job_id = @p_job_id

    UNION ALL

    -- The Job's newest steps, at most the fetch limit (StepLimit + 1), in creation order.
    SELECT
        2, s.created_at_utc, NULL, s.step_name,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        s.step_name, s.step_status_code, s.failure_message,
        NULL, NULL, NULL, NULL
    FROM (
        SELECT step_name, step_status_code, failure_message, created_at_utc
        FROM {{schema}}.steps
        WHERE job_id = @p_job_id
        ORDER BY created_at_utc DESC, step_name DESC
        LIMIT @p_step_limit
    ) s

    UNION ALL

    -- Checkpoints of every kind, by kind then name.
    SELECT
        3, NULL, c.checkpoint_kind_code, c.checkpoint_name,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL,
        c.checkpoint_kind_code, c.checkpoint_name, c.checkpoint_status_code, c.due_at_utc
    FROM {{schema}}.checkpoints c
    WHERE c.job_id = @p_job_id
) x
ORDER BY x.row_kind, x.sort_at, x.sort_kind, x.sort_name;
