DROP TABLE IF EXISTS temp._pause_job;

CREATE TEMP TABLE _pause_job AS
SELECT j.id, r.status_code AS from_status, r.version AS from_version
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_id;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    actor_code,
    actor_key,
    job_id,
    job_ref,
    execution_number,
    lineage_root_id,
    definition_id,
    tenant_id,
    worker_id,
    from_status_code,
    to_status_code,
    execution_status_code,
    duration_ms,
    reason_code,
    reason_message)
SELECT
    71 /* EventCode.JobPaused */,
    {{now}},
    j.namespace_id,
    @p_actor_code,
    @p_actor_key,
    j.id,
    j.job_ref,
    r.execution_number,
    COALESCE(j.lineage_root_id, j.id),
    j.definition_id,
    j.tenant_id,
    NULL,
    r.status_code,
    30 /* JobStatusCode.Paused */,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE
    j.id = @p_id
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND (@p_expected_version IS NULL OR r.version = @p_expected_version)
    AND r.status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */);

UPDATE {{schema}}.runtimes
SET
    status_code = 30 /* JobStatusCode.Paused */,
    -- Always a next run, which is what marks this pause as held (ScheduleWalker, "held").
    next_run_at_utc = COALESCE(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_id
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
    AND status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */);

-- A paused running member leaves its lane with no runner, so the lane hands on as a settle would: to an
-- older Blocked member, the one a restart left waiting. This is the last write, so changes() counts it.
UPDATE {{schema}}.runtimes
SET
    status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    status_code = 15 /* JobStatusCode.Blocked */
    AND EXISTS (
        SELECT 1
        FROM temp._pause_job s
        JOIN {{schema}}.runtimes paused ON paused.job_id = s.id
        WHERE
            s.from_status IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
            AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
            AND paused.status_code = 30 /* JobStatusCode.Paused */
            AND paused.lane_id = runtimes.lane_id
    )
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.runtimes o
        WHERE
            o.lane_id = runtimes.lane_id
            AND o.lane_id IS NOT NULL
            AND o.status_code IN (
                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
            )
    )
    AND job_id = (
        SELECT m.job_id
        FROM {{schema}}.runtimes m
        WHERE
            m.lane_id = runtimes.lane_id
            AND m.lane_id IS NOT NULL
            AND m.status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
            )
        ORDER BY m.job_id
        LIMIT 1
    );

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */) THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */) THEN 30 /* JobStatusCode.Paused */
        ELSE s.from_status
    END AS status_code,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */) THEN s.from_version + 1
        ELSE s.from_version
    END AS version,
    CASE WHEN changes() > 0 THEN 1 ELSE 0 END AS lane_promoted
FROM (SELECT @p_id AS qid) q
LEFT JOIN temp._pause_job s ON s.id = q.qid;
