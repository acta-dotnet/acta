DROP TABLE IF EXISTS temp._restart_job;

-- A laned job restarts Ready unless another member runs, or it was not running and an older member is
-- unfinished (docs/sql-execution-policy.md, "Lane lock order").
CREATE TEMP TABLE _restart_job AS
SELECT
    j.job_id,
    r.job_status_code AS from_status,
    r.version AS from_version,
    CASE WHEN r.job_status_code = 50 /* JobStatusCode.Executing */ THEN 1 ELSE 0 END AS rejected,
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

-- A finished laned job is not reactivated while an unfinished ancestor or descendant sits in its lane, the
-- state the enqueue ancestor guard refuses (docs/sql-execution-policy.md, "Lane lock order").
UPDATE temp._restart_job
SET rejected = 1
WHERE
    from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND EXISTS (
        WITH RECURSIVE
            ancestors (job_id, parent_job_id) AS (
                SELECT a.job_id, a.parent_job_id
                FROM {{schema}}.jobs a
                WHERE a.job_id = (SELECT j.parent_job_id FROM {{schema}}.jobs j WHERE j.job_id = @p_job_id)
                UNION ALL
                SELECT a.job_id, a.parent_job_id
                FROM {{schema}}.jobs a
                JOIN ancestors c ON a.job_id = c.parent_job_id
            ),
            descendants (job_id) AS (
                SELECT d.job_id
                FROM {{schema}}.jobs d
                WHERE d.parent_job_id = @p_job_id
                UNION ALL
                SELECT d.job_id
                FROM {{schema}}.jobs d
                JOIN descendants c ON d.parent_job_id = c.job_id
            ),
            lineage (job_id) AS (
                SELECT a.job_id FROM ancestors a
                UNION ALL
                SELECT d.job_id FROM descendants d
            )
        SELECT 1
        FROM lineage c
        JOIN {{schema}}.jobs_state lr ON lr.job_id = c.job_id
        JOIN {{schema}}.jobs_state r ON r.job_id = @p_job_id
        WHERE
            lr.lane_id = r.lane_id
            AND lr.job_status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
            )
    );

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
    73 /* EventCode.JobRestarted */,
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
JOIN temp._restart_job s ON s.job_id = j.job_id
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND (@p_expected_version IS NULL OR r.version = @p_expected_version)
    AND s.rejected = 0;

-- An exhausted step runs again from a fresh budget and retry window; an interrupted at-most-once
-- step stays, since only someone who reconciled its outcome can say it may run again.
DELETE FROM {{schema}}.steps
WHERE
    job_id = @p_job_id
    AND step_status_code = 200 /* StepStatusCode.Exhausted */
    AND (SELECT s.rejected FROM temp._restart_job s) = 0
    AND (@p_expected_version IS NULL OR (SELECT s.from_version FROM temp._restart_job s) = @p_expected_version);

UPDATE {{schema}}.jobs_state
SET
    job_status_code = (SELECT s.to_status FROM temp._restart_job s),
    failure_count = 0,
    next_run_at_utc = COALESCE(@p_next_run_at_utc, {{now}}),
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = NULL,
    result_format_id = 0 /* JobPayloadFormat.None */,
    result = NULL,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
    AND (SELECT s.rejected FROM temp._restart_job s) = 0;

SELECT
    CASE
        WHEN s.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.rejected = 1 THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END AS action,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN s.rejected = 1 THEN s.from_status
        ELSE s.to_status
    END AS status,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.rejected = 1 THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._restart_job s ON s.job_id = q.qid;
