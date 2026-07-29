SELECT r.status_code
  FROM {{schema}}.runtimes r
 WHERE r.job_id = @p_id;
