-- One batch, one implicit transaction; only the final SELECT returns rows. Lock order: the lane, then the
-- job's rows (docs/sql-internals.md, "Lane lock order"); lane_id never changes, so the
-- unlocked read that finds it is safe.
DROP TABLE IF EXISTS pg_temp._cancel_job_lane;
DROP TABLE IF EXISTS pg_temp._cancel_job;
DROP TABLE IF EXISTS pg_temp._cancel_job_promoted;
DROP TABLE IF EXISTS pg_temp._cancel_job_fence;
CREATE TEMP TABLE _cancel_job_lane (lane_id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _cancel_job (
    from_status SMALLINT NOT NULL,
    from_version INT NOT NULL,
    lane_id BIGINT,
    parent_job_id BIGINT,
    namespace_id INT NOT NULL,
    lineage_root_job_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    worker_id INT,
    event_recording_code SMALLINT NOT NULL,
    applied BOOLEAN NOT NULL
) ON COMMIT DROP;
CREATE TEMP TABLE _cancel_job_promoted (job_id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _cancel_job_fence (held BOOLEAN NOT NULL) ON COMMIT DROP;

INSERT INTO _cancel_job_lane (lane_id)
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE l.lane_id = (SELECT r.lane_id FROM {{schema}}.jobs_state r WHERE r.job_id = @p_job_id)
FOR UPDATE;

INSERT INTO _cancel_job (
    from_status, from_version, lane_id, parent_job_id, namespace_id, lineage_root_job_id, definition_id, tenant_id,
    execution_number, worker_id, event_recording_code, applied)
SELECT
    r.job_status_code, r.version, r.lane_id, j.parent_job_id, j.namespace_id, j.lineage_root_job_id, j.definition_id, j.tenant_id,
    r.execution_number, r.leased_by_worker_id, j.event_recording_code,
    (@p_expected_version IS NULL OR r.version = @p_expected_version)
    AND r.job_status_code IN (
        30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */,
        15 /* JobStatusCode.Blocked */, 50 /* JobStatusCode.Executing */
    )
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id
FOR UPDATE;

-- A cancel an ancestor's expired child wait makes lands only while that attempt holds the ancestor's row, read
-- unlocked once this job's rows are locked (docs/sql-internals.md, "Execution fence"). An operator's or the engine's
-- cancel carries no fence; the anonymous block only raises.
INSERT INTO _cancel_job_fence (held)
SELECT EXISTS (
    SELECT 1
    FROM {{schema}}.jobs_state fr
    WHERE
        fr.job_id = @p_fence_job_id
        AND fr.job_status_code = 50 /* JobStatusCode.Executing */
        AND fr.execution_number = @p_fence_execution_number
        AND fr.leased_by_worker_id = @p_fence_worker_id
)
WHERE @p_fence_job_id IS NOT NULL;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_temp._cancel_job_fence WHERE NOT held) THEN
        RAISE EXCEPTION 'ACTA:EXECUTION_FENCED:cancel_job: the awaiting job is no longer executing this attempt.' USING ERRCODE = 'P0001';
    END IF;
END
$$;

UPDATE {{schema}}.jobs_state AS r
SET
    job_status_code = 220 /* JobStatusCode.Cancelled */,
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = now() + make_interval(secs => jd.retention_seconds_effective),
    modified_at_utc = now(),
    version = r.version + 1
FROM _cancel_job s
JOIN {{schema}}.definitions jd ON jd.definition_id = s.definition_id
WHERE r.job_id = @p_job_id AND s.applied;

-- A cancelled head hands its lane on; a cancelled Blocked follower leaves the head where it is. A restarted
-- member can wait Blocked below a running one, so the oldest unfinished member is promoted only while no
-- member runs.
WITH promoted AS (
    UPDATE {{schema}}.jobs_state pr
    SET
        job_status_code = 10 /* JobStatusCode.Ready */,
        next_run_at_utc = GREATEST(pr.next_run_at_utc, now()),
        modified_at_utc = now(),
        version = pr.version + 1
    FROM _cancel_job s
    WHERE
        s.applied
        AND s.lane_id IS NOT NULL
        AND pr.job_status_code = 15 /* JobStatusCode.Blocked */
        AND pr.job_id = (
            SELECT m.job_id
            FROM {{schema}}.jobs_state m
            WHERE
                m.lane_id = s.lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1
        )
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.jobs_state o
            WHERE
                o.lane_id = s.lane_id
                AND o.lane_id IS NOT NULL
                AND o.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                    50 /* JobStatusCode.Executing */
                )
        )
    RETURNING pr.job_id
)
INSERT INTO _cancel_job_promoted (job_id)
SELECT p.job_id FROM promoted p;

-- An executing job's attempt yields first, then the job is cancelled. The attempt's details lead with its outcome,
-- then the cancel's own: the writer's payload is a compact JSON object, so its members follow the outcome once its
-- opening brace is dropped.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    worker_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    43 /* EventCode.JobExecutionYielded */, now(), s.namespace_id,
    s.worker_id,
    50 /* EntityCode.Job */, @p_job_id,
    s.definition_id, s.tenant_id, COALESCE(s.lineage_root_job_id, @p_job_id),
    s.execution_number,
    @p_event_message, 1 /* JobPayloadFormat.Json */,
    CASE
        WHEN @p_details IS NULL THEN convert_to('{"outcome":"cancelled"}', 'UTF8')
        ELSE convert_to('{"outcome":"cancelled",', 'UTF8') || substring(@p_details FROM 2)
    END
FROM _cancel_job s
WHERE
    s.applied
    AND s.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND s.from_status = 50 /* JobStatusCode.Executing */;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    70 /* EventCode.JobCancelled */, now(), s.namespace_id,
    50 /* EntityCode.Job */, @p_job_id,
    s.definition_id, s.tenant_id, COALESCE(s.lineage_root_job_id, @p_job_id),
    s.execution_number,
    @p_event_message, @p_details_format_id, @p_details
FROM _cancel_job s
WHERE s.applied AND s.event_recording_code = 20 /* EventRecordingCode.Audit */;

SELECT
    CASE
        WHEN s.from_status IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN NOT s.applied THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    CASE WHEN s.applied THEN 220 /* JobStatusCode.Cancelled */ ELSE s.from_status END::SMALLINT AS status,
    s.parent_job_id,
    CASE WHEN s.applied THEN s.from_version + 1 ELSE s.from_version END AS version,
    CASE WHEN EXISTS (SELECT 1 FROM _cancel_job_promoted) THEN 1 ELSE 0 END::SMALLINT AS lane_promoted
FROM (SELECT 1) one
LEFT JOIN _cancel_job s ON true;
