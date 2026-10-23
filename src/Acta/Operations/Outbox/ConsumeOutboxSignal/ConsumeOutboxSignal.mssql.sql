-- Version-CAS consume of an applied operator command: no row back means a newer command superseded
-- this one mid-apply, and it survives for the next tick.
DELETE FROM {{schema}}.checkpoints
OUTPUT CAST(1 AS BIGINT) AS consumed
WHERE
    job_id = @p_job_id
    AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
    AND checkpoint_name = @p_checkpoint_name
    AND version = @p_expected_version;
