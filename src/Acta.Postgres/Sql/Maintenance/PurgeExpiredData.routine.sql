CREATE OR REPLACE FUNCTION {{schema}}.purge_expired_data(
    p_namespace_id         SMALLINT,
    p_events_retention_days    INT,
    p_alert_retention_days     INT,
    p_worker_retention_seconds INT,
    p_batch_size               INT,
    p_max_iterations           INT
)
RETURNS TABLE(jobs_deleted INT, events_deleted INT, alerts_deleted INT, workers_deleted INT, locks_deleted INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_now           TIMESTAMPTZ := now();
    v_jobs          INT := 0;
    v_events        INT := 0;
    v_alerts        INT := 0;
    v_workers       INT := 0;
    v_locks         INT := 0;
    v_rows          INT;
    v_iter          INT;
    v_events_cutoff TIMESTAMPTZ := now() - make_interval(days => p_events_retention_days);
    v_alerts_cutoff TIMESTAMPTZ := now() - make_interval(days => p_alert_retention_days);
    v_worker_cutoff TIMESTAMPTZ := now() - make_interval(secs => p_worker_retention_seconds);
    v_ids           BIGINT[];
    v_lease_keys    TEXT[];
BEGIN
    v_rows := 1;
    v_iter := 0;
    WHILE v_rows > 0 AND v_iter < p_max_iterations LOOP
        SELECT array_agg(q.id) INTO v_ids FROM (
            SELECT j.id FROM {{schema}}.jobs j
             JOIN {{schema}}.runtimes r ON r.job_id = j.id
             WHERE r.namespace_id   = p_namespace_id
               AND r.status_code        IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
               AND r.retention_until_utc IS NOT NULL
               AND r.retention_until_utc <= v_now
               -- Lineage guard: parent_id carries no FK, so purging a parent whose children still
               -- exist would orphan their lineage (same rule as the manual purge_job). Only leaves
               -- delete; a fully-expired subtree drains bottom-up across iterations.
               AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_id = j.id)
             ORDER BY r.retention_until_utc, r.job_id
             LIMIT p_batch_size
             FOR UPDATE OF j, r SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            PERFORM 1 FROM {{schema}}.schedules s WHERE s.job_id = ANY(v_ids) ORDER BY s.id FOR UPDATE;
            DELETE FROM {{schema}}.tags t
             WHERE (t.scope_code = 50 /* TagScopeCode.Job */ AND t.scope_id = ANY(v_ids))
                OR (t.scope_code = 60 /* TagScopeCode.Schedule */ AND t.scope_id IN (
                    SELECT s.id FROM {{schema}}.schedules s WHERE s.job_id = ANY(v_ids)));
            DELETE FROM {{schema}}.jobs j WHERE j.id = ANY(v_ids);
        END IF;
        v_jobs := v_jobs + v_rows;
        v_iter := v_iter + 1;
    END LOOP;

    v_rows := 1;
    v_iter := 0;
    WHILE v_rows > 0 AND v_iter < p_max_iterations LOOP
        SELECT array_agg(q.id) INTO v_ids FROM (
            SELECT id FROM {{schema}}.events
             WHERE namespace_id = p_namespace_id
               AND created_at_utc   <= v_events_cutoff
             ORDER BY created_at_utc, id
             LIMIT p_batch_size
             FOR UPDATE SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.tags WHERE scope_code = 90 /* TagScopeCode.Event */ AND scope_id = ANY(v_ids);
            DELETE FROM {{schema}}.events WHERE id = ANY(v_ids);
        END IF;
        v_events := v_events + v_rows;
        v_iter := v_iter + 1;
    END LOOP;

    v_rows := 1;
    v_iter := 0;
    WHILE v_rows > 0 AND v_iter < p_max_iterations LOOP
        SELECT array_agg(q.id) INTO v_ids FROM (
            SELECT id FROM {{schema}}.alerts
             WHERE namespace_id    = p_namespace_id
               AND created_at_utc       <= v_alerts_cutoff
               AND delivery_status_code IN (30 /* AlertDeliveryStatusCode.Suppressed */, 100 /* AlertDeliveryStatusCode.Delivered */, 200 /* AlertDeliveryStatusCode.Failed */)
             ORDER BY created_at_utc, id
             LIMIT p_batch_size
             FOR UPDATE SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.tags WHERE scope_code = 80 /* TagScopeCode.Alert */ AND scope_id = ANY(v_ids);
            DELETE FROM {{schema}}.alerts WHERE id = ANY(v_ids);
        END IF;
        v_alerts := v_alerts + v_rows;
        v_iter := v_iter + 1;
    END LOOP;

    v_rows := 1;
    v_iter := 0;
    WHILE v_rows > 0 AND v_iter < p_max_iterations LOOP
        SELECT array_agg(q.id) INTO v_ids FROM (
            SELECT id FROM {{schema}}.workers
             WHERE namespace_id = p_namespace_id
               AND status_code      = 200 /* WorkerStatusCode.Dead */
               AND last_seen_at_utc <= v_worker_cutoff
             ORDER BY last_seen_at_utc, id
             LIMIT p_batch_size
             FOR UPDATE SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.tags WHERE scope_code = 70 /* TagScopeCode.Worker */ AND scope_id = ANY(v_ids);
            DELETE FROM {{schema}}.workers WHERE id = ANY(v_ids);
        END IF;
        v_workers := v_workers + v_rows;
        v_iter := v_iter + 1;
    END LOOP;

    v_rows := 1;
    v_iter := 0;
    WHILE v_rows > 0 AND v_iter < p_max_iterations LOOP
        -- Stage the batch into a variable first (same shape as the sections above): a SKIP LOCKED
        -- subquery inlined into the DELETE may be re-evaluated per outer row, locking a different
        -- row each probe and deleting past the batch size.
        SELECT array_agg(q.lease_key) INTO v_lease_keys FROM (
            SELECT lease_key FROM {{schema}}.leases
             WHERE kind_code = 10 /* LeaseKindCode.Lock */
               AND expires_at_utc <= v_now
             ORDER BY expires_at_utc
             LIMIT p_batch_size
             FOR UPDATE SKIP LOCKED) q;
        DELETE FROM {{schema}}.leases WHERE lease_key = ANY(v_lease_keys);
        GET DIAGNOSTICS v_rows = ROW_COUNT;
        v_locks := v_locks + v_rows;
        v_iter := v_iter + 1;
    END LOOP;

    RETURN QUERY SELECT v_jobs, v_events, v_alerts, v_workers, v_locks;
END;
$$;
