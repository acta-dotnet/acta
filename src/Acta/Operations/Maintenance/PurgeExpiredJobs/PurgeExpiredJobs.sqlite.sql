-- SQLite owns one immediate transaction for the file.
DROP TABLE IF EXISTS temp._purge_candidates;
DROP TABLE IF EXISTS temp._purge_jobs;

-- A batch of this namespace's expired jobs from ix_jobs_state_retention, in its order after the cursor. A full batch
-- answers its last candidate, so the next call reads on past it; a shorter one reached the end of the expired range and
-- answers no cursor.
CREATE TEMP TABLE _purge_candidates AS
SELECT r.job_id, r.retention_until_utc
FROM {{schema}}.jobs_state r
WHERE
    r.namespace_id = @p_namespace_id
    AND r.retention_until_utc IS NOT NULL
    AND r.retention_until_utc < @p_cutoff_utc
    AND (r.retention_until_utc, r.job_id) > (@p_after_retention_utc, @p_after_job_id)
ORDER BY r.retention_until_utc, r.job_id
LIMIT @p_batch_size;

-- Each candidate's status and deadline are read again on its state row. CROSS JOIN keeps the candidates the outer
-- loop: with a plain JOIN the planner can walk the whole retention index for r.
CREATE TEMP TABLE _purge_jobs AS
SELECT r.job_id
FROM temp._purge_candidates k
CROSS JOIN {{schema}}.jobs_state r ON r.job_id = k.job_id
CROSS JOIN {{schema}}.jobs j ON j.job_id = r.job_id
WHERE
    r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
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
        WHERE a.job_id = j.job_id AND a.delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.Retrying */));

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
    l.retention_until_utc AS next_retention_utc,
    COALESCE(l.job_id, 0) AS next_job_id
FROM (SELECT 1 AS one) o
LEFT JOIN (
    SELECT k.retention_until_utc, k.job_id
    FROM temp._purge_candidates k
    WHERE (SELECT COUNT(*) FROM temp._purge_candidates) = @p_batch_size
    ORDER BY k.retention_until_utc DESC, k.job_id DESC
    LIMIT 1
) l ON 1 = 1;
