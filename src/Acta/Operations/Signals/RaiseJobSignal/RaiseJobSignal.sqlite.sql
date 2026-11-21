/* No revival: an Expired latch already resolved the wait TimedOut, so a late child latch writes nothing and
   releases no job, but the landing still happened and the audit event still records it with a message
   saying why it changed nothing. The pre-state is captured first because the upsert overwrites it. */
DROP TABLE IF EXISTS temp._raise_expired;

CREATE TEMP TABLE _raise_expired AS
SELECT 1 AS expired
FROM {{schema}}.checkpoints c
WHERE
    c.job_id = @p_job_id
    AND c.checkpoint_kind_code = @p_checkpoint_kind_code
    AND c.checkpoint_name = @p_checkpoint_name
    AND c.checkpoint_status_code = 150 /* CheckpointStatusCode.Expired */;

-- Only the checkpoint the job waits on releases it, so whether it was Pending is captured before the upsert too.
DROP TABLE IF EXISTS temp._raise_pending;

CREATE TEMP TABLE _raise_pending AS
SELECT 1 AS pending
FROM {{schema}}.checkpoints c
WHERE
    c.job_id = @p_job_id
    AND c.checkpoint_kind_code = @p_checkpoint_kind_code
    AND c.checkpoint_name = @p_checkpoint_name
    AND c.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */;

-- A child latch for an outcome the child no longer holds writes nothing: the child was restarted since, and its
-- next landing raises the latch itself. A purged child has no row, and its latch is raised.
DROP TABLE IF EXISTS temp._raise_stale;
CREATE TEMP TABLE _raise_stale AS
SELECT 1 AS stale
FROM {{schema}}.jobs_state c
WHERE @p_child_job_id IS NOT NULL AND c.job_id = @p_child_job_id AND c.job_status_code <> @p_child_status;

-- First raise wins: a signal never changes once raised, so a repeat writes nothing. The same value again is already
-- in state, and another value, or any raise at a wait that expired, is rejected. A child's latch keeps rewriting,
-- since a restarted child's next landing must reach its parent.
DROP TABLE IF EXISTS temp._raise_settled;
CREATE TEMP TABLE _raise_settled AS
SELECT
    CASE
        WHEN
            c.checkpoint_status_code = 110 /* CheckpointStatusCode.Set */
            AND c.value_format_id = @p_value_format_id
            AND c.value IS @p_value
            THEN 6 /* ControlAction.AlreadyInState */
        ELSE 3 /* ControlAction.Rejected */
    END AS action
FROM {{schema}}.checkpoints c
WHERE
    c.job_id = @p_job_id
    AND c.checkpoint_kind_code = @p_checkpoint_kind_code
    AND c.checkpoint_name = @p_checkpoint_name
    AND @p_checkpoint_kind_code <> 50 /* CheckpointKindCode.Child */
    AND c.checkpoint_status_code IN (110 /* CheckpointStatusCode.Set */, 150 /* CheckpointStatusCode.Expired */);

