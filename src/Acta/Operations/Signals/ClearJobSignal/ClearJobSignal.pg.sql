-- One statement in a raise's lock order (docs/sql-internals.md, "Step, checkpoint and rate locks"): the scalar
-- subquery locks the signal's checkpoint row before the scan reaches the job's row, which target then locks, so the
-- status the delete reads holds to commit. A finished job, or a permanent job between firings, forgets the signal.
WITH target AS (
    SELECT r.job_id, r.job_status_code, r.execution_number, j.namespace_id, j.lineage_root_job_id, j.definition_id, j.tenant_id
    FROM {{schema}}.jobs_state r
    JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    WHERE r.job_id = (
        SELECT c.job_id
        FROM {{schema}}.checkpoints c
        WHERE
            c.job_id = @p_job_id
            AND c.checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
            AND c.checkpoint_name = @p_checkpoint_name
        FOR UPDATE
    )
    FOR UPDATE OF r
),
cleared AS (
    DELETE FROM {{schema}}.checkpoints c
    USING target t
    WHERE
        c.job_id = t.job_id
        AND c.checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */
        AND c.checkpoint_name = @p_checkpoint_name
        AND t.job_status_code IN (
            40 /* JobStatusCode.Idle */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
        )
    RETURNING t.job_id
),
recorded AS (
    INSERT INTO {{schema}}.events (
        event_code, created_at_utc, namespace_id,
        entity_code, entity_id,
        definition_id, tenant_id, lineage_root_job_id,
        execution_number,
        event_message, actor, details_format_id, details
    )
    SELECT
        22 /* EventCode.JobSignalCleared */, now(), t.namespace_id,
        4 /* EntityCode.Job */, t.job_id,
        t.definition_id, t.tenant_id, COALESCE(t.lineage_root_job_id, t.job_id),
        t.execution_number,
        @p_event_message, @p_actor, @p_details_format_id, @p_details
    FROM target t
    WHERE EXISTS (SELECT 1 FROM cleared)
)
-- With no signal nothing is locked or written, and the job's status read from the snapshot answers.
SELECT
    CASE
        WHEN r.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN EXISTS (SELECT 1 FROM cleared) THEN 1 /* ControlAction.Applied */
        WHEN COALESCE(t.job_status_code, r.job_status_code) IN (
            40 /* JobStatusCode.Idle */, 100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
        ) THEN 6 /* ControlAction.AlreadyInState */
        ELSE 3 /* ControlAction.Rejected */
    END::SMALLINT AS action,
    COALESCE(t.job_status_code, r.job_status_code) AS status
FROM (SELECT 1) one
LEFT JOIN {{schema}}.jobs_state r ON r.job_id = @p_job_id
LEFT JOIN target t ON true;
