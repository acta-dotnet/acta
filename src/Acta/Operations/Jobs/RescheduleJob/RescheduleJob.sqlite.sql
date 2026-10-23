DROP TABLE IF EXISTS temp._reschedule_job;

CREATE TEMP TABLE _reschedule_job AS
SELECT
    j.job_id,
    r.job_status_code AS from_status,
    r.version AS from_version,
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
    AND r.job_status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */);

UPDATE {{schema}}.jobs_state
SET
    next_run_at_utc = @p_next_run_at_utc,
    job_status_code = (SELECT s.to_status FROM temp._reschedule_job s),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
    AND job_status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */);

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
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */) THEN s.to_status
        ELSE s.from_status
    END AS status,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */) THEN s.from_version + 1
        ELSE s.from_version
    END AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._reschedule_job s ON s.job_id = q.qid;
