-- The applying tick's read of one pending operator command; empty when the inbox slot is free.
SELECT c.value_format_id, c.value, c.version
FROM {{schema}}.checkpoints c
WHERE
    c.job_id = @p_job_id
    AND c.kind_code = 20 /* JobCheckpointKindCode.Signal */
    AND c.name = @p_name
    AND c.status_code = 20 /* JobCheckpointStatusCode.Set */;
