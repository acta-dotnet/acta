DROP TABLE IF EXISTS temp._repair_start;
DROP TABLE IF EXISTS temp._repair_window;
DROP TABLE IF EXISTS temp._repair_lanes;

/* A pass visits at most 1,000 of the namespace's lanes from a random lane id with wrap, seeks each head, and
   repairs at most 100 stranded ones in visiting order; a stranded lane's lowest-id unfinished member is
   Blocked. The immediate transaction is the lanes' mutex on SQLite. */
CREATE TEMP TABLE _repair_start AS
SELECT b.lo + ABS(RANDOM()) % (b.hi - b.lo + 1) AS start_id
FROM (
    -- SQLite seeks one end of the key only for a lone MIN or MAX; one aggregate holding both scans every lane.
    SELECT
        (SELECT MIN(id) FROM {{schema}}.lanes) AS lo,
        (SELECT MAX(id) FROM {{schema}}.lanes) AS hi
) b;

-- Each window seeks the rowid from the scalar start and reads forward; the unary + keeps the planner off ux_lanes_namespace_name.
CREATE TEMP TABLE _repair_window AS
SELECT l.id
FROM {{schema}}.lanes l
WHERE
    l.id >= (SELECT s.start_id FROM temp._repair_start s)
    AND +l.namespace_id = @p_namespace_id
ORDER BY l.id
LIMIT 1000;

INSERT INTO temp._repair_window (id)
SELECT l.id
FROM {{schema}}.lanes l
WHERE
    l.id < (SELECT s.start_id FROM temp._repair_start s)
    AND +l.namespace_id = @p_namespace_id
ORDER BY l.id
LIMIT (SELECT 1000 - COUNT(*) FROM temp._repair_window);

CREATE TEMP TABLE _repair_lanes AS
SELECT h.id
FROM (
    SELECT
        w.rowid AS ord,
        (
            SELECT m.job_id
            FROM {{schema}}.runtimes m
            WHERE
                m.lane_id = w.id
                AND m.lane_id IS NOT NULL
                AND m.status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1
        ) AS id
    FROM temp._repair_window w
) h
INNER JOIN {{schema}}.runtimes r ON r.job_id = h.id
WHERE
    r.status_code = 15 /* JobStatusCode.Blocked */
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.runtimes o
        WHERE
            o.lane_id = r.lane_id
            AND o.lane_id IS NOT NULL
            AND o.status_code IN (
                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                50 /* JobStatusCode.Executing */
            )
    )
ORDER BY h.ord
LIMIT 100;

UPDATE {{schema}}.runtimes
SET
    status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id IN (SELECT rl.id FROM temp._repair_lanes rl);

