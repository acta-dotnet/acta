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
        (SELECT MIN(lane_id) FROM {{schema}}.lanes) AS lo,
        (SELECT MAX(lane_id) FROM {{schema}}.lanes) AS hi
) b;

-- Each window seeks the rowid from the scalar start and reads forward; the unary + keeps the planner off ux_lanes_namespace_name.
CREATE TEMP TABLE _repair_window AS
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE
    l.lane_id >= (SELECT s.start_id FROM temp._repair_start s)
    AND +l.namespace_id = @p_namespace_id
ORDER BY l.lane_id
LIMIT 1000;

INSERT INTO temp._repair_window (lane_id)
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE
    l.lane_id < (SELECT s.start_id FROM temp._repair_start s)
    AND +l.namespace_id = @p_namespace_id
ORDER BY l.lane_id
LIMIT (SELECT 1000 - COUNT(*) FROM temp._repair_window);

CREATE TEMP TABLE _repair_lanes AS
SELECT h.job_id
FROM (
    SELECT
        w.rowid AS ord,
        (
            SELECT m.job_id
            FROM {{schema}}.jobs_state m
            WHERE
                m.lane_id = w.lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1
        ) AS job_id
    FROM temp._repair_window w
) h
INNER JOIN {{schema}}.jobs_state r ON r.job_id = h.job_id
WHERE
    r.job_status_code = 15 /* JobStatusCode.Blocked */
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state o
        WHERE
            o.lane_id = r.lane_id
            AND o.lane_id IS NOT NULL
            AND o.job_status_code IN (
                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                50 /* JobStatusCode.Executing */
            )
    )
ORDER BY h.ord
LIMIT 100;

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id IN (SELECT rl.job_id FROM temp._repair_lanes rl);

INSERT INTO {{schema}}.events (
    event_code,
    namespace_id,
    entity_code,
    entity_id,
    definition_id,
    tenant_id,
    lineage_root_job_id,
    execution_number,
    event_message,
    details_format_id,
    details)
SELECT
    72 /* EventCode.JobResumed */,
    j.namespace_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    'Lane had no live head; crash recovery released its lowest Blocked member.',
    1 /* JobPayloadFormat.Json */,
    CAST('{"reason":"lane-repaired"}' AS BLOB)
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
INNER JOIN temp._repair_lanes rl ON rl.job_id = j.job_id
WHERE
    j.event_recording_code IN (10 /* EventRecordingCode.Failures */, 20 /* EventRecordingCode.Audit */);

DROP TABLE IF EXISTS temp._reclaim_jobs;

CREATE TEMP TABLE _reclaim_jobs AS
SELECT
    s.job_id,
    s.parent_job_id,
    s.worker_id,
    s.wait_resolved,
    s.new_failure_count,
    s.alert_profile_code,
    /* Budget-neutral for a resolved wait: the surviving path would have ended this attempt at no cost, so
       the job goes back to Suspended on the same past deadline, unclaimed and uncharged, and the replay
       lands whatever outcome the waiting overload chooses. */
    CASE
        WHEN s.wait_resolved = 1 THEN 20 /* JobStatusCode.Suspended */
        WHEN s.new_failure_count >= s.max_attempts THEN CASE WHEN s.is_slot = 1 THEN 40 /* JobStatusCode.Idle */ ELSE 200 /* JobStatusCode.Failed */ END
        ELSE 10 /* JobStatusCode.Ready */ END AS to_status
FROM (
    SELECT
        r.job_id,
        j.parent_job_id,
        r.leased_by_worker_id AS worker_id,
        -- Saturates at 32767, the ceiling WorkerFinalizer.FailureCountCeiling names; registration caps MaxAttempts there.
        CASE WHEN r.failure_count >= 32767 THEN 32767 ELSE r.failure_count + 1 END AS new_failure_count,
        jd.max_attempts_effective AS max_attempts,
        jd.alert_profile_code_effective AS alert_profile_code,
        /* The job was parked on THIS slot: the suspend copied the slot's due into next_run_at_utc, so
           the equality names the wait this attempt woke for, and an unbounded wait (NULL due) matches
           nothing. Expired means the timeout had already resolved durably. */
        CASE WHEN EXISTS (
            SELECT 1
            FROM {{schema}}.checkpoints c
            WHERE
                c.job_id = r.job_id
                AND c.checkpoint_kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
                AND c.checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */
                AND c.due_at_utc = r.next_run_at_utc
        ) THEN 1 ELSE 0 END AS wait_resolved,
        -- A slot's spent budget returns it to Idle with its firing settled Failed, never to Failed itself.
        CASE WHEN j.job_kind_code = 20 /* JobKindCode.PermanentSlot */ THEN 1 ELSE 0 END AS is_slot
    FROM {{schema}}.jobs_state r
    INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
    WHERE
        r.job_status_code = 50 /* JobStatusCode.Executing */
        -- ix_jobs_state_worker_inflight's filter restated: a partial index serves only a query that states it.
        AND r.leased_by_worker_id IS NOT NULL
        AND r.lease_expires_at_utc < {{now}}
        AND r.namespace_id = @p_namespace_id
) s;

