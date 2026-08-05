CREATE OR REPLACE FUNCTION {{schema}}.get_stale_child_latches(
    p_namespace_id SMALLINT
)
RETURNS TABLE(parent_job_id BIGINT, child_job_id BIGINT, child_status SMALLINT)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT js.job_id,
           CASE WHEN js.name ~ '^sys\.child\.[0-9]+$' THEN substring(js.name from 11)::bigint END,
           cr.status_code
      FROM {{schema}}.checkpoints js
      INNER JOIN {{schema}}.jobs p ON p.id = js.job_id AND p.namespace_id = p_namespace_id
      LEFT JOIN {{schema}}.jobs c
              ON c.id = CASE WHEN js.name ~ '^sys\.child\.[0-9]+$' THEN substring(js.name from 11)::bigint END
      LEFT JOIN {{schema}}.runtimes cr ON cr.job_id = c.id
     WHERE js.kind_code = 50 /* JobCheckpointKindCode.ChildLatch */
       AND js.name ~ '^sys\.child\.[0-9]+$'
       AND js.status_code = 10 /* JobCheckpointStatusCode.Pending */
       AND (c.id IS NULL OR cr.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */));
END;
$$;
