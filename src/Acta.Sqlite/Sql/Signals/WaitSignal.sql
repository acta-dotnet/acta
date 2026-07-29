INSERT INTO {{schema}}.checkpoints (
    job_id, kind_code, name, state_code,
    value_format_id, value, modified_at_utc, version)
VALUES (
    @p_job_id, @p_kind_code, @p_name,
    10 /* JobCheckpointStateCode.Pending */,
    0 /* JobPayloadFormat.None */,
    NULL, {{now}}, 0)
ON CONFLICT (job_id, kind_code, name) DO NOTHING;

SELECT
    CASE
        WHEN js.state_code = 20 /* JobCheckpointStateCode.Set */
        THEN 2 /* SignalWaitOutcomeCode.ContinueSet */
        ELSE 1 /* SignalWaitOutcomeCode.SuspendPending */
    END AS outcome_code,
    CASE
        WHEN js.state_code = 20 /* JobCheckpointStateCode.Set */ THEN js.value_format_id
        ELSE 0 /* JobPayloadFormat.None */
    END AS value_format_id,
    CASE
        WHEN js.state_code = 20 /* JobCheckpointStateCode.Set */ THEN js.value
        ELSE NULL
    END AS value
FROM {{schema}}.checkpoints js
WHERE js.job_id = @p_job_id AND js.kind_code = @p_kind_code AND js.name = @p_name;
