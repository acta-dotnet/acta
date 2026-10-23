CREATE OR REPLACE FUNCTION {{schema}}.extend_worker_leases(
    p_leased_by_worker_id INT,
    p_lease_ttl_seconds INT,
    p_draining BOOLEAN,
    p_job_ids BIGINT[],
    p_execution_numbers INT[],
    p_lock_keys VARCHAR[],
    p_hold_tokens UUID[],
    p_recovery_interval_seconds INT
)
RETURNS TABLE (
    job_id BIGINT,
    execution_number INT,
    lock_key VARCHAR,
    renewed BOOLEAN,
    definitions_modified_at_utc TIMESTAMPTZ,
    recovery_claimed BOOLEAN
)
LANGUAGE plpgsql
AS $$
DECLARE
    v_new_expiry TIMESTAMPTZ := now() + (p_lease_ttl_seconds * INTERVAL '1 second');
    v_namespace_id INT;
    v_recovery_claimed BOOLEAN := FALSE;
BEGIN
    UPDATE {{schema}}.workers
    SET
        last_seen_at_utc = now(),
        -- A worker marked Dead through a database outage longer than its dead-after window is alive again the
        -- moment it heartbeats; Stopped stays Stopped.
        worker_status_code = CASE
            WHEN p_draining AND worker_status_code IN (10 /* WorkerStatusCode.Active */, 200 /* WorkerStatusCode.Dead */)
                THEN 80 /* WorkerStatusCode.Draining */
            WHEN worker_status_code = 200 /* WorkerStatusCode.Dead */ THEN 10 /* WorkerStatusCode.Active */
            ELSE worker_status_code
        END,
        modified_at_utc = now(),
        version = version + 1
    WHERE worker_id = p_leased_by_worker_id
    RETURNING namespace_id INTO v_namespace_id;

    -- Lock holds renew first and never skip: no routine locks both locks and jobs_state. A hold whose token
    -- moved is not answered, and the attempt that relied on it stops.
    RETURN QUERY
    UPDATE {{schema}}.locks k
    SET expires_at_utc = v_new_expiry
    FROM unnest(p_lock_keys, p_hold_tokens) AS h (lock_key, hold_token)
    WHERE k.lock_key = h.lock_key AND k.hold_token = h.hold_token
    RETURNING NULL::BIGINT, NULL::INT, k.lock_key, TRUE, NULL::TIMESTAMPTZ, FALSE;

    /* Push the held executions' leases forward. Only a row this worker still leases at the execution the
       process holds is answered, so a row the process lost lapses and recovery takes it. Deliberately no
       version bump: a lease refresh is not a claim-generation change. */
    -- A row another transaction holds is skipped and reported unrenewed, so the renewal never waits
    -- (docs/sql-internals.md, "Explicit exceptions and maintenance").
    RETURN QUERY
    WITH held AS (
        SELECT h.job_id, h.execution_number
        FROM unnest(p_job_ids, p_execution_numbers) AS h (job_id, execution_number)
    ),
    -- Both reads find the held rows by pk_jobs_state probes: "=" in the worker test would imply ix_jobs_state_worker_inflight's
    -- filter and let a generic plan walk the worker's range of it (docs/sql-internals.md, "Locking reads seek").
    matched AS (
        SELECT r0.job_id, r0.execution_number
        FROM {{schema}}.jobs_state r0
        WHERE
            (r0.job_id, r0.execution_number) IN (SELECT h.job_id, h.execution_number FROM held h)
            AND r0.leased_by_worker_id IS NOT DISTINCT FROM p_leased_by_worker_id
            AND r0.job_status_code = 50 /* JobStatusCode.Executing */
    ),
    locked AS (
        SELECT r1.job_id
        FROM {{schema}}.jobs_state r1
        WHERE
            (r1.job_id, r1.execution_number) IN (SELECT h.job_id, h.execution_number FROM held h)
            AND r1.leased_by_worker_id IS NOT DISTINCT FROM p_leased_by_worker_id
            AND r1.job_status_code = 50 /* JobStatusCode.Executing */
        ORDER BY r1.job_id
        FOR UPDATE SKIP LOCKED
    ),
    extended AS (
        UPDATE {{schema}}.jobs_state r
        SET lease_expires_at_utc = v_new_expiry
        WHERE r.job_id IN (SELECT l.job_id FROM locked l)
        RETURNING r.job_id
    )
    SELECT m.job_id, m.execution_number, NULL::VARCHAR, e.job_id IS NOT NULL, NULL::TIMESTAMPTZ, FALSE
    FROM matched m
    LEFT JOIN extended e ON e.job_id = m.job_id;

    -- Takes the namespace's recovery pass when it is due, moving the next one an interval out; this worker runs it.
    -- A namespace row another transaction holds is skipped, so the heartbeat never waits on it.
    IF p_recovery_interval_seconds IS NOT NULL THEN
        UPDATE {{schema}}.namespaces n
        SET recovery_due_at_utc = now() + (p_recovery_interval_seconds * INTERVAL '1 second')
        WHERE n.namespace_id = (
            SELECT n0.namespace_id
            FROM {{schema}}.namespaces n0
            WHERE n0.namespace_id = v_namespace_id AND (n0.recovery_due_at_utc IS NULL OR n0.recovery_due_at_utc <= now())
            FOR UPDATE SKIP LOCKED
        );
        v_recovery_claimed := FOUND;
    END IF;

    -- The namespace's newest definition change: the worker re-reads its catalog only when it moves.
    RETURN QUERY
    SELECT NULL::BIGINT, NULL::INT, NULL::VARCHAR, FALSE, MAX(d.modified_at_utc), v_recovery_claimed
    FROM {{schema}}.definitions d
    WHERE d.namespace_id = v_namespace_id;
END;
$$;
