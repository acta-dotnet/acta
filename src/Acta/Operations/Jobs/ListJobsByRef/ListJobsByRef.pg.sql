-- Joined from the refs, so each one seeks ux_jobs_ref; a ref no job holds is absent.
SELECT j.job_ref, ns.namespace_name AS job_namespace, jd.definition_name AS job_name
FROM json_array_elements_text(@p_job_refs::json) AS r (job_ref)
INNER JOIN {{schema}}.jobs j ON j.job_ref = r.job_ref::uuid
INNER JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
INNER JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id;
