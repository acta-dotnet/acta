/* One section, one bounded atomic batch; section numbers match RetentionSection. Alert-required events above the
   namespace floor stay with their jobs (PurgeExpiredDataCommand). */
CREATE OR REPLACE FUNCTION {{schema}}.purge_expired_data(
    p_namespace_id INT,
    p_section INT,
    p_cutoff_utc TIMESTAMPTZ,
    p_batch_size INT,
    p_before_job_id BIGINT DEFAULT 0,
    p_alert_floor_utc TIMESTAMPTZ DEFAULT NULL,
    p_alert_floor_event_id BIGINT DEFAULT NULL
)
RETURNS TABLE (deleted_count INT, next_job_id BIGINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows INT := 0;
    v_next BIGINT := 0;
    v_below BIGINT;
    v_slice_end BIGINT;
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
        /* No retention index: walk jobs_state downward from the cursor, ten batches a slice, and delete this
           namespace's expired jobs in it; downward, so a walk has a fixed end however fast enqueues arrive.
           A full batch ends the slice at its last delete; the oldest row answers zero, and the walk restarts. */
        v_below := CASE WHEN p_before_job_id = 0 THEN 9223372036854775807 ELSE p_before_job_id END;
        SELECT min(s.job_id) INTO v_slice_end FROM (
            SELECT r.job_id FROM {{schema}}.jobs_state r
            WHERE r.job_id < v_below
            ORDER BY r.job_id DESC
            LIMIT p_batch_size * 10) s;
        SELECT array_agg(q.job_id) INTO v_ids FROM (
            SELECT j.job_id FROM {{schema}}.jobs j
            JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
            WHERE
                r.job_id < v_below
                AND r.job_id >= v_slice_end
                AND r.namespace_id = p_namespace_id
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
                -- A stopped recurring job stays while its definition is live: the next start would register
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
            ORDER BY r.job_id DESC
            LIMIT p_batch_size
            FOR UPDATE OF j, r SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        v_next := CASE
            WHEN v_slice_end IS NULL THEN 0
            WHEN v_rows = p_batch_size THEN (SELECT min(x) FROM unnest(v_ids) x)
            ELSE v_slice_end
        END;
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.tags
            WHERE
                entity_code = 50 /* EntityCode.Job */
                AND entity_id = ANY(v_ids);
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

    RETURN QUERY SELECT v_rows, v_next;
END;
$$;