DROP TABLE IF EXISTS temp._resume_job;

CREATE TEMP TABLE _resume_job AS
SELECT
    j.id,
    r.status_code AS from_status,
    r.version AS from_version,
    -- A laned job goes Ready only as its lane's lowest-id unfinished member; behind an older one it
    -- waits Blocked for the promotion. The immediate transaction is the lane's mutex on SQLite.
    CASE
        WHEN
            r.lane_id IS NOT NULL
            AND EXISTS (
                SELECT 1
                FROM {{schema}}.runtimes o
                WHERE
                    o.lane_id = r.lane_id
                    AND o.lane_id IS NOT NULL
                    AND o.status_code IN (
                        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                        30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                    )
                    AND o.job_id < r.job_id
            )
            THEN 15 /* JobStatusCode.Blocked */
        ELSE 10 /* JobStatusCode.Ready */
    END AS to_status
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
    72 /* EventCode.JobResumed */,
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
    30 /* JobStatusCode.Paused */,
    (SELECT s.to_status FROM temp._resume_job s),
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
    AND r.status_code = 30 /* JobStatusCode.Paused */;

UPDATE {{schema}}.runtimes
SET
    status_code = (SELECT s.to_status FROM temp._resume_job s),
    next_run_at_utc = COALESCE(@p_next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_id
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
    AND status_code = 30 /* JobStatusCode.Paused */;

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.from_status = 30 /* JobStatusCode.Paused */ THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN s.from_status = 30 /* JobStatusCode.Paused */ THEN s.to_status
        ELSE s.from_status
    END AS status_code,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status = 30 /* JobStatusCode.Paused */ THEN s.from_version + 1
        ELSE s.from_version
    END AS version
FROM (SELECT @p_id AS qid) q
LEFT JOIN temp._resume_job s ON s.id = q.qid;
