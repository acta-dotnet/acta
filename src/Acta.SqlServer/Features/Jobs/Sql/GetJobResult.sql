SELECT TOP 1 execution_number, result_format_id, result, created_at_utc
  FROM {{schema}}.results
 WHERE job_id = @p_job_id
   AND (@p_execution_number IS NULL OR execution_number = @p_execution_number)
 ORDER BY execution_number DESC;
