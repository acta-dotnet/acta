DROP TABLE IF EXISTS temp._restart_job;

-- A finished laned job is never reopened in place, which would put it back ahead of members that
-- already ran after it; the caller redrives it as a new job at the lane's tail instead. A laned job
-- restarts Ready only as its lane's lowest-id unfinished member and Blocked otherwise. The immediate
-- transaction is the lane's mutex on SQLite.
CREATE TEMP TABLE _restart_job AS
SELECT
    j.id,
    r.status_code AS from_status,
    r.version AS from_version,
    CASE
        WHEN r.status_code = 50 /* JobStatusCode.Executing */ THEN 1
        WHEN
            r.lane_id IS NOT NULL
            AND r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 1
        ELSE 0
    END AS rejected,
    CASE
        WHEN
            r.lane_id IS NOT NULL
            AND EXISTS (
                SELECT 1
                FROM {{schema}}.runtimes o
                WHERE
                    o.lane_id = r.lane_id
                    AND o.lane_id IS NOT NULL
                    AND o.status_code IN (10, 15, 20, 30, 40, 50)
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
    73 /* EventCode.JobRestarted */,
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
    s.to_status,
    NULL,
    NULL,
    @p_reason_code,
    @p_reason_message
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
JOIN temp._restart_job s ON s.id = j.id
WHERE
    j.id = @p_id
    AND j.audit_level_code = 20 /* JobAuditLevelCode.Audit */
    AND (@p_expected_version IS NULL OR r.version = @p_expected_version)
    AND s.rejected = 0;

UPDATE {{schema}}.runtimes
SET
    status_code = (SELECT s.to_status FROM temp._restart_job s),
    failure_count = 0,
    next_run_at_utc = COALESCE(@p_next_run_at_utc, {{now}}),
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = NULL,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_id
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
    AND (SELECT s.rejected FROM temp._restart_job s) = 0;

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.rejected = 1 THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN s.rejected = 1 THEN s.from_status
        ELSE s.to_status
    END AS status_code,
    CASE
        WHEN s.id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.rejected = 1 THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT @p_id AS qid) q
LEFT JOIN temp._restart_job s ON s.id = q.qid;
