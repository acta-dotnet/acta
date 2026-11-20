-- Joined from the refs, so each one seeks ux_jobs_ref; a ref no job holds is absent.
SELECT j.job_ref, ns.namespace_name AS job_namespace, jd.definition_name AS job_name
FROM OPENJSON (@p_job_refs) AS r
INNER JOIN {{schema}}.jobs AS j ON j.job_ref = CAST(r.[value] AS UNIQUEIDENTIFIER)
INNER JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = j.namespace_id
INNER JOIN {{schema}}.definitions AS jd ON jd.definition_id = j.definition_id;
