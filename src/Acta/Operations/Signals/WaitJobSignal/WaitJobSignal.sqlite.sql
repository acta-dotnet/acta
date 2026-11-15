/* Arbiter for one durable wait, ordered so the final SELECT reads a settled row: flip an overdue
   Pending to Expired first, then arm. */
/* Arming is one-directional. A NULL due_at_utc is armed when the caller carries a bound (a length, or an instant: a
   group's stored deadline), so code redeployed with a bound can un-strand a wait suspended without one; a stored due
   is never overwritten, never extended, and never cleared by a subsequent unbounded call. */
/* Nothing here reads a captured pre-state, so a first arrival that loses the insert needs no re-read
   the way pg's routine does: the arming update below applies to whatever row now stands, and the
   final SELECT reports it settled. */
/* A wait this call armed with a length suspends once: the length is positive and the flip runs before
   the arm. A checkpoint this call arms at or past an instant is armed Expired, so a child landing after it
   cannot revive the wait, and the wait times out now. Pg and mssql state the same rules. */
-- The wait changes the checkpoint only while this attempt holds the job's row (docs/sql-internals.md, "Execution fence").
SELECT ACTA_ERROR('ACTA:EXECUTION_FENCED:wait_job_signal: the job is no longer executing this attempt.')
WHERE NOT EXISTS (
    SELECT 1
    FROM {{schema}}.jobs_state r
    WHERE
        r.job_id = @p_job_id
        AND r.job_status_code = 50 /* JobStatusCode.Executing */
        AND r.execution_number = @p_execution_number
        AND r.leased_by_worker_id = @p_leased_by_worker_id
);

-- The state before this call changes anything, so an expiry it makes is told from one it replays.
DROP TABLE IF EXISTS temp._wait_before;
CREATE TEMP TABLE _wait_before AS
SELECT c.checkpoint_status_code AS status
FROM {{schema}}.checkpoints c
WHERE c.job_id = @p_job_id AND c.checkpoint_kind_code = @p_checkpoint_kind_code AND c.checkpoint_name = @p_checkpoint_name;

UPDATE {{schema}}.checkpoints
SET
    checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id AND checkpoint_kind_code = @p_checkpoint_kind_code AND checkpoint_name = @p_checkpoint_name
    AND checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
    AND due_at_utc IS NOT NULL
    AND due_at_utc <= {{now}};

INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code, due_at_utc,
    value_format_id, value, modified_at_utc, version
)
VALUES (
    @p_job_id, @p_checkpoint_kind_code, @p_checkpoint_name,
    CASE
        WHEN COALESCE(@p_due_at_utc, {{now}} + (@p_timeout_seconds) * 1000) <= {{now}} THEN 30 /* CheckpointStatusCode.Expired */
        ELSE 10 /* CheckpointStatusCode.Pending */
    END,
    COALESCE(@p_due_at_utc, {{now}} + (@p_timeout_seconds) * 1000),
    0 /* JobPayloadFormat.None */,
    NULL, {{now}}, 0
)
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO NOTHING;

UPDATE {{schema}}.checkpoints
SET
    checkpoint_status_code = CASE
        WHEN COALESCE(@p_due_at_utc, {{now}} + (@p_timeout_seconds) * 1000) <= {{now}} THEN 30 /* CheckpointStatusCode.Expired */
        ELSE 10 /* CheckpointStatusCode.Pending */
    END,
    due_at_utc = COALESCE(@p_due_at_utc, {{now}} + (@p_timeout_seconds) * 1000),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id AND checkpoint_kind_code = @p_checkpoint_kind_code AND checkpoint_name = @p_checkpoint_name
    AND checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
    AND due_at_utc IS NULL
    AND COALESCE(@p_due_at_utc, @p_timeout_seconds) IS NOT NULL;

-- An expiry this call made reaches the timeline, a child named as its parent named it; a replayed one does not.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, entity_code, entity_id, definition_id, tenant_id, lineage_root_job_id,
    execution_number, event_message, details_format_id, details
)
SELECT
    83 /* EventCode.JobWaitExpired */,
    {{now}},
    j.namespace_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    @p_execution_number,
    'The wait for '
    || CASE
        WHEN @p_checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
            THEN 'child ' || COALESCE('''' || ch.deduplication_key || '''', 'job ' || substr(@p_checkpoint_name, 11))
        ELSE 'signal ''' || @p_checkpoint_name || ''''
    END || ' expired.',
    1 /* JobPayloadFormat.Json */,
    CAST(
        json_object(
            'kind', CASE WHEN @p_checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */ THEN 'child-latch' ELSE 'signal' END,
            'name', @p_checkpoint_name,
            'dueAtUtc', strftime('%Y-%m-%dT%H:%M:%fZ', c.due_at_utc / 1000.0, 'unixepoch')
        ) AS BLOB
    )
FROM {{schema}}.checkpoints c
JOIN {{schema}}.jobs j ON j.job_id = c.job_id
LEFT JOIN {{schema}}.jobs ch
    ON
        @p_checkpoint_kind_code = 50 /* CheckpointKindCode.ChildLatch */
        AND ch.parent_job_id = j.job_id
        AND 'sys.child.' || ch.job_id = @p_checkpoint_name
WHERE
    c.job_id = @p_job_id AND c.checkpoint_kind_code = @p_checkpoint_kind_code AND c.checkpoint_name = @p_checkpoint_name
    AND c.checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */
    AND NOT EXISTS (SELECT 1 FROM temp._wait_before b WHERE b.status = 30 /* CheckpointStatusCode.Expired */)
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */;

SELECT
    CASE
        WHEN js.checkpoint_status_code = 20 /* CheckpointStatusCode.Set */
            THEN 2 /* WaitJobSignalOutcomeCode.ContinueSet */
        WHEN js.checkpoint_status_code = 30 /* CheckpointStatusCode.Expired */
            THEN 3 /* WaitJobSignalOutcomeCode.TimedOut */
        ELSE 1 /* WaitJobSignalOutcomeCode.SuspendPending */
    END AS outcome_code,
    CASE
        WHEN js.checkpoint_status_code = 20 /* CheckpointStatusCode.Set */ THEN js.value_format_id
        ELSE 0 /* JobPayloadFormat.None */
    END AS value_format_id,
    CASE
        WHEN js.checkpoint_status_code = 20 /* CheckpointStatusCode.Set */ THEN js.value
        ELSE NULL
    END AS value
FROM {{schema}}.checkpoints js
WHERE js.job_id = @p_job_id AND js.checkpoint_kind_code = @p_checkpoint_kind_code AND js.checkpoint_name = @p_checkpoint_name;