/* An orphaned attempt that spent the budget is the job failing for good; any other lands Ready or back on its wait.
   Failures and Audit record it, and so does an enabled alert profile at any level, in this transaction, so the
   projector meets every lease-loss transition it alerts on. */
INSERT INTO {{schema}}.events (
    event_code,
    namespace_id,
    entity_code,
    entity_id,
    definition_id,
    tenant_id,
    lineage_root_job_id,
    worker_id,
    execution_number,
    event_message,
    details_format_id,
    details)
SELECT
    CASE WHEN rj.to_status = 200 /* JobStatusCode.Failed */ THEN 44 /* EventCode.JobFailed */ ELSE 42 /* EventCode.JobExecutionOrphaned */ END,
    j.namespace_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.leased_by_worker_id,
    r.execution_number,
    CASE WHEN rj.wait_resolved = 1
        THEN 'Worker lease expired while an expired wait was resolving; re-armed on the same deadline with no attempt charged.'
        ELSE 'Worker lease expired; reclaimed by crash recovery.' END,
    1 /* JobPayloadFormat.Json */,
    CAST(
        CASE rj.to_status
            WHEN 200 /* JobStatusCode.Failed */ THEN '{"reason":"lease-expired"}'
            WHEN 40 /* JobStatusCode.Idle */ THEN '{"reason":"lease-expired","to":"idle"}'
            WHEN 20 /* JobStatusCode.Suspended */ THEN '{"reason":"lease-expired","to":"suspended"}'
            ELSE '{"reason":"lease-expired","to":"ready"}'
        END AS BLOB
    )
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
INNER JOIN temp._reclaim_jobs rj ON rj.job_id = j.job_id
WHERE
    j.event_recording_code IN (10 /* EventRecordingCode.Failures */, 20 /* EventRecordingCode.Audit */)
    OR rj.alert_profile_code <> 0 /* AlertProfileCode.None */;

-- Each worker a reclaimed row names is stamped after its Orphaned event, so the purge keeps it while that event lasts.
UPDATE {{schema}}.workers
SET
    modified_at_utc = {{now}},
    version = version + 1
WHERE worker_id IN (SELECT rj.worker_id FROM temp._reclaim_jobs rj);

UPDATE {{schema}}.jobs_state
SET
    job_status_code = rj.to_status,
    failure_count = CASE WHEN rj.wait_resolved = 1
        THEN jobs_state.failure_count
        ELSE rj.new_failure_count END,
    next_run_at_utc = CASE
        WHEN rj.to_status = 10 /* JobStatusCode.Ready */ THEN {{now}}
        WHEN rj.to_status = 40 /* JobStatusCode.Idle */ THEN NULL
        ELSE jobs_state.next_run_at_utc END,
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = CASE WHEN rj.to_status = 200 /* JobStatusCode.Failed */
        THEN {{now}} + (jd.retention_seconds_effective) * 1000
        ELSE jobs_state.retention_until_utc END,
    modified_at_utc = {{now}},
    version = jobs_state.version + 1
FROM {{schema}}.jobs j
JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
JOIN temp._reclaim_jobs rj ON rj.job_id = j.job_id
WHERE
    j.job_id = jobs_state.job_id;

-- A lost attempt that spent the budget settles the firing Failed: a slot's, which wakes the members parked on the slot,
-- or a charged root's, which releases its schedule's capacity.
UPDATE {{schema}}.checkpoints
SET
    checkpoint_status_code = 120 /* CheckpointStatusCode.Failed */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
    AND checkpoint_name = 'sys.firing'
    AND checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
    AND job_id IN (
        SELECT rj.job_id
        FROM temp._reclaim_jobs rj
        JOIN {{schema}}.jobs j ON j.job_id = rj.job_id
        WHERE rj.to_status = 40 /* JobStatusCode.Idle */ OR (rj.to_status = 200 /* JobStatusCode.Failed */ AND j.schedule_id IS NOT NULL)
    );

UPDATE {{schema}}.schedules_state AS st
SET
    outstanding_count = st.outstanding_count - c.settled,
    next_evaluation_at_utc = CASE
        WHEN st.evaluation_status_code = 20 /* EvaluationStatusCode.WaitingForCapacity */ THEN {{now}}
        ELSE st.next_evaluation_at_utc
    END,
    evaluation_status_code = CASE
        WHEN st.evaluation_status_code = 20 /* EvaluationStatusCode.WaitingForCapacity */ THEN 10 /* EvaluationStatusCode.Scheduled */
        ELSE st.evaluation_status_code
    END,
    modified_at_utc = {{now}},
    version = st.version + 1
