-- One batch, one implicit transaction; only the final SELECT returns rows. The batch's jobs are locked in a statement of
-- their own, so every delete after it reads what committed before the lock, an alert a raise landed included
-- (docs/sql-internals.md, "A read after a lock").
DROP TABLE IF EXISTS pg_temp._purge_slice;
DROP TABLE IF EXISTS pg_temp._purge_jobs;
CREATE TEMP TABLE _purge_slice (
    below TIMESTAMPTZ NOT NULL,
    below_id BIGINT NOT NULL,
    slice_end TIMESTAMPTZ,
    slice_end_id BIGINT
) ON COMMIT DROP;
CREATE TEMP TABLE _purge_jobs (job_id BIGINT NOT NULL, created_at_utc TIMESTAMPTZ NOT NULL) ON COMMIT DROP;

/* No retention index: walk this namespace's jobs from newer to older through ix_jobs_namespace_created, ten batches a
   slice, and delete the expired ones in it. A cycle ends at the namespace's oldest job, which answers no cursor; a full
   batch ends the slice at its last delete, so the walk moves on past what it deleted. */
INSERT INTO _purge_slice (below, below_id, slice_end, slice_end_id)
SELECT b.below, b.below_id, s.created_at_utc, s.job_id
FROM (
    -- A cycle starts at the newest job old enough to have expired: none expires sooner than the shortest retention any
    -- definition of the namespace has, a retired one included. The framework's own are left out: they run only
    -- permanent jobs, and their default would hold a longer-keeping namespace's walk to it.
    SELECT
        COALESCE(@p_before_created_utc, @p_cutoff_utc - make_interval(secs => COALESCE(min(d.retention_seconds_effective), 0))) AS below,
        CASE WHEN @p_before_created_utc IS NULL THEN 9223372036854775807 ELSE @p_before_job_id END AS below_id
    FROM {{schema}}.definitions d
    WHERE d.namespace_id = @p_namespace_id AND d.definition_name NOT LIKE 'sys.%'
) b
LEFT JOIN LATERAL (
    SELECT w.created_at_utc, w.job_id
    FROM (
        SELECT j.created_at_utc, j.job_id
        FROM {{schema}}.jobs j
        WHERE j.namespace_id = @p_namespace_id AND (j.created_at_utc, j.job_id) < (b.below, b.below_id)
        ORDER BY j.created_at_utc DESC, j.job_id DESC
        LIMIT @p_batch_size * 10
    ) w
    ORDER BY w.created_at_utc, w.job_id
    LIMIT 1
) s ON true;

INSERT INTO _purge_jobs (job_id, created_at_utc)
SELECT j.job_id, j.created_at_utc
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.namespace_id = @p_namespace_id
    AND (j.created_at_utc, j.job_id) < (SELECT s.below, s.below_id FROM _purge_slice s)
    AND (j.created_at_utc, j.job_id) >= (SELECT s.slice_end, s.slice_end_id FROM _purge_slice s)
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
ORDER BY j.created_at_utc DESC, j.job_id DESC
LIMIT @p_batch_size
FOR UPDATE OF j, r SKIP LOCKED;

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
    t.deleted_count,
    CASE WHEN t.deleted_count = @p_batch_size THEN l.created_at_utc ELSE s.slice_end END AS next_created_utc,
    CASE WHEN t.deleted_count = @p_batch_size THEN l.job_id ELSE COALESCE(s.slice_end_id, 0) END AS next_job_id
FROM _purge_slice s
CROSS JOIN (SELECT count(*)::INT AS deleted_count FROM _purge_jobs) t
LEFT JOIN LATERAL (SELECT p.created_at_utc, p.job_id FROM _purge_jobs p ORDER BY p.created_at_utc, p.job_id LIMIT 1) l ON true;