INSERT INTO {{schema}}.events (
    event_code,
    namespace_id,
    actor_code,
    actor,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    72 /* EventCode.JobResumed */,
    j.namespace_id,
    10 /* ActorCode.Sys */,
    NULL,
    j.id,
    j.job_ref,
    r.execution_number,
    COALESCE(j.lineage_root_id, j.id),
    j.definition_id,
    j.tenant_id,
    15 /* JobStatusCode.Blocked */,
    10 /* JobStatusCode.Ready */,
    NULL,
    NULL,
    67 /* EventReasonCode.JobLaneRepaired */,
    'Lane had no live head; crash recovery released its lowest Blocked member.'
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.runtimes r ON r.job_id = j.id
INNER JOIN temp._repair_lanes rl ON rl.id = j.id
WHERE
    j.audit_level_code IN (10 /* DefinitionAuditLevelCode.Failures */, 20 /* DefinitionAuditLevelCode.Audit */);

DROP TABLE IF EXISTS temp._reclaim_jobs;

CREATE TEMP TABLE _reclaim_jobs AS
SELECT
    s.id,
    s.parent_id,
    s.worker_id,
    s.wait_resolved,
    s.new_failure_count,
    /* Budget-neutral for a resolved wait: the surviving path would have ended this attempt at no cost, so
       the job goes back to Suspended on the same past deadline, unclaimed and uncharged, and the replay
       lands whatever outcome the waiting overload chooses. */
    CASE
        WHEN s.wait_resolved = 1 THEN 20 /* JobStatusCode.Suspended */
        WHEN s.is_recurring = 0 AND s.new_failure_count >= s.max_attempts THEN 200 /* JobStatusCode.Failed */
        ELSE 10 /* JobStatusCode.Ready */ END AS to_status
FROM (
    SELECT
        r.job_id AS id,
        j.parent_id,
        r.leased_by_worker_id AS worker_id,
        r.failure_count + 1 AS new_failure_count,
        jd.max_attempts_effective AS max_attempts,
        /* The job was parked on THIS slot: the suspend copied the slot's due into next_run_at_utc, so
           the equality names the wait this attempt woke for, and an unbounded wait (NULL due) matches
           nothing. Expired means the timeout had already resolved durably. */
        CASE WHEN EXISTS (
            SELECT 1
            FROM {{schema}}.checkpoints c
            WHERE
                c.job_id = r.job_id
                AND c.kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
                AND c.status_code = 30 /* CheckpointStatusCode.Expired */
                AND c.due_at_utc = r.next_run_at_utc
        ) THEN 1 ELSE 0 END AS wait_resolved,
        /* MaxAttempts is the one-off retry budget and a slot's failure_count accumulates across
           occurrences, so a recurring reclaim always re-arms Ready instead of terminally failing the
           schedule - mirroring ComputeRecurringOutcome on the worker path. */
        CASE WHEN EXISTS (
            SELECT 1
            FROM {{schema}}.schedules sc
            WHERE sc.job_id = r.job_id
        ) THEN 1 ELSE 0 END AS is_recurring
    FROM {{schema}}.runtimes r
    INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
    INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
    WHERE
        r.status_code = 50 /* JobStatusCode.Executing */
        -- ix_runtimes_worker_inflight's filter restated: a partial index serves only a query that states it.
        AND r.leased_by_worker_id IS NOT NULL
        AND r.lease_expires_at_utc < {{now}}
        AND r.namespace_id = @p_namespace_id
) s;

INSERT INTO {{schema}}.events (
    event_code,
    namespace_id,
    actor_code,
    actor,
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
    r.leased_by_worker_id,
    r.status_code,
    rj.to_status,
    230 /* ExecutionStatusCode.Orphaned */,
    NULL,
    21 /* EventReasonCode.JobLeaseExpired */,
    CASE WHEN rj.wait_resolved = 1
        THEN 'Worker lease expired while an expired wait was resolving; re-armed on the same deadline with no attempt charged.'
        ELSE 'Worker lease expired; reclaimed by crash recovery.' END
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
INNER JOIN temp._reclaim_jobs rj ON rj.id = j.id
WHERE
    j.audit_level_code IN (10 /* DefinitionAuditLevelCode.Failures */, 20 /* DefinitionAuditLevelCode.Audit */);

-- Each worker a reclaimed row names is stamped after its Orphaned event, so the purge keeps it while that event lasts.
UPDATE {{schema}}.workers
SET
    modified_at_utc = {{now}},
    version = version + 1
WHERE id IN (SELECT rj.worker_id FROM temp._reclaim_jobs rj);

UPDATE {{schema}}.runtimes
SET
    status_code = rj.to_status,
    failure_count = CASE WHEN rj.wait_resolved = 1
        THEN runtimes.failure_count
        ELSE rj.new_failure_count END,
    next_run_at_utc = CASE
        WHEN rj.to_status = 10 /* JobStatusCode.Ready */ THEN {{now}}
        ELSE runtimes.next_run_at_utc END,
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = CASE WHEN rj.to_status = 200 /* JobStatusCode.Failed */
        THEN {{now}} + (jd.retention_seconds_effective) * 1000
        ELSE runtimes.retention_until_utc END,
    modified_at_utc = {{now}},
    version = runtimes.version + 1
FROM {{schema}}.jobs j
JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
JOIN temp._reclaim_jobs rj ON rj.id = j.id
WHERE
    j.id = runtimes.job_id;

-- A head reclaimed to Failed hands its lane to the lowest-id unfinished member when that member is
-- Blocked and no member runs; a head re-armed Ready keeps its lane.
UPDATE {{schema}}.runtimes
SET
    status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    status_code = 15 /* JobStatusCode.Blocked */
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.runtimes o
        WHERE
            o.lane_id = runtimes.lane_id
            AND o.lane_id IS NOT NULL
            AND o.status_code IN (
                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                50 /* JobStatusCode.Executing */
            )
    )
    AND job_id IN (
        SELECT (
            SELECT m.job_id
            FROM {{schema}}.runtimes m
            WHERE
                m.lane_id = f.lane_id
                AND m.lane_id IS NOT NULL
                AND m.status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1
        )
        FROM {{schema}}.runtimes f
        JOIN temp._reclaim_jobs rj ON rj.id = f.job_id
        WHERE
            f.lane_id IS NOT NULL
            AND f.status_code = 200 /* JobStatusCode.Failed */
    );

SELECT rj.id AS job_id, rj.to_status, rj.parent_id, 0 AS lane_repaired
FROM temp._reclaim_jobs rj
UNION ALL
SELECT rl.id AS job_id, 10 /* JobStatusCode.Ready */ AS to_status, NULL AS parent_id, 1 AS lane_repaired
FROM temp._repair_lanes rl;
