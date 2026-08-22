CREATE OR REPLACE FUNCTION {{schema}}.reclaim_stuck_jobs(
    p_namespace_id INT
)
RETURNS TABLE (job_id BIGINT, to_status SMALLINT, parent_id BIGINT)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    WITH stuck AS (
        SELECT
            r.job_id AS id,
            r.status_code AS from_status,
            r.execution_number,
            j.lineage_root_id,
            j.definition_id,
            j.tenant_id,
            j.audit_level_code,
            (r.failure_count + 1) AS new_failure_count,
            jd.max_attempts_effective AS max_attempts,
            jd.retention_seconds_effective AS retention_seconds,
            /* The job was parked on THIS slot: the suspend copied the slot's due into next_run_at_utc,
               so the equality names the wait this attempt woke for, and an unbounded wait (NULL due)
               matches nothing. Expired means the timeout had already resolved durably. */
            EXISTS (
                SELECT 1
                FROM {{schema}}.checkpoints c
                WHERE
                    c.job_id = r.job_id
                    AND c.kind_code IN (20 /* JobCheckpointKindCode.Signal */, 50 /* JobCheckpointKindCode.ChildLatch */)
                    AND c.status_code = 30 /* JobCheckpointStatusCode.Expired */
                    AND c.due_at_utc = r.next_run_at_utc
            ) AS wait_resolved
        FROM {{schema}}.runtimes r
        INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
        INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
        WHERE
            r.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
            AND r.lease_expires_at_utc < now()
            AND r.namespace_id = p_namespace_id
        FOR UPDATE OF r SKIP LOCKED
    ),
    reclaimed AS (
        /* Budget-neutral for a resolved wait: the surviving path would have ended this attempt at no
           cost, so the job goes back to Suspended on the same past deadline, unclaimed and uncharged,
           and the replay lands whatever outcome the waiting overload chooses. */
        UPDATE {{schema}}.runtimes r
        SET
            status_code = CASE
                WHEN s.wait_resolved THEN 20 /* JobStatusCode.Suspended */
                WHEN s.new_failure_count >= s.max_attempts THEN 200 /* JobStatusCode.Failed */
                ELSE 10 /* JobStatusCode.Ready */ END,
            failure_count = CASE WHEN s.wait_resolved
                THEN r.failure_count
                ELSE s.new_failure_count END,
            next_run_at_utc = CASE
                WHEN s.wait_resolved THEN r.next_run_at_utc
                WHEN s.new_failure_count >= s.max_attempts THEN r.next_run_at_utc
                ELSE now() END,
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = CASE WHEN NOT s.wait_resolved AND s.new_failure_count >= s.max_attempts
                THEN now() + make_interval(secs => s.retention_seconds)
                ELSE r.retention_until_utc END,
            modified_at_utc = now(),
            version = r.version + 1
        FROM stuck s
        JOIN {{schema}}.jobs j ON j.id = s.id
        WHERE r.job_id = s.id
        RETURNING
            r.job_id AS id, j.namespace_id, r.execution_number, j.lineage_root_id,
            j.definition_id, j.tenant_id, s.from_status, r.status_code AS new_status, s.audit_level_code,
            j.parent_id AS job_parent_id, j.job_ref
    ),
    event_insert AS (
        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
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
            now(),
            r.namespace_id,
            10 /* ActorCode.Sys */,
            NULL,
            r.id,
            r.job_ref,
            r.execution_number,
            COALESCE(r.lineage_root_id, r.id),
            r.definition_id,
            r.tenant_id,
            NULL,
            r.from_status,
            r.new_status,
            230 /* ExecutionStatusCode.Orphaned */,
            NULL,
            21 /* JobEventReasonCode.JobLeaseExpired */,
            /* Suspended is reachable only through the resolved-wait arm above, so the landed status is
               what tells the two messages apart. */
            CASE WHEN r.new_status = 20 /* JobStatusCode.Suspended */
                THEN 'Worker lease expired while an expired wait was resolving; re-armed on the same deadline with no attempt charged.'
                ELSE 'Worker lease expired; reclaimed by the sys.recovery system job.' END
        FROM reclaimed r
        WHERE r.audit_level_code IN (10 /* JobAuditLevelCode.Failures */, 20 /* JobAuditLevelCode.Audit */)
    )
    SELECT r.id, r.new_status, r.job_parent_id
    FROM reclaimed r;
END;
$$;
