-- One variable of the job by its canonical name, sought on the key; empty when the handler has not set it.
SELECT c.value_format_id, c.value, c.version
FROM {{schema}}.checkpoints c
WHERE
    c.job_id = @p_job_id
    AND c.checkpoint_kind_code = 10 /* CheckpointKindCode.Variable */
    AND c.checkpoint_name = @p_checkpoint_name;
