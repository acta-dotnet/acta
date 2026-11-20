-- Staged first, materialized, so the locking read runs once: a SKIP LOCKED read run again per row would lock a
-- different row each probe and delete past the batch size.
WITH lock_del AS MATERIALIZED (
    SELECT lock_key
    FROM {{schema}}.locks
    WHERE expires_at_utc <= @p_cutoff_utc
    ORDER BY expires_at_utc
    LIMIT @p_batch_size
    FOR UPDATE SKIP LOCKED
),
deleted AS (
    DELETE FROM {{schema}}.locks t
    USING lock_del d
    WHERE t.lock_key = d.lock_key
    RETURNING 1
)
SELECT count(*)::INT AS deleted_count FROM deleted;
