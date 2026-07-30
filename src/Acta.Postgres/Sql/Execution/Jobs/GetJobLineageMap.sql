-- Lineage map: five result sets for one focused Job, read at one instant for a consistent snapshot.
-- Postgres and SQLite share this file (WITH RECURSIVE + LIMIT); SQL Server uses the .mssql variant.

-- 1) Focus job.
SELECT j.id, j.job_ref,
       ns.name AS namespace_name, jd.name AS job_name,
       r.status_code,
       j.parent_id, pj.job_ref AS parent_job_ref,
       j.lineage_root_id, lr.job_ref AS lineage_root_job_ref,
       j.created_at_utc, r.modified_at_utc
  FROM {{schema}}.jobs j
  INNER JOIN {{schema}}.runtimes    r  ON r.job_id = j.id
  INNER JOIN {{schema}}.namespaces  ns ON ns.id = j.namespace_id
  INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
  LEFT JOIN {{schema}}.jobs pj ON pj.id = j.parent_id
  LEFT JOIN {{schema}}.jobs lr ON lr.id = j.lineage_root_id
 WHERE j.id = @p_id;

-- 2) Ancestors: walk parent_id from the focus job's parent up to the root, root row first.
WITH RECURSIVE anc(id, parent_id, depth) AS (
    SELECT a.id, a.parent_id, 1
      FROM {{schema}}.jobs a
     WHERE a.id = (SELECT parent_id FROM {{schema}}.jobs WHERE id = @p_id)
    UNION ALL
    SELECT p.id, p.parent_id, anc.depth + 1
      FROM {{schema}}.jobs p
      INNER JOIN anc ON p.id = anc.parent_id
)
SELECT j.id, j.job_ref,
       ns.name AS namespace_name, jd.name AS job_name,
       r.status_code,
       j.parent_id, pj.job_ref AS parent_job_ref,
       j.lineage_root_id, lr.job_ref AS lineage_root_job_ref,
       j.created_at_utc, r.modified_at_utc
  FROM anc
  INNER JOIN {{schema}}.jobs j ON j.id = anc.id
  INNER JOIN {{schema}}.runtimes    r  ON r.job_id = j.id
  INNER JOIN {{schema}}.namespaces  ns ON ns.id = j.namespace_id
  INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
  LEFT JOIN {{schema}}.jobs pj ON pj.id = j.parent_id
  LEFT JOIN {{schema}}.jobs lr ON lr.id = j.lineage_root_id
 ORDER BY anc.depth DESC;

-- 3) Focus job steps, creation order.
SELECT s.name, s.state_code
  FROM {{schema}}.steps s
 WHERE s.job_id = @p_id
 ORDER BY s.id;

-- 4) Focus job checkpoints (signal / timer / variable / progress / child-latch), for the active wait.
SELECT c.kind_code, c.name, c.state_code, c.due_at_utc
  FROM {{schema}}.checkpoints c
 WHERE c.job_id = @p_id
 ORDER BY c.kind_code, c.name;

-- 5) Direct children, newest first, capped at the fetch limit (ChildLimit + 1).
SELECT j.id, j.job_ref,
       jd.name AS job_name,
       r.status_code,
       j.created_at_utc, r.modified_at_utc
  FROM {{schema}}.jobs j
  INNER JOIN {{schema}}.runtimes    r  ON r.job_id = j.id
  INNER JOIN {{schema}}.definitions jd ON jd.id = j.definition_id
 WHERE j.parent_id = @p_id
 ORDER BY j.created_at_utc DESC, j.id DESC
 LIMIT @p_child_limit;
