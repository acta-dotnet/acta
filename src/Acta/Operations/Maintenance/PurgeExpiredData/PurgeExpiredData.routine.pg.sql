/* One section, one bounded atomic batch; section numbers match RetentionSection. Alert-required events above the
   namespace floor stay with their jobs (PurgeExpiredDataCommand). */
CREATE OR REPLACE FUNCTION {{schema}}.purge_expired_data(
    p_namespace_id INT,
    p_section INT,
    p_cutoff_utc TIMESTAMPTZ,
    p_batch_size INT,
    p_before_created_utc TIMESTAMPTZ DEFAULT NULL,
    p_before_job_id BIGINT DEFAULT 0,
    p_alert_floor_utc TIMESTAMPTZ DEFAULT NULL,
    p_alert_floor_event_id BIGINT DEFAULT NULL
)
RETURNS TABLE (deleted_count INT, next_created_utc TIMESTAMPTZ, next_job_id BIGINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows INT := 0;
    v_next TIMESTAMPTZ;
    v_next_id BIGINT := 0;
    v_below TIMESTAMPTZ := p_before_created_utc;
    v_below_id BIGINT := p_before_job_id;
    v_slice_end TIMESTAMPTZ;
    v_slice_end_id BIGINT;
    v_last TIMESTAMPTZ;
    v_last_id BIGINT;
    v_ids BIGINT[];
    v_worker_ids INT[];
    v_lock_keys TEXT[];
    v_floor_utc TIMESTAMPTZ := COALESCE(p_alert_floor_utc, p_cutoff_utc);
    v_floor_id BIGINT := COALESCE(p_alert_floor_event_id, 9223372036854775807);
BEGIN
    IF p_batch_size <= 0 OR p_section NOT BETWEEN 1 AND 7 THEN
        RAISE EXCEPTION 'Invalid retention section or batch size.';
    END IF;

    CASE p_section
    WHEN 1 THEN
        /* No retention index: walk this namespace's jobs from newer to older through ix_jobs_namespace_created, ten
           batches a slice, and delete the expired ones in it. A cycle ends at the namespace's oldest job, which answers
           no cursor; a full batch ends the slice at its last delete, so the walk moves on past what it deleted. */
        IF v_below IS NULL THEN
            -- A cycle starts at the newest job old enough to have expired: none expires sooner than the shortest
            -- retention any definition of the namespace has, a retired one included. The framework's own are left
            -- out: they run only permanent jobs, and their default would hold a longer-keeping namespace's walk to it.
            SELECT p_cutoff_utc - make_interval(secs => COALESCE(min(d.retention_seconds_effective), 0))
            INTO v_below
            FROM {{schema}}.definitions d
            WHERE d.namespace_id = p_namespace_id AND d.definition_name NOT LIKE 'sys.%';
            v_below_id := 9223372036854775807;
        END IF;

        SELECT s.created_at_utc, s.job_id INTO v_slice_end, v_slice_end_id FROM (
            SELECT j.created_at_utc, j.job_id FROM {{schema}}.jobs j
            WHERE j.namespace_id = p_namespace_id AND (j.created_at_utc, j.job_id) < (v_below, v_below_id)
            ORDER BY j.created_at_utc DESC, j.job_id DESC
            LIMIT p_batch_size * 10) s
        ORDER BY s.created_at_utc, s.job_id
        LIMIT 1;
        SELECT
            array_agg(q.job_id),
            min(q.created_at_utc),
            (array_agg(q.job_id ORDER BY q.created_at_utc, q.job_id))[1]
        INTO v_ids, v_last, v_last_id
        FROM (
            SELECT j.job_id, j.created_at_utc FROM {{schema}}.jobs j
            JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
            WHERE
                j.namespace_id = p_namespace_id
                AND (j.created_at_utc, j.job_id) < (v_below, v_below_id)
                AND (j.created_at_utc, j.job_id) >= (v_slice_end, v_slice_end_id)
                AND r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                AND r.retention_until_utc IS NOT NULL
                AND r.retention_until_utc <= p_cutoff_utc
                -- Lineage guard: parent_job_id carries no FK, so purging a parent whose children still
                -- exist would orphan their lineage (same rule as the manual purge_job). Only leaves
                -- delete; a fully-expired subtree drains bottom-up across iterations.
                AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_job_id = j.job_id)
                -- A completed child of a live parent is kept: the parent's replay dedupes onto its row and
                -- reads its result, so purging it would run the child again. The tree drains once the
                -- parent is terminal.
                AND NOT EXISTS (
                    SELECT 1 FROM {{schema}}.jobs_state p
                    WHERE p.job_id = j.parent_job_id
                      AND p.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */))
                -- A stopped recurring job stays while its definition is live: the next deploy would register
                -- its declaration as a new job and undo the cancel or failure. Retiring the definition lets
                -- it drain.
                AND NOT EXISTS (
                    SELECT 1 FROM {{schema}}.schedules s
                    JOIN {{schema}}.definitions d ON d.definition_id = j.definition_id
                    WHERE s.job_id = j.job_id AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */)
                -- A job stays while the projector still owes one of its events, since raising an alert reads the job row.
                AND NOT EXISTS (
                    SELECT 1
                    FROM {{schema}}.events e
                    JOIN {{schema}}.definitions d ON d.definition_id = e.definition_id
                    WHERE
                        e.entity_code = 50 /* EntityCode.Job */
                        AND e.entity_id = j.job_id
                        AND e.created_at_utc >= v_floor_utc
                        AND (e.created_at_utc > v_floor_utc OR e.event_id > v_floor_id)
                        AND (
                            e.event_code IN (41 /* EventCode.JobExecutionFailed */, 42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */)
                            OR (e.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND e.execution_number > 1)
                        )
                        AND d.alert_profile_code_effective <> 0 /* AlertProfileCode.None */)
                -- A job stays while an alert of it is undelivered: the failure it reports has reached nobody yet.
                AND NOT EXISTS (
                    SELECT 1 FROM {{schema}}.alerts a
                    WHERE a.job_id = j.job_id AND a.delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */))
            ORDER BY j.created_at_utc DESC, j.job_id DESC
            LIMIT p_batch_size
            FOR UPDATE OF j, r SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows = p_batch_size THEN
            v_next := v_last;
            v_next_id := v_last_id;
        ELSIF v_slice_end IS NOT NULL THEN
            v_next := v_slice_end;
            v_next_id := v_slice_end_id;
        END IF;
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.tags
            WHERE
                entity_code = 50 /* EntityCode.Job */
                AND entity_id = ANY(v_ids);
            -- The job's settled alerts go with it, so no alert outlives its job (docs/sql-internals.md, "Alert lock order").
            DELETE FROM {{schema}}.alerts a WHERE a.job_id = ANY(v_ids);
            DELETE FROM {{schema}}.checkpoints c WHERE c.job_id = ANY(v_ids);
            DELETE FROM {{schema}}.steps s WHERE s.job_id = ANY(v_ids);
            DELETE FROM {{schema}}.schedules_state st
            WHERE st.schedule_id IN (SELECT s.schedule_id FROM {{schema}}.schedules s WHERE s.job_id = ANY(v_ids));
            DELETE FROM {{schema}}.schedules s WHERE s.job_id = ANY(v_ids);
            DELETE FROM {{schema}}.jobs j WHERE j.job_id = ANY(v_ids);
            DELETE FROM {{schema}}.jobs_state r WHERE r.job_id = ANY(v_ids);
        END IF;

    WHEN 2 THEN
        -- Collected by the primary key, so each delete is a direct seek on pk_events; events still owed to the alert
        -- projector stay past the cutoff, see the floor above.
        SELECT array_agg(q.worker_id), array_agg(q.event_id) INTO v_worker_ids, v_ids FROM (
            SELECT e.worker_id, e.event_id FROM {{schema}}.events e
            WHERE
                e.namespace_id = p_namespace_id
                AND e.created_at_utc <= p_cutoff_utc
                AND NOT (
                    e.entity_code = 50 /* EntityCode.Job */
                    AND e.created_at_utc >= v_floor_utc
                    AND (e.created_at_utc > v_floor_utc OR e.event_id > v_floor_id)
                    AND (
                        e.event_code IN (41 /* EventCode.JobExecutionFailed */, 42 /* EventCode.JobExecutionOrphaned */, 44 /* EventCode.JobFailed */)
                        OR (e.event_code = 40 /* EventCode.JobExecutionSucceeded */ AND e.execution_number > 1)
                    )
                    AND EXISTS (
                        SELECT 1
                        FROM {{schema}}.definitions d
                        WHERE d.definition_id = e.definition_id AND d.alert_profile_code_effective <> 0 /* AlertProfileCode.None */))
            ORDER BY e.created_at_utc, e.event_id
            LIMIT p_batch_size
            FOR UPDATE OF e SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.events e
            USING unnest(v_worker_ids, v_ids) AS d (worker_id, event_id)
            WHERE e.worker_id = d.worker_id AND e.event_id = d.event_id;
        END IF;

    WHEN 3 THEN
        SELECT array_agg(q.alert_id) INTO v_ids FROM (
            SELECT alert_id FROM {{schema}}.alerts
            WHERE
                namespace_id = p_namespace_id
                AND created_at_utc <= p_cutoff_utc
                AND delivery_status_code IN (30 /* AlertDeliveryStatusCode.Suppressed */, 100 /* AlertDeliveryStatusCode.Delivered */, 200 /* AlertDeliveryStatusCode.Failed */)
            ORDER BY created_at_utc, alert_id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.alerts WHERE alert_id = ANY(v_ids);
        END IF;

    WHEN 4 THEN
        SELECT array_agg(q.alert_id) INTO v_ids FROM (
            SELECT alert_id FROM {{schema}}.alerts
            WHERE
                namespace_id = p_namespace_id
                AND created_at_utc <= p_cutoff_utc
                AND delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
            ORDER BY created_at_utc, alert_id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.alerts WHERE alert_id = ANY(v_ids);
        END IF;

    WHEN 5 THEN
        -- Events about a terminal worker land after its last heartbeat, and each one stamps modified_at_utc, so the
        -- row goes once both are past the window and no Executing job it leases is left for recovery to reclaim.
        SELECT array_agg(q.worker_id) INTO v_ids FROM (
            SELECT w.worker_id FROM {{schema}}.workers w
            WHERE
                w.namespace_id = p_namespace_id
                AND w.worker_status_code IN (100 /* WorkerStatusCode.Stopped */, 200 /* WorkerStatusCode.Dead */)
                AND w.last_seen_at_utc <= p_cutoff_utc
                AND w.modified_at_utc <= p_cutoff_utc
                AND NOT EXISTS (
                    SELECT 1
                    FROM {{schema}}.jobs_state r
                    WHERE
                        r.leased_by_worker_id = w.worker_id
                        -- ix_jobs_state_worker_inflight's filter restated: a partial index serves only a query that states it.
                        AND r.leased_by_worker_id IS NOT NULL
                        AND r.job_status_code = 50 /* JobStatusCode.Executing */
                )
            ORDER BY w.last_seen_at_utc, w.worker_id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.workers WHERE worker_id = ANY(v_ids);
        END IF;

    WHEN 6 THEN
        -- Stage the batch into a variable first (same shape as the sections above): a SKIP LOCKED
        -- subquery inlined into the DELETE may be re-evaluated per outer row, locking a different
        -- row each probe and deleting past the batch size.
        SELECT array_agg(q.lock_key) INTO v_lock_keys FROM (
            SELECT lock_key FROM {{schema}}.locks
            WHERE
                expires_at_utc <= p_cutoff_utc
            ORDER BY expires_at_utc
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED) q;
        DELETE FROM {{schema}}.locks WHERE lock_key = ANY(v_lock_keys);
        GET DIAGNOSTICS v_rows = ROW_COUNT;

    WHEN 7 THEN
        -- Unreferenced lanes, locked in id order skipping a lane an enqueue or settle holds, then
        -- re-checked under the lock (docs/sql-internals.md, "Lane lock order").
        SELECT array_agg(q.lane_id) INTO v_ids FROM (
            SELECT l.lane_id FROM {{schema}}.lanes l
            WHERE
                l.namespace_id = p_namespace_id
                AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs_state r WHERE r.lane_id = l.lane_id)
            ORDER BY l.lane_id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED) q;
        DELETE FROM {{schema}}.lanes l
        WHERE
            l.lane_id = ANY(v_ids)
            AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs_state r WHERE r.lane_id = l.lane_id);
        GET DIAGNOSTICS v_rows = ROW_COUNT;
    END CASE;

    RETURN QUERY SELECT v_rows, v_next, v_next_id;
END;
$$;