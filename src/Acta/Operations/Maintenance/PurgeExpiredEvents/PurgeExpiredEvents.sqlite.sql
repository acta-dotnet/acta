-- SQLite owns one immediate transaction for the file.
DROP TABLE IF EXISTS temp._purge_events;

-- A batch reads on from the position its pass gives, so no batch reads the index entries an earlier one deleted.
CREATE TEMP TABLE _purge_events AS
SELECT e.event_id, e.created_at_utc
FROM {{schema}}.events e
WHERE
    e.namespace_id = @p_namespace_id
    AND e.created_at_utc < @p_cutoff_utc
    AND e.created_at_utc >= @p_after_created_utc
    AND (e.created_at_utc > @p_after_created_utc OR e.event_id > @p_after_event_id)
ORDER BY e.created_at_utc, e.event_id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.events WHERE event_id IN (SELECT p.event_id FROM temp._purge_events p);

SELECT
    (SELECT COUNT(*) FROM temp._purge_events) AS deleted_count,
    COALESCE(l.created_at_utc, @p_after_created_utc) AS last_created_utc,
    COALESCE(l.event_id, @p_after_event_id) AS last_event_id
FROM (SELECT 1 AS one) o
LEFT JOIN (
    SELECT p.created_at_utc, p.event_id FROM temp._purge_events p ORDER BY p.created_at_utc DESC, p.event_id DESC LIMIT 1
) l ON 1 = 1;
