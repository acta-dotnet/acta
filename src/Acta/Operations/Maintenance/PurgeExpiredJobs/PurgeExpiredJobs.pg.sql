-- One batch, one implicit transaction; only the final SELECT returns rows. The batch's jobs are locked in a statement of
-- their own, so every delete after it reads what committed before the lock, an alert a raise landed included
-- (docs/sql-internals.md, "A read after a lock").
DROP TABLE IF EXISTS pg_temp._purge_candidates;
DROP TABLE IF EXISTS pg_temp._purge_jobs;
CREATE TEMP TABLE _purge_candidates (job_id BIGINT NOT NULL, retention_until_utc TIMESTAMPTZ NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _purge_jobs (job_id BIGINT NOT NULL) ON COMMIT DROP;

/* A batch of this namespace's expired jobs from ix_jobs_state_retention, in its order after the cursor, read without
   locks. A full batch answers its last candidate, so the next call reads on past it; a shorter one reached the end of
   the expired range and answers no cursor. */
INSERT INTO _purge_candidates (job_id, retention_until_utc)
SELECT r.job_id, r.retention_until_utc
FROM {{schema}}.jobs_state r
WHERE
    r.namespace_id = @p_namespace_id
    AND r.retention_until_utc IS NOT NULL
    AND r.retention_until_utc < @p_cutoff_utc
    AND (r.retention_until_utc, r.job_id) > (@p_after_retention_utc, @p_after_job_id)
ORDER BY r.retention_until_utc, r.job_id
LIMIT @p_batch_size;

-- Each candidate is locked through its state row, where the status and the deadline are read again, and then its own
-- row, skipping a row another transaction holds. The batch is one array, so both rows are sought by job id.
INSERT INTO _purge_jobs (job_id)
SELECT j.job_id
FROM {{schema}}.jobs_state r
JOIN {{schema}}.jobs j ON j.job_id = r.job_id
WHERE
    r.job_id = ANY (ARRAY(SELECT k.job_id FROM _purge_candidates k))
    AND r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND r.retention_until_utc IS NOT NULL
    AND r.retention_until_utc < @p_cutoff_utc
    -- Lineage guard: parent_job_id carries no FK, so purging a parent whose children still
    -- exist would orphan their lineage (same rule as the manual purge_job). Only leaves
    -- delete; a fully-expired subtree drains bottom-up across iterations.
    AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs c WHERE c.parent_job_id = j.job_id)
    -- A finished child is kept until its parent has finished and the parent's own retention is due, because the
    -- parent's replay reads the child's result from the child's row. Trees still drain from the leaves up.
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.jobs_state p
        WHERE p.job_id = j.parent_job_id
          AND (p.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
               OR p.retention_until_utc IS NULL
               OR p.retention_until_utc > @p_cutoff_utc))
    -- A stopped recurring job stays while its definition is live: the next deploy would register
    -- its declaration as a new job and undo the cancel or failure. Retiring the definition lets
    -- it drain.
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.schedules s
        JOIN {{schema}}.definitions d ON d.definition_id = j.definition_id
        WHERE s.job_id = j.job_id AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */)
    -- A job stays while an alert of it is undelivered: the failure it reports has reached nobody yet.
    AND NOT EXISTS (
        SELECT 1 FROM {{schema}}.alerts a
        WHERE a.job_id = j.job_id AND a.delivery_status_code IN (10 /* AlertDeliveryStatusCode.Pending */, 20 /* AlertDeliveryStatusCode.Retrying */))
FOR UPDATE OF r, j SKIP LOCKED;

-- Each delete takes the batch as one array and seeks its rows by job id.
DELETE FROM {{schema}}.tags
WHERE
    entity_code = 4 /* EntityCode.Job */
    AND entity_id = ANY (ARRAY(SELECT p.job_id FROM _purge_jobs p));
-- The job's settled alerts go with it, so no alert outlives its job (docs/sql-internals.md, "Alert lock order").
DELETE FROM {{schema}}.alerts a WHERE a.job_id = ANY (ARRAY(SELECT p.job_id FROM _purge_jobs p));
DELETE FROM {{schema}}.checkpoints c WHERE c.job_id = ANY (ARRAY(SELECT p.job_id FROM _purge_jobs p));
DELETE FROM {{schema}}.steps s WHERE s.job_id = ANY (ARRAY(SELECT p.job_id FROM _purge_jobs p));
DELETE FROM {{schema}}.schedules_state st
WHERE st.schedule_id IN (SELECT s.schedule_id FROM {{schema}}.schedules s WHERE s.job_id = ANY (ARRAY(SELECT p.job_id FROM _purge_jobs p)));
DELETE FROM {{schema}}.schedules s WHERE s.job_id = ANY (ARRAY(SELECT p.job_id FROM _purge_jobs p));
DELETE FROM {{schema}}.jobs j WHERE j.job_id = ANY (ARRAY(SELECT p.job_id FROM _purge_jobs p));
DELETE FROM {{schema}}.jobs_state r WHERE r.job_id = ANY (ARRAY(SELECT p.job_id FROM _purge_jobs p));

SELECT
    (SELECT count(*)::INT FROM _purge_jobs) AS deleted_count,
    l.retention_until_utc AS next_retention_utc,
    COALESCE(l.job_id, 0) AS next_job_id
FROM (SELECT 1) one
LEFT JOIN (
    SELECT k.retention_until_utc, k.job_id
    FROM _purge_candidates k
    WHERE (SELECT count(*) FROM _purge_candidates) = @p_batch_size
    ORDER BY k.retention_until_utc DESC, k.job_id DESC
    LIMIT 1
) l ON true;
