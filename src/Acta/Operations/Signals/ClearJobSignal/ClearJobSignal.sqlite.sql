-- A finished job, or a permanent job between firings, forgets the signal, so its next wait for it waits again. The
-- event goes first, since it reads the checkpoint the delete removes.
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
    actor,
    details_format_id,
    details
)
SELECT
    82 /* EventCode.JobSignalCleared */,
    {{now}},
    j.namespace_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    @p_event_message,
    @p_actor,
    @p_details_format_id,
    @p_details
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
JOIN {{schema}}.checkpoints c
    ON
        c.job_id = j.job_id
        AND c.checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
        AND c.checkpoint_name = @p_checkpoint_name
WHERE
    j.job_id = @p_job_id
    AND r.job_status_code IN (
        40 /* JobStatusCode.Idle */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
    );

DELETE FROM {{schema}}.checkpoints
WHERE
    job_id = @p_job_id
    AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
    AND checkpoint_name = @p_checkpoint_name
    AND EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state r
        WHERE
            r.job_id = @p_job_id
            AND r.job_status_code IN (
                40 /* JobStatusCode.Idle */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
            )
    );

-- The delete is the last write, so changes() counts it.
SELECT
    CASE
        WHEN r.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN changes() > 0 THEN 1 /* ControlAction.Applied */
        WHEN r.job_status_code IN (
            40 /* JobStatusCode.Idle */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
        ) THEN 6 /* ControlAction.AlreadyInState */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    r.job_status_code AS status
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN {{schema}}.jobs_state r ON r.job_id = q.qid;
