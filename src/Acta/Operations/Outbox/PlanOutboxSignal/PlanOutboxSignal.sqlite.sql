-- Rewrites the operator command the tick read into its plan: the same command carrying the Quarantined rows it
-- targets. Only a pass whose read still matches the stored value plans it; any other pass stops before it
-- touches the source.
UPDATE {{schema}}.checkpoints
SET value = @p_planned_value
WHERE
    job_id = @p_job_id
    AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
    AND checkpoint_name = @p_checkpoint_name
    AND value = @p_value
RETURNING 1 AS planned;
