CREATE OR REPLACE FUNCTION {{schema}}.reclaim_stuck_jobs(
    p_namespace_id INT
)
RETURNS TABLE (job_id BIGINT, to_status SMALLINT, parent_job_id BIGINT, lane_repaired SMALLINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_lanes BIGINT [] := '{}';
    v_stranded BIGINT [] := '{}';
    v_repaired BIGINT [] := '{}';
    v_window BIGINT [] := '{}';
    v_candidates BIGINT [] := '{}';
    v_min_lane BIGINT;
    v_max_lane BIGINT;
    v_start_lane BIGINT;
    v_lane_id BIGINT;
    v_head_id BIGINT;
    v_head_status SMALLINT;
BEGIN
    -- A pass visits at most 1,000 of the namespace's lanes from a random lane id with wrap, seeks each head
    -- through ix_jobs_state_lane, and repairs at most 100 stranded ones in visiting order; a stranded lane's
    -- lowest-id unfinished member is Blocked. Every lane is visited eventually.
    SELECT min(l.lane_id), max(l.lane_id) INTO v_min_lane, v_max_lane FROM {{schema}}.lanes l;
    IF v_max_lane IS NOT NULL THEN
        v_start_lane := v_min_lane + floor(random() * (v_max_lane - v_min_lane + 1))::BIGINT;
        v_window := ARRAY(
            SELECT l.lane_id
            FROM {{schema}}.lanes l
            WHERE l.namespace_id = p_namespace_id AND l.lane_id >= v_start_lane
            ORDER BY l.lane_id
            LIMIT 1000
        );
        v_window := v_window || ARRAY(
            SELECT l.lane_id
            FROM {{schema}}.lanes l
            WHERE l.namespace_id = p_namespace_id AND l.lane_id < v_start_lane
            ORDER BY l.lane_id
            LIMIT 1000 - cardinality(v_window)
        );
        v_stranded := ARRAY(
            SELECT w.lane_id
            FROM unnest(v_window) WITH ORDINALITY AS w (lane_id, ord)
            CROSS JOIN LATERAL (
                SELECT m.job_status_code
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
            ) h
            WHERE
                h.job_status_code = 15 /* JobStatusCode.Blocked */
                AND NOT EXISTS (
                    SELECT 1
                    FROM {{schema}}.jobs_state o
                    WHERE
                        o.lane_id = w.lane_id
                        AND o.lane_id IS NOT NULL
                        AND o.job_status_code IN (
                            10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                            50 /* JobStatusCode.Executing */
                        )
                )
            ORDER BY w.ord
            LIMIT 100
        );
    END IF;

    -- Lock order: the stuck rows' lanes and the stranded lanes in id order, then the jobs_state rows
    -- (docs/sql-internals.md, "Lane lock order"). A lane another transaction holds is
    -- skipped, and its stuck rows and repair wait for a later pass.
    v_candidates := v_stranded || ARRAY(
        SELECT r.lane_id
        FROM {{schema}}.jobs_state r
        WHERE
            r.job_status_code = 50 /* JobStatusCode.Executing */
            -- ix_jobs_state_worker_inflight's filter restated: a partial index serves only a query that states it.
            AND r.leased_by_worker_id IS NOT NULL
            AND r.lease_expires_at_utc < now()
            AND r.namespace_id = p_namespace_id
            AND r.lane_id IS NOT NULL
    );
    FOR v_lane_id IN
        SELECT l.lane_id
        FROM {{schema}}.lanes l
        WHERE l.lane_id = ANY (v_candidates)
        ORDER BY l.lane_id
        FOR UPDATE SKIP LOCKED
    LOOP
        v_lanes := v_lanes || v_lane_id;
    END LOOP;
    v_stranded := ARRAY(SELECT s.lane_id FROM unnest(v_stranded) AS s (lane_id) WHERE s.lane_id = ANY (v_lanes));

    -- Each worker a stuck row names is stamped at the instant of the Orphaned event below, so the purge keeps it while
    -- that event lasts. Workers lock in id order before any jobs_state row, the order the heartbeat takes them.
    UPDATE {{schema}}.workers w
    SET
        modified_at_utc = now(),
        version = w.version + 1
    FROM (
        SELECT w0.worker_id
        FROM {{schema}}.workers w0
        WHERE w0.worker_id IN (
            SELECT r.leased_by_worker_id
            FROM {{schema}}.jobs_state r
            WHERE
                r.job_status_code = 50 /* JobStatusCode.Executing */
                AND r.leased_by_worker_id IS NOT NULL
                AND r.lease_expires_at_utc < now()
                AND r.namespace_id = p_namespace_id
                AND (r.lane_id IS NULL OR r.lane_id = ANY (v_lanes))
        )
        ORDER BY w0.worker_id
        FOR UPDATE
    ) k
    WHERE w.worker_id = k.worker_id;

    -- The walk read without locks, so each stranded lane is re-read under its lock and repaired only
    -- when its lowest-id unfinished member is still Blocked and no member runs.
    FOREACH v_lane_id IN ARRAY v_stranded
    LOOP
        LOOP
            v_head_id := NULL;
            v_head_status := NULL;

            SELECT m.job_id, m.job_status_code INTO v_head_id, v_head_status
            FROM {{schema}}.jobs_state m
            WHERE
                m.lane_id = v_lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1;

            EXIT WHEN v_head_status IS DISTINCT FROM 15 /* JobStatusCode.Blocked */;
            EXIT WHEN EXISTS (
                SELECT 1
                FROM {{schema}}.jobs_state o
                WHERE
                    o.lane_id = v_lane_id
                    AND o.lane_id IS NOT NULL
                    AND o.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                        50 /* JobStatusCode.Executing */
                    )
            );

            UPDATE {{schema}}.jobs_state pr
            SET
                job_status_code = 10 /* JobStatusCode.Ready */,
                next_run_at_utc = GREATEST(pr.next_run_at_utc, now()),
                modified_at_utc = now(),
                version = pr.version + 1
            WHERE
                pr.job_id = v_head_id
                AND pr.job_status_code = 15 /* JobStatusCode.Blocked */;

            IF FOUND THEN
                v_repaired := v_repaired || v_head_id;
                EXIT;
            END IF;
        END LOOP;
    END LOOP;

    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
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
        now(),
        j.namespace_id,
        50 /* EntityCode.Job */,
        j.job_id,
        j.definition_id,
        j.tenant_id,
        COALESCE(j.lineage_root_job_id, j.job_id),
        r.execution_number,
        'Lane had no live head; crash recovery released its lowest Blocked member.',
        1 /* JobPayloadFormat.Json */,
        convert_to('{"reason":"lane-repaired"}', 'UTF8')
    FROM {{schema}}.jobs j
    INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
    WHERE
        j.job_id = ANY (v_repaired)
        AND j.event_recording_code IN (10 /* EventRecordingCode.Failures */, 20 /* EventRecordingCode.Audit */);

    RETURN QUERY
    WITH stuck AS (
        SELECT
            r.job_id,
            r.execution_number,
            j.lineage_root_job_id,
            j.definition_id,
            j.tenant_id,
            j.event_recording_code,
            r.leased_by_worker_id AS worker_id,
            -- Saturates at 32767, the ceiling WorkerFinalizer.FailureCountCeiling names; registration caps MaxAttempts there.
            CASE WHEN r.failure_count >= 32767 THEN 32767 ELSE r.failure_count + 1 END AS new_failure_count,
            jd.max_attempts_effective AS max_attempts,
            jd.retention_seconds_effective AS retention_seconds,
            jd.alert_profile_code_effective AS alert_profile_code,
            /* The job was parked on THIS slot: the suspend copied the slot's due into next_run_at_utc,
               so the equality names the wait this attempt woke for, and an unbounded wait (NULL due)
               matches nothing. Expired means the timeout had already resolved durably. */
            EXISTS (
                SELECT 1
                FROM {{schema}}.checkpoints c
                WHERE
                    c.job_id = r.job_id
                    AND c.checkpoint_kind_code IN (20 /* CheckpointKindCode.Signal */, 50 /* CheckpointKindCode.ChildLatch */)
                    AND c.checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */
                    AND c.due_at_utc = r.next_run_at_utc
            ) AS wait_resolved,
            /* MaxAttempts is the one-off retry budget and a slot's failure_count accumulates across
               occurrences, so a recurring reclaim always re-arms Ready instead of terminally failing
               the schedule - mirroring ComputeRecurringOutcome on the worker path. */
            EXISTS (
                SELECT 1
                FROM {{schema}}.schedules sc
                WHERE sc.job_id = r.job_id
            ) AS is_recurring
        FROM {{schema}}.jobs_state r
        INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
        INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
        WHERE
            r.job_status_code = 50 /* JobStatusCode.Executing */
            -- ix_jobs_state_worker_inflight's filter restated: a partial index serves only a query that states it.
            AND r.leased_by_worker_id IS NOT NULL
            AND r.lease_expires_at_utc < now()
            AND r.namespace_id = p_namespace_id
            AND (r.lane_id IS NULL OR r.lane_id = ANY (v_lanes))
        FOR UPDATE OF r SKIP LOCKED
    ),
    reclaimed AS (
        /* Budget-neutral for a resolved wait: the surviving path would have ended this attempt at no
           cost, so the job goes back to Suspended on the same past deadline, unclaimed and uncharged,
           and the replay lands whatever outcome the waiting overload chooses. */
        UPDATE {{schema}}.jobs_state r
        SET
            job_status_code = CASE
                WHEN s.wait_resolved THEN 20 /* JobStatusCode.Suspended */
                WHEN NOT s.is_recurring AND s.new_failure_count >= s.max_attempts THEN 200 /* JobStatusCode.Failed */
                ELSE 10 /* JobStatusCode.Ready */ END,
            failure_count = CASE WHEN s.wait_resolved
                THEN r.failure_count
                ELSE s.new_failure_count END,
            next_run_at_utc = CASE
                WHEN s.wait_resolved THEN r.next_run_at_utc
                WHEN NOT s.is_recurring AND s.new_failure_count >= s.max_attempts THEN r.next_run_at_utc
                ELSE now() END,
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = CASE WHEN NOT s.wait_resolved AND NOT s.is_recurring AND s.new_failure_count >= s.max_attempts
                THEN now() + make_interval(secs => s.retention_seconds)
                ELSE r.retention_until_utc END,
            modified_at_utc = now(),
            version = r.version + 1
        FROM stuck s
        JOIN {{schema}}.jobs j ON j.job_id = s.job_id
        WHERE r.job_id = s.job_id
        RETURNING
            r.job_id, j.namespace_id, r.execution_number, j.lineage_root_job_id,
            j.definition_id, j.tenant_id, s.worker_id, r.job_status_code AS new_status, s.event_recording_code,
            s.alert_profile_code, j.parent_job_id
    ),
    /* An orphaned attempt that spent the budget is the job failing for good; any other lands Ready or back on its
       wait. Failures and Audit record it, and so does an enabled alert profile at any level, in this transaction, so
       the projector meets every lease-loss transition it alerts on. */
    event_insert AS (
        INSERT INTO {{schema}}.events (
            event_code,
            created_at_utc,
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
            CASE WHEN r.new_status = 200 /* JobStatusCode.Failed */ THEN 44 /* EventCode.JobFailed */ ELSE 42 /* EventCode.JobExecutionOrphaned */ END,
            now(),
            r.namespace_id,
            50 /* EntityCode.Job */,
            r.job_id,
            r.definition_id,
            r.tenant_id,
            COALESCE(r.lineage_root_job_id, r.job_id),
            r.worker_id,
            r.execution_number,
            /* Suspended is reachable only through the resolved-wait arm above, so the landed status is
               what tells the two messages apart. */
            CASE WHEN r.new_status = 20 /* JobStatusCode.Suspended */
                THEN 'Worker lease expired while an expired wait was resolving; re-armed on the same deadline with no attempt charged.'
                ELSE 'Worker lease expired; reclaimed by crash recovery.' END,
            1 /* JobPayloadFormat.Json */,
            convert_to(
                CASE r.new_status
                    WHEN 200 /* JobStatusCode.Failed */ THEN '{"reason":"lease-expired"}'
                    WHEN 20 /* JobStatusCode.Suspended */ THEN '{"reason":"lease-expired","to":"suspended"}'
                    ELSE '{"reason":"lease-expired","to":"ready"}'
                END,
                'UTF8'
            )
        FROM reclaimed r
        WHERE
            r.event_recording_code IN (10 /* EventRecordingCode.Failures */, 20 /* EventRecordingCode.Audit */)
            OR r.alert_profile_code <> 0 /* AlertProfileCode.None */
    )
    SELECT r.job_id, r.new_status, r.parent_job_id, 0::SMALLINT
    FROM reclaimed r;

    -- A head reclaimed to Failed hands its lane on; a head re-armed Ready keeps it. Promotion runs under
    -- the lane lock and re-reads rather than trusting an update that matched nothing.
    FOREACH v_lane_id IN ARRAY v_lanes
    LOOP
        LOOP
            v_head_id := NULL;
            v_head_status := NULL;

            SELECT m.job_id, m.job_status_code INTO v_head_id, v_head_status
            FROM {{schema}}.jobs_state m
            WHERE
                m.lane_id = v_lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1;

            EXIT WHEN v_head_status IS DISTINCT FROM 15 /* JobStatusCode.Blocked */;
            EXIT WHEN EXISTS (
                SELECT 1
                FROM {{schema}}.jobs_state o
                WHERE
                    o.lane_id = v_lane_id
                    AND o.lane_id IS NOT NULL
                    AND o.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                        50 /* JobStatusCode.Executing */
                    )
            );

            UPDATE {{schema}}.jobs_state pr
            SET
                job_status_code = 10 /* JobStatusCode.Ready */,
                next_run_at_utc = GREATEST(pr.next_run_at_utc, now()),
                modified_at_utc = now(),
                version = pr.version + 1
            WHERE
                pr.job_id = v_head_id
                AND pr.job_status_code = 15 /* JobStatusCode.Blocked */;

            EXIT WHEN FOUND;
        END LOOP;
    END LOOP;

    RETURN QUERY
    SELECT x.job_id, 10 /* JobStatusCode.Ready */::SMALLINT, NULL::BIGINT, 1::SMALLINT
    FROM unnest(v_repaired) AS x (job_id);
END;
$$;
