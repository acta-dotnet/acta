SELECT
    result_format_id,
    result
FROM {{schema}}.runtimes
WHERE
    job_id = @p_job_id
    AND result_format_id <> 0 /* JobPayloadFormat.None */
    AND (@p_execution_number IS NULL OR execution_number = @p_execution_number);
