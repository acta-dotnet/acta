DROP TABLE IF EXISTS temp._resume_job;

CREATE TEMP TABLE _resume_job AS
SELECT
    j.job_id,
    r.job_status_code AS from_status,
    r.version AS from_version,
    -- A slot with no firing outstanding resumes Idle, for admission to find; one paused mid-firing resumes Ready to
    -- run it. A laned job resumes Ready unless another member runs or an older member is unfinished
    -- (docs/sql-internals.md, "Lane lock order"). The immediate transaction is the lane's mutex on SQLite.
    CASE
        WHEN j.job_kind_code = 20 /* JobKindCode.PermanentSlot */ AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.checkpoints cp
            WHERE cp.job_id = j.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                AND cp.checkpoint_name = 'sys.firing' AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
        ) THEN 40 /* JobStatusCode.Idle */
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
                        OR o.job_id < r.job_id
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
    72 /* EventCode.JobResumed */,
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
    AND r.job_status_code = 30 /* JobStatusCode.Paused */;

UPDATE {{schema}}.jobs_state
SET
    job_status_code = (SELECT s.to_status FROM temp._resume_job s),
    next_run_at_utc = CASE
        WHEN (SELECT s.to_status FROM temp._resume_job s) = 40 /* JobStatusCode.Idle */ THEN NULL
        ELSE COALESCE(@p_next_run_at_utc, {{now}})
    END,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
    AND job_status_code = 30 /* JobStatusCode.Paused */;

-- The members parked on the slot evaluate again now that it is Idle.
UPDATE {{schema}}.schedules_state
SET
    next_evaluation_at_utc = {{now}},
    evaluation_status_code = 10 /* EvaluationStatusCode.Scheduled */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    schedule_id IN (SELECT js.schedule_id FROM {{schema}}.schedules js WHERE js.job_id = @p_job_id)
    AND evaluation_status_code IN (20 /* EvaluationStatusCode.WaitingForCapacity */, 50 /* EvaluationStatusCode.SlotUnavailable */)
    AND EXISTS (
        SELECT 1
        FROM temp._resume_job s
        WHERE
            s.to_status = 40 /* JobStatusCode.Idle */
            AND s.from_status = 30 /* JobStatusCode.Paused */
            AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    );

SELECT
    CASE
        WHEN s.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.from_status = 30 /* JobStatusCode.Paused */ THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN s.from_status = 30 /* JobStatusCode.Paused */ THEN s.to_status
        ELSE s.from_status
    END AS status,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status = 30 /* JobStatusCode.Paused */ THEN s.from_version + 1
        ELSE s.from_version
    END AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._resume_job s ON s.job_id = q.qid;
