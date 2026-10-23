-- Deletes the operator command the tick read, matched by its stored value, which carries the command id
-- minted at park. Any other command under the name, one that superseded it or one parked at the same version
-- after another pass consumed it, never matches and survives for the next tick.
DELETE FROM {{schema}}.checkpoints
WHERE
    job_id = @p_job_id
    AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
    AND checkpoint_name = @p_checkpoint_name
    AND value = @p_value
RETURNING 1 AS consumed;
