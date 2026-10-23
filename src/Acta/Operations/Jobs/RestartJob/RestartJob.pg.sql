-- One batch, one implicit transaction; only the final SELECT returns rows. Lock order: the lane, then the
-- job's rows (docs/sql-internals.md, "Lane lock order"); lane_id never changes, so the
-- unlocked read that finds it is safe.
DROP TABLE IF EXISTS pg_temp._restart_job_lane;
DROP TABLE IF EXISTS pg_temp._restart_job;
CREATE TEMP TABLE _restart_job_lane (lane_id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _restart_job (
    from_status SMALLINT NOT NULL,
    from_version INT NOT NULL,
    lane_id BIGINT,
    parent_job_id BIGINT,
    namespace_id INT NOT NULL,
    lineage_root_job_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    event_recording_code SMALLINT NOT NULL,
    action SMALLINT NOT NULL,
    to_status SMALLINT NOT NULL
) ON COMMIT DROP;

INSERT INTO _restart_job_lane (lane_id)
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE l.lane_id = (SELECT r.lane_id FROM {{schema}}.jobs_state r WHERE r.job_id = @p_job_id)
FOR UPDATE;

INSERT INTO _restart_job (
    from_status, from_version, lane_id, parent_job_id, namespace_id, lineage_root_job_id, definition_id, tenant_id,
    execution_number, event_recording_code, action, to_status)
SELECT
    r.job_status_code, r.version, r.lane_id, j.parent_job_id, j.namespace_id, j.lineage_root_job_id, j.definition_id, j.tenant_id,
    r.execution_number, j.event_recording_code,
    CASE
        WHEN @p_expected_version IS NOT NULL AND r.version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN r.job_status_code = 50 /* JobStatusCode.Executing */ THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END,
    10 /* JobStatusCode.Ready */
FROM {{schema}}.jobs_state r
JOIN {{schema}}.jobs j ON j.job_id = r.job_id
WHERE r.job_id = @p_job_id
FOR UPDATE OF r;

-- A finished laned job is not reactivated while an unfinished ancestor or descendant sits in its lane, the
-- state the enqueue ancestor guard refuses.
UPDATE _restart_job s
SET action = 3 /* ControlAction.Rejected */
WHERE
    s.action = 1 /* ControlAction.Applied */
    AND s.lane_id IS NOT NULL
    AND s.from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
    AND EXISTS (
        WITH RECURSIVE
        ancestors AS (
            SELECT a.job_id, a.parent_job_id
            FROM {{schema}}.jobs a
            WHERE a.job_id = (SELECT x.parent_job_id FROM _restart_job x)
            UNION ALL
            SELECT a.job_id, a.parent_job_id
            FROM {{schema}}.jobs a
            INNER JOIN ancestors c ON a.job_id = c.parent_job_id
        ),
        descendants AS (
            SELECT d.job_id
            FROM {{schema}}.jobs d
            WHERE d.parent_job_id = @p_job_id
            UNION ALL
            SELECT d.job_id
            FROM {{schema}}.jobs d
            INNER JOIN descendants c ON d.parent_job_id = c.job_id
        ),
        lineage AS (
            SELECT a.job_id FROM ancestors a
            UNION ALL
            SELECT d.job_id FROM descendants d
        )
        SELECT 1
        FROM lineage c
        INNER JOIN {{schema}}.jobs_state lr ON lr.job_id = c.job_id
        WHERE
            lr.lane_id = (SELECT x.lane_id FROM _restart_job x)
            AND lr.job_status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
            )
    );

-- A laned job restarts Blocked while another member runs, or when it was not running and an older member is
-- unfinished.
UPDATE _restart_job s
SET to_status = 15 /* JobStatusCode.Blocked */
WHERE
    s.action = 1 /* ControlAction.Applied */
    AND s.lane_id IS NOT NULL
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
                OR (
                    s.from_status NOT IN (10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */)
                    AND o.job_id < @p_job_id
                )
            )
    );

UPDATE {{schema}}.jobs_state AS r
SET
    job_status_code = s.to_status,
    failure_count = 0,
    next_run_at_utc = COALESCE(@p_next_run_at_utc, now()),
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = NULL,
    result_format_id = 0 /* JobPayloadFormat.None */,
    result = NULL,
    modified_at_utc = now(),
    version = r.version + 1
FROM _restart_job s
WHERE r.job_id = @p_job_id AND s.action = 1 /* ControlAction.Applied */;

-- An exhausted step runs again from a fresh budget and retry window; an interrupted at-most-once step stays,
-- since only someone who reconciled its outcome can say it may run again.
DELETE FROM {{schema}}.steps st
WHERE
    st.job_id = @p_job_id
    AND st.step_status_code = 200 /* StepStatusCode.Exhausted */
    AND EXISTS (SELECT 1 FROM _restart_job s WHERE s.action = 1 /* ControlAction.Applied */);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    73 /* EventCode.JobRestarted */, now(), s.namespace_id,
    50 /* EntityCode.Job */, @p_job_id,
    s.definition_id, s.tenant_id, COALESCE(s.lineage_root_job_id, @p_job_id),
    s.execution_number,
    @p_event_message, @p_details_format_id, @p_details
FROM _restart_job s
WHERE s.action = 1 /* ControlAction.Applied */ AND s.event_recording_code = 20 /* EventRecordingCode.Audit */;

SELECT
    COALESCE(s.action, 2 /* ControlAction.NotFound */)::SMALLINT AS action,
    CASE WHEN s.action = 1 /* ControlAction.Applied */ THEN s.to_status ELSE s.from_status END AS status,
    CASE WHEN s.action = 1 /* ControlAction.Applied */ THEN s.from_version + 1 ELSE s.from_version END AS version
FROM (SELECT 1) one
LEFT JOIN _restart_job s ON true;