FROM (
    SELECT j.schedule_id, COUNT(*) AS settled
    FROM temp._reclaim_jobs rj
    JOIN {{schema}}.jobs j ON j.job_id = rj.job_id
    WHERE j.schedule_id IS NOT NULL AND rj.to_status = 200 /* JobStatusCode.Failed */
    GROUP BY j.schedule_id
) c
WHERE st.schedule_id = c.schedule_id;

UPDATE {{schema}}.schedules_state
SET
    next_evaluation_at_utc = {{now}},
    evaluation_status_code = 10 /* EvaluationStatusCode.Scheduled */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    evaluation_status_code IN (20 /* EvaluationStatusCode.WaitingForCapacity */, 50 /* EvaluationStatusCode.SlotUnavailable */)
    AND schedule_id IN (
        SELECT js.schedule_id
        FROM {{schema}}.schedules js
        JOIN temp._reclaim_jobs rj ON rj.job_id = js.job_id
        WHERE rj.to_status = 40 /* JobStatusCode.Idle */
    );

-- A lost attempt that spent the budget is a firing that failed for good: the definition's firing failure policy applies
-- as it does on a completion, each schedule paused once per sweep, the event attributed to the first.
DROP TABLE IF EXISTS temp._reclaim_policy_paused;

CREATE TEMP TABLE _reclaim_policy_paused AS
SELECT js.schedule_id, js.schedule_name, MIN(j.job_id) AS job_id, j.namespace_id, j.definition_id, j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id) AS lineage_root_job_id, r.execution_number, j.event_recording_code
FROM temp._reclaim_jobs rj
INNER JOIN {{schema}}.jobs j ON j.job_id = rj.job_id
INNER JOIN {{schema}}.jobs_state r ON r.job_id = rj.job_id
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
INNER JOIN {{schema}}.schedules js ON js.definition_id = jd.definition_id
WHERE
    (rj.to_status = 40 /* JobStatusCode.Idle */ OR (rj.to_status = 200 /* JobStatusCode.Failed */ AND j.schedule_id IS NOT NULL))
    AND jd.firing_failure_policy_code = 20 /* FiringFailurePolicyCode.PauseScheduling */
    AND js.schedule_status_code = 10 /* ScheduleStatusCode.Active */
GROUP BY js.schedule_id;

UPDATE {{schema}}.schedules
SET
    schedule_status_code = 30 /* ScheduleStatusCode.Paused */,
    paused_until_utc = NULL,
    pause_message = 'Paused by the firing failure policy: the last firing failed for good.',
    modified_at_utc = {{now}},
    version = version + 1
WHERE schedule_id IN (SELECT p.schedule_id FROM temp._reclaim_policy_paused p);

UPDATE {{schema}}.schedules_state
SET
    next_evaluation_at_utc = NULL,
    modified_at_utc = {{now}},
    version = version + 1
WHERE schedule_id IN (SELECT p.schedule_id FROM temp._reclaim_policy_paused p);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id, execution_number, event_message, details_format_id, details)
SELECT
    100 /* EventCode.SchedulePaused */, {{now}}, p.namespace_id, 50 /* EntityCode.Job */, p.job_id,
    p.definition_id, p.tenant_id, p.lineage_root_job_id, p.execution_number, 'Paused by the firing failure policy: the last firing failed for good.',
    1 /* JobPayloadFormat.Json */, CAST('{"schedule":"' || p.schedule_name || '","reason":"firing-failed"}' AS BLOB)
FROM temp._reclaim_policy_paused p
WHERE p.event_recording_code = 20 /* EventRecordingCode.Audit */;

-- A head reclaimed to Failed hands its lane to the lowest-id unfinished member when that member is
-- Blocked and no member runs; a head re-armed Ready keeps its lane.
UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_status_code = 15 /* JobStatusCode.Blocked */
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state o
        WHERE
            o.lane_id = jobs_state.lane_id
            AND o.lane_id IS NOT NULL
            AND o.job_status_code IN (
                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                50 /* JobStatusCode.Executing */
            )
    )
    AND job_id IN (
        SELECT (
            SELECT m.job_id
            FROM {{schema}}.jobs_state m
            WHERE
                m.lane_id = f.lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1
        )
        FROM {{schema}}.jobs_state f
        JOIN temp._reclaim_jobs rj ON rj.job_id = f.job_id
        WHERE
            f.lane_id IS NOT NULL
            AND f.job_status_code = 200 /* JobStatusCode.Failed */
    );

SELECT rj.job_id, rj.to_status, rj.parent_job_id, 0 AS lane_repaired
FROM temp._reclaim_jobs rj
UNION ALL
SELECT rl.job_id, 10 /* JobStatusCode.Ready */ AS to_status, NULL AS parent_job_id, 1 AS lane_repaired
FROM temp._repair_lanes rl;
