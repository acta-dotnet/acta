-- One batch, one implicit transaction; only the final SELECT returns rows. Unreferenced lanes, locked in id order
-- skipping a lane an enqueue or settle holds, then re-checked under the lock by a statement of its own, which reads
-- what committed before the lock (docs/sql-internals.md, "Lane lock order" and "A read after a lock").
DROP TABLE IF EXISTS pg_temp._purge_lanes;
CREATE TEMP TABLE _purge_lanes (lane_id BIGINT NOT NULL) ON COMMIT DROP;

INSERT INTO _purge_lanes (lane_id)
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE
    l.namespace_id = @p_namespace_id
    AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs_state r WHERE r.lane_id = l.lane_id)
ORDER BY l.lane_id
LIMIT @p_batch_size
FOR UPDATE SKIP LOCKED;

WITH deleted AS (
    DELETE FROM {{schema}}.lanes l
    WHERE
        l.lane_id = ANY (ARRAY(SELECT p.lane_id FROM _purge_lanes p))
        AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs_state r WHERE r.lane_id = l.lane_id)
    RETURNING 1
)
SELECT count(*)::INT AS deleted_count FROM deleted;
