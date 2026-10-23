/* Slot arbiter for one durable wait, ordered so the final SELECT reads a settled row: flip an overdue
   Pending to Expired first, then arm. */
/* Arming is one-directional. A NULL due_at_utc is armed when the caller carries a timeout, so code
   redeployed with a bound can un-strand a wait suspended without one; a stored due is never
   overwritten, never extended, and never cleared by a subsequent unbounded call. */
/* Nothing here reads a captured pre-state, so a first arrival that loses the insert needs no re-read
   the way pg's routine does: the arming update below applies to whatever row now stands, and the
   final SELECT reports it settled. */
/* A wait this call armed suspends once, even with an already-elapsed deadline: the flip runs before
   the arm, so only a wait armed by an earlier call can expire. Pg and mssql state the same rule. */
UPDATE {{schema}}.checkpoints
SET
    checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id AND checkpoint_kind_code = @p_checkpoint_kind_code AND checkpoint_name = @p_checkpoint_name
    AND checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
    AND due_at_utc IS NOT NULL
    AND due_at_utc <= {{now}};

INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, due_at_utc,
    value_format_id, value, modified_at_utc, version
)
VALUES (
    @p_job_id, @p_checkpoint_kind_code, @p_checkpoint_name,
    10 /* CheckpointStatusCode.Pending */,
    CASE WHEN @p_timeout_seconds IS NULL THEN NULL ELSE {{now}} + (@p_timeout_seconds) * 1000 END,
    0 /* JobPayloadFormat.None */,
    NULL, {{now}}, 0
)
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO NOTHING;

UPDATE {{schema}}.checkpoints
SET
    due_at_utc = {{now}} + (@p_timeout_seconds) * 1000,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id AND checkpoint_kind_code = @p_checkpoint_kind_code AND checkpoint_name = @p_checkpoint_name
    AND checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
    AND due_at_utc IS NULL
    AND @p_timeout_seconds IS NOT NULL;

SELECT
    CASE
        WHEN js.checkpoint_status_code = 20 /* CheckpointStatusCode.Set */
            THEN 2 /* WaitSignalOutcomeCode.ContinueSet */
        WHEN js.checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */
            THEN 3 /* WaitSignalOutcomeCode.TimedOut */
        ELSE 1 /* WaitSignalOutcomeCode.SuspendPending */
    END AS outcome_code,
    CASE
        WHEN js.checkpoint_status_code = 20 /* CheckpointStatusCode.Set */ THEN js.value_format_id
        ELSE 0 /* JobPayloadFormat.None */
    END AS value_format_id,
    CASE
        WHEN js.checkpoint_status_code = 20 /* CheckpointStatusCode.Set */ THEN js.value
        ELSE NULL
    END AS value
FROM {{schema}}.checkpoints js
WHERE js.job_id = @p_job_id AND js.checkpoint_kind_code = @p_checkpoint_kind_code AND js.checkpoint_name = @p_checkpoint_name;
