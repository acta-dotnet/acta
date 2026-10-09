CREATE OR REPLACE FUNCTION {{schema}}.extend_worker_leases(
    p_leased_by_worker_id INT,
    p_lease_ttl_seconds INT,
    p_draining BOOLEAN,
    p_job_ids BIGINT[],
    p_execution_numbers INT[]
)
RETURNS TABLE (job_id BIGINT, execution_number INT, renewed BOOLEAN, definitions_modified_at_utc TIMESTAMPTZ)
LANGUAGE plpgsql
AS $$
DECLARE
    v_new_expiry TIMESTAMPTZ := now() + (p_lease_ttl_seconds * INTERVAL '1 second');
    v_namespace_id INT;
BEGIN
    UPDATE {{schema}}.workers
    SET
        last_seen_at_utc = now(),
        -- A worker marked Dead through a database outage longer than its dead-after window is alive again the
        -- moment it heartbeats; Stopped stays Stopped.
        status_code = CASE
            WHEN p_draining AND status_code IN (10 /* WorkerStatusCode.Active */, 200 /* WorkerStatusCode.Dead */)
                THEN 80 /* WorkerStatusCode.Draining */
            WHEN status_code = 200 /* WorkerStatusCode.Dead */ THEN 10 /* WorkerStatusCode.Active */
            ELSE status_code
        END,
        modified_at_utc = now(),
        version = version + 1
    WHERE id = p_leased_by_worker_id
    RETURNING namespace_id INTO v_namespace_id;

    /* Push the held executions' leases forward. Only a row this worker still leases at the execution the
       process holds is answered, so a row the process lost lapses and recovery takes it. Deliberately no
       version bump: a lease refresh is not a claim-generation change. */
    -- A row another transaction holds is skipped and reported unrenewed, so the renewal never waits
    -- (docs/internals/sql-execution-policy.md, "Explicit exceptions and maintenance").
    RETURN QUERY
    WITH held AS (
        SELECT h.job_id, h.execution_number
        FROM unnest(p_job_ids, p_execution_numbers) AS h (job_id, execution_number)
    ),
    matched AS (
        SELECT r0.job_id, r0.execution_number
        FROM {{schema}}.runtimes r0
        WHERE
            (r0.job_id, r0.execution_number) IN (SELECT h.job_id, h.execution_number FROM held h)
            AND r0.leased_by_worker_id = p_leased_by_worker_id
            AND r0.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
    ),
    locked AS (
        SELECT r1.job_id
        FROM {{schema}}.runtimes r1
        WHERE
            (r1.job_id, r1.execution_number) IN (SELECT h.job_id, h.execution_number FROM held h)
            AND r1.leased_by_worker_id = p_leased_by_worker_id
            AND r1.status_code IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
        ORDER BY r1.job_id
        FOR UPDATE SKIP LOCKED
    ),
    extended AS (
        UPDATE {{schema}}.runtimes r
        SET lease_expires_at_utc = v_new_expiry
        WHERE r.job_id IN (SELECT l.job_id FROM locked l)
        RETURNING r.job_id
    )
    SELECT m.job_id, m.execution_number, e.job_id IS NOT NULL, NULL::TIMESTAMPTZ
    FROM matched m
    LEFT JOIN extended e ON e.job_id = m.job_id;

    -- The namespace's newest definition change: the worker re-reads its catalog only when it moves.
    RETURN QUERY
    SELECT NULL::BIGINT, NULL::INT, FALSE, MAX(d.modified_at_utc)
    FROM {{schema}}.definitions d
    WHERE d.namespace_id = v_namespace_id;
END;
$$;
