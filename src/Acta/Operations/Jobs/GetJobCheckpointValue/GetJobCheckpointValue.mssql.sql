-- One checkpoint's value by kind and stored name, sought on the key, with the time of its last write; empty when it
-- holds none. The scheduler's firing envelope is engine state the checkpoint list leaves out, so this read leaves it
-- out too.
SELECT c.value_format_id, c.modified_at_utc, c.value
FROM {{schema}}.checkpoints c
WHERE
    c.job_id = @p_job_id
    AND c.checkpoint_kind_code = @p_checkpoint_kind_code
    AND c.checkpoint_kind_code <> 60 /* CheckpointKindCode.Firing */
    AND c.checkpoint_name = @p_checkpoint_name
    AND c.value_format_id <> 0 /* JobPayloadFormat.None */;
