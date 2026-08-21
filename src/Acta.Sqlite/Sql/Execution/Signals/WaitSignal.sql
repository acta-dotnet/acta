/* Slot arbiter for one durable wait, ordered so the final SELECT reads a settled row: flip an overdue
   Pending to Expired first, then arm. */
/* Arming is one-directional. A NULL due_at_utc is armed when the caller carries a timeout, so code
   redeployed with a bound can un-strand a wait suspended without one; a stored due is never
   overwritten, never extended, and never cleared by a subsequent unbounded call. */
UPDATE {{schema}}.checkpoints
SET
    status_code = 30 /* JobCheckpointStatusCode.Expired */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id AND kind_code = @p_kind_code AND name = @p_name
    AND status_code = 10 /* JobCheckpointStatusCode.Pending */
    AND due_at_utc IS NOT NULL
    AND due_at_utc <= {{now}};

INSERT INTO {{schema}}.checkpoints (
    job_id, kind_code, name, status_code, due_at_utc,
    value_format_id, value, modified_at_utc, version
)
VALUES (
    @p_job_id, @p_kind_code, @p_name,
    10 /* JobCheckpointStatusCode.Pending */,
    CASE WHEN @p_timeout_seconds IS NULL THEN NULL ELSE {{now}} + (@p_timeout_seconds) * 1000 END,
    0 /* JobPayloadFormat.None */,
    NULL, {{now}}, 0
)
ON CONFLICT (job_id, kind_code, name) DO NOTHING;

UPDATE {{schema}}.checkpoints
SET
    due_at_utc = {{now}} + (@p_timeout_seconds) * 1000,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id AND kind_code = @p_kind_code AND name = @p_name
    AND status_code = 10 /* JobCheckpointStatusCode.Pending */
    AND due_at_utc IS NULL
    AND @p_timeout_seconds IS NOT NULL;

SELECT
    CASE
        WHEN js.status_code = 20 /* JobCheckpointStatusCode.Set */
            THEN 2 /* SignalWaitOutcomeCode.ContinueSet */
        WHEN js.status_code = 30 /* JobCheckpointStatusCode.Expired */
            THEN 3 /* SignalWaitOutcomeCode.TimedOut */
        ELSE 1 /* SignalWaitOutcomeCode.SuspendPending */
    END AS outcome_code,
    CASE
        WHEN js.status_code = 20 /* JobCheckpointStatusCode.Set */ THEN js.value_format_id
        ELSE 0 /* JobPayloadFormat.None */
    END AS value_format_id,
    CASE
        WHEN js.status_code = 20 /* JobCheckpointStatusCode.Set */ THEN js.value
        ELSE NULL
    END AS value
FROM {{schema}}.checkpoints js
WHERE js.job_id = @p_job_id AND js.kind_code = @p_kind_code AND js.name = @p_name;
