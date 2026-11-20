-- SQLite owns one immediate transaction for the file.
DROP TABLE IF EXISTS temp._purge_cursor;
DROP TABLE IF EXISTS temp._purge_slice;
DROP TABLE IF EXISTS temp._purge_jobs;

-- No retention index: walk this namespace's jobs from newer to older through ix_jobs_namespace_created, ten batches a
-- slice, and delete the expired ones in it. A cycle ends at the namespace's oldest job, which answers no cursor; a
-- full batch ends the slice at its last delete, so the walk moves on past what it deleted.
CREATE TEMP TABLE _purge_cursor AS
-- A cycle starts at the newest job old enough to have expired: none expires sooner than the shortest retention any
-- definition of the namespace has, a retired one included. The framework's own are left out: they run only permanent
-- jobs, and their default would hold a longer-keeping namespace's walk to it.
SELECT
    COALESCE(
        @p_before_created_utc,
        @p_cutoff_utc - 1000 * (
            SELECT COALESCE(MIN(d.retention_seconds_effective), 0)
            FROM {{schema}}.definitions d
            WHERE d.namespace_id = @p_namespace_id AND d.definition_name NOT LIKE 'sys.%'
        )
    ) AS below,
    CASE WHEN @p_before_created_utc IS NULL THEN 9223372036854775807 ELSE @p_before_job_id END AS below_id;

CREATE TEMP TABLE _purge_slice AS
SELECT s.created_at_utc AS slice_end, s.job_id AS slice_end_id
FROM (
    SELECT j.created_at_utc, j.job_id
    FROM {{schema}}.jobs j, temp._purge_cursor c
    WHERE
        j.namespace_id = @p_namespace_id
        AND j.created_at_utc <= c.below
        AND (j.created_at_utc < c.below OR j.job_id < c.below_id)
    ORDER BY j.created_at_utc DESC, j.job_id DESC
    LIMIT @p_batch_size * 10
) s
ORDER BY s.created_at_utc, s.job_id
LIMIT 1;

CREATE TEMP TABLE _purge_jobs AS
SELECT r.job_id, j.created_at_utc
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
JOIN temp._purge_cursor c
JOIN temp._purge_slice e
WHERE
    j.namespace_id = @p_namespace_id
    AND j.created_at_utc <= c.below
    AND (j.created_at_utc < c.below OR j.job_id < c.below_id)
    AND j.created_at_utc >= e.slice_end
    AND (j.created_at_utc > e.slice_end OR j.job_id >= e.slice_end_id)
    AND r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND r.retention_until_utc IS NOT NULL
    AND r.retention_until_utc < @p_cutoff_utc
    -- Lineage guard: parent_job_id carries no FK, so purging a parent whose children still exist would
    -- orphan their lineage (same rule as the manual purge_job). Only leaves delete in this pass; a
    -- fully-expired subtree drains bottom-up across successive batches.
    AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_job_id = r.job_id)
    -- A finished child is kept until its parent has finished and the parent's own retention is due, because the parent's
    -- replay reads the child's result from the child's row. Trees still drain from the leaves up.
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.jobs_state p
        WHERE p.job_id = j.parent_job_id
          AND (p.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
               OR p.retention_until_utc IS NULL
               OR p.retention_until_utc > @p_cutoff_utc))
    -- A stopped recurring job stays while its definition is live: the next deploy would register its
    -- declaration as a new job and undo the cancel or failure. Retiring the definition lets it drain.
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.schedules s
        JOIN {{schema}}.definitions d ON d.definition_id = j.definition_id
        WHERE s.job_id = j.job_id AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */)
    -- A job stays while an alert of it is undelivered: the failure it reports has reached nobody yet.
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.alerts a
        WHERE a.job_id = j.job_id AND a.delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.Retrying */))
ORDER BY j.created_at_utc DESC, j.job_id DESC
LIMIT @p_batch_size;

DELETE FROM {{schema}}.tags WHERE entity_code = 4 /* EntityCode.Job */ AND entity_id IN (SELECT p.job_id FROM temp._purge_jobs p);

-- The job's settled alerts go with it, so no alert outlives its job.
DELETE FROM {{schema}}.alerts WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);
DELETE FROM {{schema}}.checkpoints WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);
DELETE FROM {{schema}}.steps WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);
DELETE FROM {{schema}}.schedules_state
WHERE schedule_id IN (SELECT s.schedule_id FROM {{schema}}.schedules s WHERE s.job_id IN (SELECT p.job_id FROM temp._purge_jobs p));
DELETE FROM {{schema}}.schedules WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);
DELETE FROM {{schema}}.jobs WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);
DELETE FROM {{schema}}.jobs_state WHERE job_id IN (SELECT p.job_id FROM temp._purge_jobs p);

SELECT
    (SELECT COUNT(*) FROM temp._purge_jobs) AS deleted_count,
    CASE
        WHEN (SELECT COUNT(*) FROM temp._purge_jobs) = @p_batch_size
            THEN (SELECT p.created_at_utc FROM temp._purge_jobs p ORDER BY p.created_at_utc, p.job_id LIMIT 1)
        ELSE (SELECT slice_end FROM temp._purge_slice)
    END AS next_created_utc,
    CASE
        WHEN (SELECT COUNT(*) FROM temp._purge_jobs) = @p_batch_size
            THEN (SELECT p.job_id FROM temp._purge_jobs p ORDER BY p.created_at_utc, p.job_id LIMIT 1)
        ELSE COALESCE((SELECT slice_end_id FROM temp._purge_slice), 0)
    END AS next_job_id;
