DROP TABLE IF EXISTS temp._pause_job;

CREATE TEMP TABLE _pause_job AS
SELECT j.job_id, r.job_status_code AS from_status, r.version AS from_version
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
    71 /* EventCode.JobPaused */,
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
    AND r.job_status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */);

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 30 /* JobStatusCode.Paused */,
    -- Always a next run, which is what marks this pause as held (ScheduleWalker, "held").
    next_run_at_utc = COALESCE(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
    AND job_status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */);

-- A paused running member leaves its lane with no runner, so the lane hands on as a settle would: to an
-- older Blocked member, the one a restart left waiting. This is the last write, so changes() counts it.
UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_status_code = 15 /* JobStatusCode.Blocked */
    AND EXISTS (
        SELECT 1
        FROM temp._pause_job s
        JOIN {{schema}}.jobs_state paused ON paused.job_id = s.job_id
        WHERE
            s.from_status IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
            AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
            AND paused.job_status_code = 30 /* JobStatusCode.Paused */
            AND paused.lane_id = jobs_state.lane_id
    )
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state o
        WHERE
            o.lane_id = jobs_state.lane_id
            AND o.lane_id IS NOT NULL
            AND o.job_status_code IN (
                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                50 /* JobStatusCode.Executing */
            )
    )
    AND job_id = (
        SELECT m.job_id
        FROM {{schema}}.jobs_state m
        WHERE
            m.lane_id = jobs_state.lane_id
            AND m.lane_id IS NOT NULL
            AND m.job_status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
            )
        ORDER BY m.job_id
        LIMIT 1
    );

SELECT
    CASE
        WHEN s.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */) THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */) THEN 30 /* JobStatusCode.Paused */
        ELSE s.from_status
    END AS status,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */) THEN s.from_version + 1
        ELSE s.from_version
    END AS version,
    CASE WHEN changes() > 0 THEN 1 ELSE 0 END AS lane_promoted
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._pause_job s ON s.job_id = q.qid;
