-- A CROSS JOIN keeps the refs the outer loop, which SQLite never reorders, so each ref seeks ux_jobs_ref; a ref no job
-- holds is absent.
SELECT j.job_ref, ns.namespace_name AS job_namespace, jd.definition_name AS job_name
FROM json_each(@p_job_refs) r
CROSS JOIN {{schema}}.jobs j
INNER JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
WHERE j.job_ref = r.value;
