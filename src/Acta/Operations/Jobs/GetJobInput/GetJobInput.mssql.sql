-- The job's namespace and name come with the input, so a typed read checks the input type in the same round trip.
SELECT
    ns.namespace_name AS job_namespace,
    jd.definition_name AS job_name,
    j.input_format_id,
    j.input
FROM {{schema}}.jobs j
JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
WHERE j.job_id = @p_job_id;
