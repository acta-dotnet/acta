-- Explain read: the header, the newest steps, and every checkpoint of one Job in one statement, so the diagnostic reads one
-- snapshot. row_kind tells them apart (1 the header, 2 a step, 3 a checkpoint); the columns of the other kinds are
-- null. No now(); the caller supplies the DB clock.
SELECT
    x.row_kind, x.job_id, x.job_ref, x.job_namespace, x.job_name, x.kind, x.status, x.failure_count,
    x.max_attempts_effective, x.next_run_at_utc, x.leased_by_worker_id, x.lease_expires_at_utc,
    x.worker_deployment_version, x.worker_status, x.worker_last_seen_at_utc, x.latest_reason_message, x.latest_reason_actor,
    x.latest_reason_details_format_id, x.latest_reason_details, x.last_executed_by_worker_version, x.leased_by_worker_ref,
    x.last_executed_by_worker_ref, x.definition_status, x.step_name, x.step_status, x.failure_message,
    x.checkpoint_kind, x.checkpoint_name, x.checkpoint_status, x.due_at_utc, x.child_job_ref, x.child_name, x.failed_executions,
    x.orphaned_executions
FROM (
    -- The header: job + jobs_state snapshot, the leasing worker's liveness (LEFT JOIN on the lease owner), the newest
    -- reason on the timeline (a failed, orphaned, or terminally failed attempt, or a Cancelled job's cancel), and the
    -- worker of the newest event that names one. Each walks the job's timeline index newest first to its first match, read by rowid.
    SELECT
        1 AS row_kind,
        NULL AS sort_at,
        NULL AS sort_kind,
        NULL AS sort_name,
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
        le.event_message AS latest_reason_message,
        le.actor AS latest_reason_actor,
        le.details_format_id AS latest_reason_details_format_id,
        le.details AS latest_reason_details,
        lxw.deployment_version AS last_executed_by_worker_version,
        w.worker_ref AS leased_by_worker_ref,
        lxw.worker_ref AS last_executed_by_worker_ref,
        jd.definition_status_code AS definition_status,
        NULL AS step_name,
        NULL AS step_status,
        NULL AS failure_message,
        NULL AS checkpoint_kind,
        NULL AS checkpoint_name,
        NULL AS checkpoint_status,
        NULL AS due_at_utc,
        NULL AS child_job_ref,
        NULL AS child_name,
        rc.failed_executions,
        rc.orphaned_executions
    FROM {{schema}}.jobs j
    INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
    INNER JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
    INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
    LEFT JOIN {{schema}}.workers w ON w.worker_id = r.leased_by_worker_id
    -- A restart ends what an earlier cancel explains: a cancel after it that recorded nothing has no reason.
    LEFT JOIN {{schema}}.events le
        ON
            le.event_code <> 20 /* EventCode.JobRestarted */
            AND le.event_id
            = (
                SELECT e.event_id FROM {{schema}}.events e
                WHERE
                    e.entity_code = 4 /* EntityCode.Job */
                    AND e.entity_id = j.job_id
                    AND (
                        (
                            r.job_status_code = 220 /* JobStatusCode.Cancelled */
                            AND e.event_code IN (8 /* EventCode.JobCancelled */, 20 /* EventCode.JobRestarted */)
                        )
                        OR (
                            r.job_status_code <> 220 /* JobStatusCode.Cancelled */
                            AND e.event_code IN (9 /* EventCode.JobExecutionFailed */, 10 /* EventCode.JobExecutionOrphaned */, 13 /* EventCode.JobFailed */)
                        )
                    )
                ORDER BY e.created_at_utc DESC, e.event_id DESC
                LIMIT 1
            )
    LEFT JOIN {{schema}}.events lx
        ON
            lx.event_id
            = (
                SELECT e.event_id FROM {{schema}}.events e
                WHERE e.entity_code = 4 /* EntityCode.Job */ AND e.entity_id = j.job_id AND e.worker_id <> 0
                ORDER BY e.created_at_utc DESC, e.event_id DESC
                LIMIT 1
            )
    LEFT JOIN {{schema}}.workers lxw ON lxw.worker_id = lx.worker_id
    -- The attempts this run lost since its last restart: failed ones and ones crash recovery reclaimed. Only a Succeeded
    -- headline reads them, so any other status skips both reads and answers null. A CROSS JOIN keeps the job's state row
    -- in SQLite's outer loop, so a status that fails the gate never reaches the events.
    LEFT JOIN (
        SELECT
            e.entity_id AS job_id,
            SUM(CASE WHEN e.event_code = 9 /* EventCode.JobExecutionFailed */ THEN 1 ELSE 0 END) AS failed_executions,
            SUM(CASE WHEN e.event_code = 10 /* EventCode.JobExecutionOrphaned */ THEN 1 ELSE 0 END) AS orphaned_executions
        FROM {{schema}}.jobs_state g
        CROSS JOIN {{schema}}.events e
        WHERE
            g.job_id = @p_job_id
            AND g.job_status_code = 100 /* JobStatusCode.Succeeded */
            AND e.entity_code = 4 /* EntityCode.Job */
            AND e.entity_id = @p_job_id
            AND e.event_code IN (9 /* EventCode.JobExecutionFailed */, 10 /* EventCode.JobExecutionOrphaned */)
            AND e.event_id > COALESCE((
                SELECT MAX(rs.event_id)
                FROM {{schema}}.events rs
                WHERE rs.entity_code = 4 /* EntityCode.Job */ AND rs.entity_id = @p_job_id AND rs.event_code = 20 /* EventCode.JobRestarted */
            ), 0)
        GROUP BY e.entity_id
    ) rc ON rc.job_id = j.job_id
    WHERE j.job_id = @p_job_id

    UNION ALL

    -- The Job's newest steps, at most the fetch limit (StepLimit + 1), in creation order.
    SELECT
        2, s.created_at_utc, NULL, s.step_name,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        s.step_name, s.step_status_code, s.failure_message,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
    FROM (
        SELECT step_name, step_status_code, failure_message, created_at_utc
        FROM {{schema}}.steps
        WHERE job_id = @p_job_id
        ORDER BY created_at_utc DESC, step_name DESC
        LIMIT @p_step_limit
    ) s

    UNION ALL

    -- Checkpoints of every kind, by kind then name; a child latch names its child, the direct child its name carries.
    SELECT
        3, NULL, c.checkpoint_kind_code, c.checkpoint_name,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
        NULL, NULL, NULL,
        c.checkpoint_kind_code, c.checkpoint_name, c.checkpoint_status_code, c.due_at_utc, ch.job_ref, ch.idempotency_key, NULL, NULL
    FROM {{schema}}.checkpoints c
    LEFT JOIN {{schema}}.jobs ch
        ON
            c.checkpoint_kind_code = 50 /* CheckpointKindCode.Child */
            AND ch.parent_job_id = c.job_id
            -- A seek on the child's id, the latch name's tail, so no sibling row is read.
            AND ch.job_id = CAST(substr(c.checkpoint_name, 11) AS INTEGER)
    WHERE c.job_id = @p_job_id
) x
ORDER BY x.row_kind, x.sort_at, x.sort_kind, x.sort_name;
