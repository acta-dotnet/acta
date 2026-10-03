-- Park admission for the sys.outbox operator inbox: insert when the slot is free, supersede when the
-- pending command has outlived the worker-dead window, reject otherwise. See IOutboxSignalStore.ParkAsync.
INSERT INTO {{schema}}.checkpoints (job_id, kind_code, name, status_code, value_format_id, value, modified_at_utc, version)
VALUES (
    @p_job_id,
    20 /* JobCheckpointKindCode.Signal */,
    @p_name,
    20 /* JobCheckpointStatusCode.Set */,
    @p_value_format_id,
    @p_value,
    {{now}},
    0
)
ON CONFLICT (job_id, kind_code, name) DO UPDATE SET
    value_format_id = excluded.value_format_id,
    value = excluded.value,
    status_code = 20 /* JobCheckpointStatusCode.Set */,
    modified_at_utc = {{now}},
    version = checkpoints.version + 1
WHERE checkpoints.modified_at_utc <= @p_stale_before_utc;

-- The minted command id inside @p_value makes the payload unique, so value equality is the
-- "my write landed" test; a losing park reads the incumbent's park instant as the rejection age.
SELECT
    CASE WHEN c.value = @p_value THEN 1 /* ControlAction.Applied */ ELSE 3 /* ControlAction.Rejected */ END AS action,
    c.modified_at_utc AS pending_since_utc
FROM {{schema}}.checkpoints c
WHERE c.job_id = @p_job_id AND c.kind_code = 20 /* JobCheckpointKindCode.Signal */ AND c.name = @p_name;
