-- The job's namespace and name come with the result, so a typed read checks the result type in the same round trip.
SELECT
    ns.namespace_name AS job_namespace,
    jd.definition_name AS job_name,
    r.result_format_id,
    r.result
FROM {{schema}}.jobs_state r
JOIN {{schema}}.jobs j ON j.job_id = r.job_id
JOIN {{schema}}.namespaces ns ON ns.namespace_id = j.namespace_id
JOIN {{schema}}.definitions jd ON jd.definition_id = j.definition_id
WHERE
    r.job_id = @p_job_id
    AND r.result_format_id <> 0 /* JobPayloadFormat.None */
    AND (@p_execution_number IS NULL OR r.execution_number = @p_execution_number);
