-- One batch, one implicit transaction; only the final SELECT returns rows. Lock order: the lane, then the
-- job's rows (docs/sql-internals.md, "Lane lock order"); lane_id never changes, so the
-- unlocked read that finds it is safe.
DROP TABLE IF EXISTS pg_temp._resume_job_lane;
DROP TABLE IF EXISTS pg_temp._resume_job;
CREATE TEMP TABLE _resume_job_lane (lane_id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _resume_job (
    from_status SMALLINT NOT NULL,
    from_version INT NOT NULL,
    lane_id BIGINT,
    namespace_id INT NOT NULL,
    lineage_root_job_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    event_recording_code SMALLINT NOT NULL,
    to_status SMALLINT NOT NULL
) ON COMMIT DROP;

INSERT INTO _resume_job_lane (lane_id)
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE l.lane_id = (SELECT r.lane_id FROM {{schema}}.jobs_state r WHERE r.job_id = @p_job_id)
FOR UPDATE;

INSERT INTO _resume_job (
    from_status, from_version, lane_id, namespace_id, lineage_root_job_id, definition_id, tenant_id, execution_number,
    event_recording_code, to_status)
SELECT
    r.job_status_code, r.version, r.lane_id, j.namespace_id, j.lineage_root_job_id, j.definition_id, j.tenant_id, r.execution_number,
    j.event_recording_code,
    -- A slot with no firing outstanding resumes Idle, for admission to find; one paused mid-firing resumes Ready to
    -- run it.
    CASE
        WHEN j.job_kind_code = 20 /* JobKindCode.PermanentSlot */ AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.checkpoints cp
            WHERE cp.job_id = j.job_id AND cp.checkpoint_kind_code = 60 /* CheckpointKindCode.Firing */
                AND cp.checkpoint_name = 'sys.firing' AND cp.checkpoint_status_code = 10 /* CheckpointStatusCode.Pending */
        ) THEN 40 /* JobStatusCode.Idle */
        ELSE 10 /* JobStatusCode.Ready */
    END
FROM {{schema}}.jobs_state r
JOIN {{schema}}.jobs j ON j.job_id = r.job_id
WHERE r.job_id = @p_job_id
FOR UPDATE OF r;

-- A laned job resumes Blocked while another member runs or an older member is unfinished.
UPDATE _resume_job s
SET to_status = 15 /* JobStatusCode.Blocked */
WHERE
    s.lane_id IS NOT NULL
    AND EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state o
        WHERE
            o.lane_id = s.lane_id
            AND o.lane_id IS NOT NULL
            AND o.job_status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
            )
            AND o.job_id <> @p_job_id
            AND (
                o.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                    50 /* JobStatusCode.Executing */
                )
                OR o.job_id < @p_job_id
            )
    );

UPDATE {{schema}}.jobs_state AS r
SET
    job_status_code = s.to_status,
    next_run_at_utc = CASE WHEN s.to_status = 40 /* JobStatusCode.Idle */ THEN NULL ELSE COALESCE(@p_next_run_at_utc, now()) END,
    modified_at_utc = now(),
    version = r.version + 1
FROM _resume_job s
WHERE
    r.job_id = @p_job_id
    AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    AND s.from_status = 30 /* JobStatusCode.Paused */;

-- The members parked on the slot evaluate again now that it is Idle.
UPDATE {{schema}}.schedules_state st
SET
    next_evaluation_at_utc = now(),
    evaluation_status_code = 10 /* EvaluationStatusCode.Scheduled */,
    modified_at_utc = now(),
    version = st.version + 1
FROM {{schema}}.schedules js
JOIN _resume_job s ON true
WHERE
    st.schedule_id = js.schedule_id
    AND js.job_id = @p_job_id
    AND s.to_status = 40 /* JobStatusCode.Idle */
    AND s.from_status = 30 /* JobStatusCode.Paused */
    AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    AND st.evaluation_status_code IN (20 /* EvaluationStatusCode.WaitingForCapacity */, 50 /* EvaluationStatusCode.SlotUnavailable */);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    72 /* EventCode.JobResumed */, now(), s.namespace_id,
    50 /* EntityCode.Job */, @p_job_id,
    s.definition_id, s.tenant_id, COALESCE(s.lineage_root_job_id, @p_job_id),
    s.execution_number,
    @p_event_message, @p_details_format_id, @p_details
FROM _resume_job s
WHERE
    s.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    AND s.from_status = 30 /* JobStatusCode.Paused */;

SELECT
    CASE
        WHEN s.from_status IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.from_status <> 30 /* JobStatusCode.Paused */ THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    CASE
        WHEN s.from_status IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN s.from_status <> 30 /* JobStatusCode.Paused */ THEN s.from_status
        ELSE s.to_status
    END AS status,
    CASE
        WHEN s.from_status IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status <> 30 /* JobStatusCode.Paused */ THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN _resume_job s ON true;
