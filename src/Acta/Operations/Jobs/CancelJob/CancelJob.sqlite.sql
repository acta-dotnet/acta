-- A cancel an ancestor's expired child wait makes lands only while that attempt holds the ancestor's row
-- (docs/sql-internals.md, "Execution fence"); an operator's or the engine's cancel carries no fence.
SELECT ACTA_ERROR('ACTA:EXECUTION_FENCED:cancel_job: the awaiting job is no longer executing this attempt.')
WHERE
    @p_fence_job_id IS NOT NULL
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state fr
        WHERE
            fr.job_id = @p_fence_job_id
            AND fr.job_status_code = 50 /* JobStatusCode.Executing */
            AND fr.execution_number = @p_fence_execution_number
            AND fr.leased_by_worker_id = @p_fence_worker_id
    );

DROP TABLE IF EXISTS temp._cancel_job;

CREATE TEMP TABLE _cancel_job AS
SELECT j.job_id, r.job_status_code AS from_status, j.parent_job_id, r.version AS from_version
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id;

-- An executing job's attempt yields first, then the job is cancelled. The attempt's details lead with its outcome,
-- then the cancel's own: the writer's payload is a compact JSON object, so its members follow the outcome once its
-- opening brace is dropped.
INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    worker_id,
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
    43 /* EventCode.JobExecutionYielded */,
    {{now}},
    j.namespace_id,
    r.leased_by_worker_id,
    50 /* EntityCode.Job */,
    j.job_id,
    j.definition_id,
    j.tenant_id,
    COALESCE(j.lineage_root_job_id, j.job_id),
    r.execution_number,
    @p_event_message,
    1 /* JobPayloadFormat.Json */,
    CAST(CASE
        WHEN @p_details IS NULL THEN '{"outcome":"cancelled"}'
        ELSE '{"outcome":"cancelled",' || substr(@p_details, 2)
    END AS BLOB)
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND (@p_expected_version IS NULL OR r.version = @p_expected_version)
    AND r.job_status_code = 50 /* JobStatusCode.Executing */;

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
    70 /* EventCode.JobCancelled */,
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
    AND r.job_status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 50 /* JobStatusCode.Executing */);

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 220 /* JobStatusCode.Cancelled */,
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = {{now}} + (
        SELECT jd.retention_seconds_effective
        FROM {{schema}}.definitions jd
        JOIN {{schema}}.jobs j2 ON j2.job_id = jobs_state.job_id
        WHERE jd.definition_id = j2.definition_id
    ) * 1000,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
    AND job_status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 50 /* JobStatusCode.Executing */);

-- A cancelled head hands its lane to the next member; a cancelled Blocked follower leaves the head where
-- it is, which the promotion reads for itself. A restarted member can wait Blocked below a running one,
-- so it promotes only while no member runs. The immediate transaction is the lane's mutex on SQLite.
UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_status_code = 15 /* JobStatusCode.Blocked */
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
        JOIN {{schema}}.jobs_state cancelled ON cancelled.lane_id = m.lane_id
        WHERE
            cancelled.job_id = @p_job_id
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
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 50 /* JobStatusCode.Executing */) THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 50 /* JobStatusCode.Executing */) THEN 220 /* JobStatusCode.Cancelled */
        ELSE s.from_status
    END AS status,
    s.parent_job_id,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 50 /* JobStatusCode.Executing */) THEN s.from_version + 1
        ELSE s.from_version
    END AS version,
    CASE WHEN changes() > 0 THEN 1 ELSE 0 END AS lane_promoted
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._cancel_job s ON s.job_id = q.qid;
