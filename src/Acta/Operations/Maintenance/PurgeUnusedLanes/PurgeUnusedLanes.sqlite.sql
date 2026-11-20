-- SQLite owns one immediate transaction for the file, which is the lanes' mutex.
DROP TABLE IF EXISTS temp._purge_lanes;

CREATE TEMP TABLE _purge_lanes AS
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE
    l.namespace_id = @p_namespace_id
    AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs_state r WHERE r.lane_id = l.lane_id)
ORDER BY l.lane_id
LIMIT @p_batch_size;

DELETE FROM {{schema}}.lanes WHERE lane_id IN (SELECT p.lane_id FROM temp._purge_lanes p);

SELECT COUNT(*) AS deleted_count FROM temp._purge_lanes;
