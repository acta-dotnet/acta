DROP TABLE IF EXISTS temp._reschedule_job;

CREATE TEMP TABLE _reschedule_job AS
SELECT
    j.job_id,
    r.job_status_code AS from_status,
    r.version AS from_version,
    -- An Idle or Paused permanent slot with no firing outstanding takes a manual firing at the instant; a slot
    -- mid-firing moves its instant like any waiting job. Idle applies to a slot alone.
    CASE
        WHEN
            j.job_kind_code = 20 /* JobKindCode.PermanentSlot */
            AND r.job_status_code IN (40 /* JobStatusCode.Idle */, 30 /* JobStatusCode.Paused */)
            AND NOT EXISTS (
                SELECT 1
                FROM {{schema}}.checkpoints cp
                WHERE cp.job_id = j.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                    AND cp.checkpoint_name = 'sys.firing' AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
            )
            THEN 1
        ELSE 0
    END AS fires,
    CASE
        WHEN r.job_status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */) THEN 1
        WHEN r.job_status_code = 40 /* JobStatusCode.Idle */ AND j.job_kind_code = 20 /* JobKindCode.PermanentSlot */ THEN 1
        ELSE 0
    END AS applies,
    -- A laned job is rescheduled Ready unless another member runs, or it was not running and an older
    -- member is unfinished; a Blocked job's new instant applies once it is promoted. The immediate
    -- transaction is the lane's mutex on SQLite.
    CASE
        WHEN
            r.lane_id IS NOT NULL
            AND EXISTS (
                SELECT 1
                FROM {{schema}}.jobs_state o
                WHERE
                    o.lane_id = r.lane_id
                    AND o.lane_id IS NOT NULL
                    AND o.job_status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                    )
                    AND o.job_id <> r.job_id
                    AND (
                        o.job_status_code IN (
                            10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                            50 /* JobStatusCode.Executing */
                        )
                        OR (
                            r.job_status_code NOT IN (
                                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */
                            )
                            AND o.job_id < r.job_id
                        )
                    )
            )
            THEN 15 /* JobStatusCode.Blocked */
        ELSE 10 /* JobStatusCode.Ready */
    END AS to_status
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id;

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
    details)
SELECT
    61 /* EventCode.JobRescheduled */,
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
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND (@p_expected_version IS NULL OR r.version = @p_expected_version)
    AND EXISTS (SELECT 1 FROM temp._reschedule_job s WHERE s.applies = 1);

-- A null instant is now; a manual firing starts with a fresh failure budget.
UPDATE {{schema}}.jobs_state
SET
    next_run_at_utc = COALESCE(@p_next_run_at_utc, {{now}}),
    job_status_code = (SELECT s.to_status FROM temp._reschedule_job s),
    failure_count = CASE WHEN (SELECT s.fires FROM temp._reschedule_job s) = 1 THEN 0 ELSE failure_count END,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
    AND (SELECT s.applies FROM temp._reschedule_job s) = 1;

-- The manual envelope replaces the settled one of the slot's previous firing.
INSERT INTO {{schema}}.checkpoints (
    job_id, checkpoint_kind_code, checkpoint_name, checkpoint_status_code,
    value_format_id, value, created_at_utc, modified_at_utc, version)
SELECT
    @p_job_id, 60 /* CheckpointKindCode.Firing */, 'sys.firing', 10 /* CheckpointStatusCode.Pending */,
    1 /* JobPayloadFormat.Json */, CAST(@p_envelope AS BLOB), {{now}}, {{now}}, 0
WHERE EXISTS (
    SELECT 1
    FROM temp._reschedule_job s
    WHERE s.fires = 1 AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
)
ON CONFLICT (job_id, checkpoint_kind_code, checkpoint_name) DO UPDATE SET
    checkpoint_status_code = excluded.checkpoint_status_code,
    value_format_id = excluded.value_format_id,
    value = excluded.value,
    modified_at_utc = {{now}},
    version = version + 1;

SELECT
    CASE
        WHEN s.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.applies = 1 THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN s.applies = 1 THEN s.to_status
        ELSE s.from_status
    END AS status,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.applies = 1 THEN s.from_version + 1
        ELSE s.from_version
    END AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._reschedule_job s ON s.job_id = q.qid;
