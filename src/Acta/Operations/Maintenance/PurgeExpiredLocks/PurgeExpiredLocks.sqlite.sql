-- SQLite owns one immediate transaction for the file.
DROP TABLE IF EXISTS temp._purge_locks;

CREATE TEMP TABLE _purge_locks AS
SELECT lock_key
FROM {{schema}}.locks
WHERE expires_at_utc <= @p_cutoff_utc
ORDER BY expires_at_utc
LIMIT @p_batch_size;

DELETE FROM {{schema}}.locks WHERE lock_key IN (SELECT p.lock_key FROM temp._purge_locks p);

SELECT COUNT(*) AS deleted_count FROM temp._purge_locks;
