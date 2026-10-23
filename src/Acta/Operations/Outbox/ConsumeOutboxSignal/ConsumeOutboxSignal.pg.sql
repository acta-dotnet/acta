-- Deletes the command the tick read, matched by its stored value, which carries the command id minted at park, and
-- only when it did writes the evidence event, so one command leaves one event. Any other command under the name, a
-- superseding one or one parked at the same version after another pass consumed it, survives for the next tick.
WITH consumed AS (
    DELETE FROM {{schema}}.checkpoints
    WHERE
        job_id = @p_job_id
        AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
        AND checkpoint_name = @p_checkpoint_name
        AND value = @p_value
    RETURNING job_id
),
recorded AS (
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
        now(),
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
    FROM consumed c
    INNER JOIN {{schema}}.jobs j ON j.job_id = c.job_id
    INNER JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
)
SELECT COUNT(*)::BIGINT AS consumed FROM consumed;