-- A child's landing names the child as its parent named it, and how it ended; a purged child keeps its id.
DROP TABLE IF EXISTS temp._raise_landing;
CREATE TEMP TABLE _raise_landing AS
SELECT COALESCE(
    @p_event_message,
    'Child '
    || COALESCE((SELECT '''' || c.idempotency_key || '''' FROM {{schema}}.jobs c WHERE c.job_id = @p_child_job_id), 'job ' || @p_child_job_id)
    || ' '
    || CASE @p_child_status
        WHEN 100 /* JobStatusCode.Succeeded */ THEN 'succeeded'
        WHEN 200 /* JobStatusCode.Failed */ THEN 'failed'
        ELSE 'cancelled'
    END || '.'
) AS message;

INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, value_format_id, value, modified_at_utc, version
)
SELECT
    @p_job_id,
    @p_checkpoint_kind_code,
    @p_checkpoint_name,
    110 /* CheckpointStatusCode.Set */,
    @p_value_format_id,
    @p_value,
    {{now}},
    0
WHERE
    EXISTS (
        SELECT 1 FROM {{schema}}.jobs j
        JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
        WHERE
            j.job_id = @p_job_id
            -- A finished job takes no signal, but still a child's latch, which a restart of it reads.
            AND (
                r.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                OR @p_checkpoint_kind_code = 50 /* CheckpointKindCode.Child */
            )
    )
    AND NOT EXISTS (SELECT 1 FROM temp._raise_expired)
    AND NOT EXISTS (SELECT 1 FROM temp._raise_stale)
    AND NOT EXISTS (SELECT 1 FROM temp._raise_settled)
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE
SET
    checkpoint_status_code = 110 /* CheckpointStatusCode.Set */,
    value_format_id = @p_value_format_id,
    value = @p_value,
    modified_at_utc = {{now}},
    version = {{schema}}.checkpoints.version + 1;

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
    23 /* EventCode.JobSignalRaised */,
    {{now}},
    j.namespace_id,
    4 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    CASE
        WHEN EXISTS (SELECT 1 FROM temp._raise_expired)
            THEN substr(COALESCE((SELECT m.message FROM temp._raise_landing m) || ' ', '') || 'Signal not applied: the wait had already expired.', 1, 512)
        ELSE (SELECT m.message FROM temp._raise_landing m)
    END,
    @p_actor,
    -- The raise names what was raised: a signal by its name, a child's latch by the child's ref and how it ended.
    -- That it released the job is the reason of the job.resumed beside it. A signal's name is dotted kebab ASCII.
    1 /* JobPayloadFormat.Json */,
    CASE
        WHEN @p_child_job_id IS NULL THEN CAST('{"signal":"' || @p_checkpoint_name || '"}' AS BLOB)
        ELSE CAST(
            json_patch(
                '{}',
                json_object(
                    'child', (SELECT 'job_' || replace(c.job_ref, '-', '') FROM {{schema}}.jobs c WHERE c.job_id = @p_child_job_id),
                    'status', CASE @p_child_status
                        WHEN 100 /* JobStatusCode.Succeeded */ THEN 'succeeded'
                        WHEN 200 /* JobStatusCode.Failed */ THEN 'failed'
                        ELSE 'cancelled'
                    END
                )
            ) AS BLOB
        )
    END
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND (
        r.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        OR @p_checkpoint_kind_code = 50 /* CheckpointKindCode.Child */
    )
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND NOT EXISTS (SELECT 1 FROM temp._raise_stale)
    AND NOT EXISTS (SELECT 1 FROM temp._raise_settled);

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
    21 /* EventCode.JobResumed */,
    {{now}},
    j.namespace_id,
    4 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    (SELECT m.message FROM temp._raise_landing m),
    @p_actor,
    @p_details_format_id,
    @p_details
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND r.job_status_code = 20 /* JobStatusCode.Suspended */
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND EXISTS (SELECT 1 FROM temp._raise_pending)
    AND NOT EXISTS (SELECT 1 FROM temp._raise_stale);

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = {{now}},
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND job_status_code = 20 /* JobStatusCode.Suspended */
    AND EXISTS (SELECT 1 FROM temp._raise_pending)
    AND NOT EXISTS (SELECT 1 FROM temp._raise_stale);

SELECT
    CASE
        WHEN r.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN EXISTS (SELECT 1 FROM temp._raise_stale) THEN 3 /* ControlAction.Rejected */
        -- A finished job takes no signal, but a raise repeating the value it holds is already in state, whenever it comes.
        WHEN (SELECT s.action FROM temp._raise_settled s) = 6 /* ControlAction.AlreadyInState */ THEN 6 /* ControlAction.AlreadyInState */
        WHEN
            r.job_status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            AND @p_checkpoint_kind_code <> 50 /* CheckpointKindCode.Child */
            THEN 3 /* ControlAction.Rejected */
        WHEN EXISTS (SELECT 1 FROM temp._raise_settled) THEN (SELECT s.action FROM temp._raise_settled s)
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    r.job_status_code AS status
FROM (SELECT @p_job_id AS job_id) probe
LEFT JOIN {{schema}}.jobs_state r ON r.job_id = @p_job_id;
