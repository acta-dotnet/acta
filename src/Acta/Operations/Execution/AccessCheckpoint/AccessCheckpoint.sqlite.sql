SELECT ACTA_ERROR('Variable payload must use a non-zero format id and non-NULL value.')
WHERE
    @p_action IN (10 /* AccessCheckpointAction.Set */, 30 /* AccessCheckpointAction.GetOrSet */)
    AND (@p_value_format_id = 0 /* JobPayloadFormat.None */ OR @p_value IS NULL);

-- A write lands only while this attempt holds the job's row (docs/sql-internals.md, "Execution fence").
SELECT ACTA_ERROR('ACTA:EXECUTION_FENCED:access_checkpoint: the job is no longer executing this attempt.')
WHERE
    @p_action IN (10 /* AccessCheckpointAction.Set */, 30 /* AccessCheckpointAction.GetOrSet */, 50 /* AccessCheckpointAction.Delete */)
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state r
        WHERE
            r.job_id = @p_job_id
            AND r.job_status_code = 50 /* JobStatusCode.Executing */
            AND r.execution_number = @p_execution_number
            AND r.leased_by_worker_id = @p_leased_by_worker_id
    );

INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name,
    value_format_id, value
)
SELECT
    @p_job_id,
    @p_checkpoint_kind_code,
    @p_checkpoint_name,
    @p_value_format_id,
    @p_value
WHERE @p_action = 10 /* AccessCheckpointAction.Set */
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
    value_format_id = excluded.value_format_id,
    value = excluded.value,
    modified_at_utc = {{now}},
    version = checkpoints.version + 1;

INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name,
    value_format_id, value
)
SELECT
    @p_job_id,
    @p_checkpoint_kind_code,
    @p_checkpoint_name,
    @p_value_format_id,
    @p_value
WHERE @p_action = 30 /* AccessCheckpointAction.GetOrSet */
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO NOTHING;

DELETE FROM {{schema}}.checkpoints
WHERE
    @p_action = 50 /* AccessCheckpointAction.Delete */
    AND job_id = @p_job_id
    AND checkpoint_kind_code = @p_checkpoint_kind_code
    AND checkpoint_name = @p_checkpoint_name;

SELECT
    CASE
        WHEN @p_action = 50 /* AccessCheckpointAction.Delete */ THEN (SELECT CHANGES())
        WHEN @p_action = 40 /* AccessCheckpointAction.Exists */
            THEN (
                CASE WHEN EXISTS (
                    SELECT 1
                    FROM {{schema}}.checkpoints AS ev
                    WHERE
                        ev.job_id = @p_job_id
                        AND ev.checkpoint_kind_code = @p_checkpoint_kind_code
                        AND ev.checkpoint_name = @p_checkpoint_name
                ) THEN 1 ELSE 0 END
            )
        WHEN @p_action = 20 /* AccessCheckpointAction.Get */ AND jv.job_id IS NULL THEN 0
        ELSE 1
    END AS found,
    jv.value_format_id,
    jv.value,
    jv.version
FROM (SELECT 1) AS one
LEFT JOIN {{schema}}.checkpoints AS jv
    ON
        @p_action IN (20 /* AccessCheckpointAction.Get */, 30 /* AccessCheckpointAction.GetOrSet */)
        AND jv.job_id = @p_job_id
        AND jv.checkpoint_kind_code = @p_checkpoint_kind_code
        AND jv.checkpoint_name = @p_checkpoint_name;
