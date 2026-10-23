-- The applying tick's read of one pending operator command; empty when the inbox slot is free.
SELECT c.value_format_id, c.value, c.version
FROM {{schema}}.checkpoints c
WHERE
    c.job_id = @p_job_id
    AND c.checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
    AND c.checkpoint_name = @p_checkpoint_name
    AND c.checkpoint_status_code = 20 /* CheckpointStatusCode.Set */;
