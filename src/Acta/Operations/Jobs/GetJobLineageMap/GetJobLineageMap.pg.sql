-- Lineage map: five result sets for one focused Job, read at one instant for a consistent snapshot.
-- Postgres and SQLite share this file (WITH RECURSIVE + LIMIT); SQL Server uses the .mssql variant.

-- 1) Focus job.
SELECT
    j.job_id,
    j.job_ref,
    ns.namespace_name AS job_namespace,
    jd.definition_name AS job_name,
    r.job_status_code AS status,
    j.parent_job_id,
    pj.job_ref AS parent_job_ref,
    j.lineage_root_job_id,
    lr.job_ref AS lineage_root_job_ref,
    j.created_at_utc,
    r.modified_at_utc
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
INNER JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
LEFT JOIN {{schema}}.jobs pj ON pj.job_id = j.parent_job_id
LEFT JOIN {{schema}}.jobs lr ON lr.job_id = j.lineage_root_job_id
WHERE j.job_id = @p_job_id;

-- 2) Ancestors: walk parent_job_id from the focus job's parent up to the root, root row first.
WITH RECURSIVE anc (job_id, parent_job_id, depth) AS (
    SELECT a.job_id, a.parent_job_id, 1
    FROM {{schema}}.jobs a
    WHERE a.job_id = (SELECT parent_job_id FROM {{schema}}.jobs WHERE job_id = @p_job_id)
    UNION ALL
    SELECT p.job_id, p.parent_job_id, anc.depth + 1
    FROM {{schema}}.jobs p
    INNER JOIN anc ON p.job_id = anc.parent_job_id
)
SELECT
    j.job_id,
    j.job_ref,
    ns.namespace_name AS job_namespace,
    jd.definition_name AS job_name,
    r.job_status_code AS status,
    j.parent_job_id,
    pj.job_ref AS parent_job_ref,
    j.lineage_root_job_id,
    lr.job_ref AS lineage_root_job_ref,
    j.created_at_utc,
    r.modified_at_utc
FROM anc
INNER JOIN {{schema}}.jobs j ON j.job_id = anc.job_id
INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
INNER JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
LEFT JOIN {{schema}}.jobs pj ON pj.job_id = j.parent_job_id
LEFT JOIN {{schema}}.jobs lr ON lr.job_id = j.lineage_root_job_id
ORDER BY anc.depth DESC;

-- 3) Focus job steps, creation order.
SELECT s.step_name, s.step_status_code AS status
FROM {{schema}}.steps s
WHERE s.job_id = @p_job_id
ORDER BY s.created_at_utc, s.step_name;

-- 4) Focus job checkpoints (signal / timer / variable / progress / child-latch), for the active wait.
SELECT c.checkpoint_kind_code AS kind, c.checkpoint_name, c.checkpoint_status_code AS status, c.due_at_utc
FROM {{schema}}.checkpoints c
WHERE c.job_id = @p_job_id
ORDER BY c.checkpoint_kind_code, c.checkpoint_name;

-- 5) Direct children, newest first, capped at the fetch limit (ChildLimit + 1).
SELECT
    j.job_id,
    j.job_ref,
    jd.definition_name AS job_name,
    r.job_status_code AS status,
    j.created_at_utc,
    r.modified_at_utc
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
WHERE j.parent_job_id = @p_job_id
ORDER BY j.created_at_utc DESC, j.job_id DESC
LIMIT @p_child_limit;
