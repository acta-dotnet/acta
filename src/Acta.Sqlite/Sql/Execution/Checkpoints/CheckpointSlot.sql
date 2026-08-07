SELECT ACTA_ERROR('Variable payload must use a non-zero format id and non-NULL value.')
WHERE
    @p_action IN (10 /* CheckpointSlotAction.Set */, 30 /* CheckpointSlotAction.GetOrSet */)
    AND (@p_value_format_id = 0 /* JobPayloadFormat.None */ OR @p_value IS NULL);

INSERT INTO {{schema}}.checkpoints (
    job_id, kind_code, name,
    value_format_id, value
)
SELECT
    @p_job_id,
    @p_kind_code,
    @p_name,
    @p_value_format_id,
    @p_value
WHERE @p_action = 10 /* CheckpointSlotAction.Set */
ON CONFLICT (job_id, kind_code, name) DO UPDATE SET
    value_format_id = excluded.value_format_id,
    value = excluded.value,
    modified_at_utc = {{now}},
    version = checkpoints.version + 1;

INSERT INTO {{schema}}.checkpoints (
    job_id, kind_code, name,
    value_format_id, value
)
SELECT
    @p_job_id,
    @p_kind_code,
    @p_name,
    @p_value_format_id,
    @p_value
WHERE @p_action = 30 /* CheckpointSlotAction.GetOrSet */
ON CONFLICT (job_id, kind_code, name) DO NOTHING;

DELETE FROM {{schema}}.checkpoints
WHERE
    @p_action = 50 /* CheckpointSlotAction.Delete */
    AND job_id = @p_job_id
    AND kind_code = @p_kind_code
    AND name = @p_name;

SELECT
    CASE
        WHEN @p_action = 50 /* CheckpointSlotAction.Delete */ THEN (SELECT CHANGES())
        WHEN @p_action = 40 /* CheckpointSlotAction.Exists */
            THEN (
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM {{schema}}.checkpoints AS ev
                    WHERE
                        ev.job_id = @p_job_id
                        AND ev.kind_code = @p_kind_code
                        AND ev.name = @p_name
                ) THEN 1 ELSE 0 END
            )
        WHEN @p_action = 20 /* CheckpointSlotAction.Get */ AND jv.job_id IS NULL THEN 0
        ELSE 1
    END AS found,
    jv.value_format_id,
    jv.value,
    jv.version
FROM (SELECT 1) AS one
LEFT JOIN {{schema}}.checkpoints AS jv
    ON
        @p_action IN (20 /* CheckpointSlotAction.Get */, 30 /* CheckpointSlotAction.GetOrSet */)
        AND jv.job_id = @p_job_id
        AND jv.kind_code = @p_kind_code
        AND jv.name = @p_name;
