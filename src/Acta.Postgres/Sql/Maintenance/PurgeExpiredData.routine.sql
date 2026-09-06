-- One section, one bounded atomic batch. Section numbers match RetentionSection.
CREATE OR REPLACE FUNCTION {{schema}}.purge_expired_data(
    p_namespace_id INT,
    p_section INT,
    p_cutoff_utc TIMESTAMPTZ,
    p_batch_size INT
)
RETURNS TABLE (deleted_count INT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_rows INT := 0;
    v_ids BIGINT[];
    v_lock_keys TEXT[];
    v_alerts_slot_id BIGINT;
BEGIN
    IF p_batch_size <= 0 OR p_section NOT BETWEEN 1 AND 7 THEN
        RAISE EXCEPTION 'Invalid retention section or batch size.';
    END IF;

    CASE p_section
    WHEN 1 THEN
        SELECT array_agg(q.id) INTO v_ids FROM (
            SELECT j.id FROM {{schema}}.jobs j
            JOIN {{schema}}.runtimes r ON r.job_id = j.id
            WHERE
                r.namespace_id = p_namespace_id
                AND r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                AND r.retention_until_utc IS NOT NULL
                AND r.retention_until_utc <= p_cutoff_utc
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
            WHERE
                (t.scope_code = 50 /* TagScopeCode.Job */ AND t.scope_id = ANY(v_ids))
                OR (t.scope_code = 60 /* TagScopeCode.Schedule */ AND t.scope_id IN (
                    SELECT s.id FROM {{schema}}.schedules s WHERE s.job_id = ANY(v_ids)));
            DELETE FROM {{schema}}.jobs j WHERE j.id = ANY(v_ids);
        END IF;

    WHEN 2 THEN
        SELECT array_agg(q.id) INTO v_ids FROM (
            SELECT id FROM {{schema}}.events
            WHERE
                namespace_id = p_namespace_id
                AND created_at_utc <= p_cutoff_utc
            ORDER BY created_at_utc, id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.tags
            WHERE
                scope_code = 90 /* TagScopeCode.Event */
                AND scope_id = ANY(v_ids);
            DELETE FROM {{schema}}.events WHERE id = ANY(v_ids);
        END IF;

    WHEN 3 THEN
        SELECT array_agg(q.id) INTO v_ids FROM (
            SELECT id FROM {{schema}}.alerts
            WHERE
                namespace_id = p_namespace_id
                AND created_at_utc <= p_cutoff_utc
                AND delivery_status_code IN (30 /* AlertDeliveryStatusCode.Suppressed */, 100 /* AlertDeliveryStatusCode.Delivered */, 200 /* AlertDeliveryStatusCode.Failed */)
            ORDER BY created_at_utc, id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.tags
            WHERE
                scope_code = 80 /* TagScopeCode.Alert */
                AND scope_id = ANY(v_ids);
            DELETE FROM {{schema}}.alerts WHERE id = ANY(v_ids);
        END IF;

    WHEN 4 THEN
        SELECT array_agg(q.id) INTO v_ids FROM (
            SELECT id FROM {{schema}}.alerts
            WHERE
                namespace_id = p_namespace_id
                AND created_at_utc <= p_cutoff_utc
                AND delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.RetryAfter */)
            ORDER BY created_at_utc, id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.tags
            WHERE
                scope_code = 80 /* TagScopeCode.Alert */
                AND scope_id = ANY(v_ids);
            DELETE FROM {{schema}}.alerts WHERE id = ANY(v_ids);
        END IF;

    WHEN 5 THEN
        SELECT j.id INTO v_alerts_slot_id
        FROM {{schema}}.jobs j
        WHERE
            j.namespace_id = p_namespace_id
            AND j.deduplication_key = 'sys.alerts'
            AND j.parent_id IS NULL;
        DELETE FROM {{schema}}.checkpoints c
        WHERE (c.job_id, c.kind_code, c.name) IN (
            SELECT k.job_id, k.kind_code, k.name FROM {{schema}}.checkpoints k
            WHERE
                k.job_id = v_alerts_slot_id
                AND k.kind_code = 10 /* JobCheckpointKindCode.Variable */
                AND k.name LIKE 'alerts-skip-%'
                AND k.modified_at_utc <= p_cutoff_utc
            LIMIT p_batch_size);
        GET DIAGNOSTICS v_rows = ROW_COUNT;

    WHEN 6 THEN
        SELECT array_agg(q.id) INTO v_ids FROM (
            SELECT id FROM {{schema}}.workers
            WHERE
                namespace_id = p_namespace_id
                AND status_code IN (100 /* WorkerStatusCode.Stopped */, 200 /* WorkerStatusCode.Dead */)
                AND last_seen_at_utc <= p_cutoff_utc
            ORDER BY last_seen_at_utc, id
            LIMIT p_batch_size
            FOR UPDATE SKIP LOCKED) q;
        v_rows := COALESCE(cardinality(v_ids), 0);
        IF v_rows > 0 THEN
            DELETE FROM {{schema}}.tags
            WHERE
                scope_code = 70 /* TagScopeCode.Worker */
                AND scope_id = ANY(v_ids);
            DELETE FROM {{schema}}.workers WHERE id = ANY(v_ids);
        END IF;

    WHEN 7 THEN
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
    END CASE;

    RETURN QUERY SELECT v_rows;
END;
$$;

-- CREATE OR REPLACE across arities creates an overload instead of replacing; drop the retired
-- six-parameter signature (whole-sweep retention windows, before the per-section batch) so
-- pre-existing installs cannot resolve the stale unbounded form.
DROP FUNCTION IF EXISTS {{schema}}.purge_expired_data(INT, INT, INT, INT, INT, INT);
