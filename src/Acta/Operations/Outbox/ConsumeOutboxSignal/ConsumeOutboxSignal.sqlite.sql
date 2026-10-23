-- Writes the evidence event and deletes the command the tick read, both matched by its stored value, which carries the
-- command id minted at park, so one command leaves one event and any other command under the name survives. SQLite
-- runs one writer at a time, so the event's guard and the delete read the same row.
INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    definition_id,
    tenant_id,
    lineage_root_job_id,
    execution_number,
    event_message,
    details_format_id,
    details
)
SELECT
    @p_event_code,
    {{now}},
    j.namespace_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM {{schema}}.jobs j
INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND EXISTS (
        SELECT 1
        FROM {{schema}}.checkpoints c
        WHERE
            c.job_id = @p_job_id
            AND c.checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
            AND c.checkpoint_name = @p_checkpoint_name
            AND c.value = @p_value
    );

DELETE FROM {{schema}}.checkpoints
WHERE
    job_id = @p_job_id
    AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
    AND checkpoint_name = @p_checkpoint_name
    AND value = @p_value
RETURNING 1 AS consumed;
