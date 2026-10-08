-- One batch, one implicit transaction; only the final SELECT returns rows. Lock order: the lane, then the
-- job's rows (docs/internals/sql-execution-policy.md, "Lane lock order"); lane_id never changes, so the
-- unlocked read that finds it is safe.
DROP TABLE IF EXISTS pg_temp._cancel_job_lane;
DROP TABLE IF EXISTS pg_temp._cancel_job;
DROP TABLE IF EXISTS pg_temp._cancel_job_promoted;
CREATE TEMP TABLE _cancel_job_lane (id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _cancel_job (
    from_status SMALLINT NOT NULL,
    from_version INT NOT NULL,
    lane_id BIGINT,
    parent_id BIGINT,
    namespace_id INT NOT NULL,
    lineage_root_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    worker_id INT,
    audit_level_code SMALLINT NOT NULL,
    job_ref UUID NOT NULL,
    applied BOOLEAN NOT NULL
) ON COMMIT DROP;
CREATE TEMP TABLE _cancel_job_promoted (job_id BIGINT NOT NULL) ON COMMIT DROP;

INSERT INTO _cancel_job_lane (id)
SELECT l.id
FROM {{schema}}.lanes l
WHERE l.id = (SELECT r.lane_id FROM {{schema}}.runtimes r WHERE r.job_id = @p_id)
FOR UPDATE;

INSERT INTO _cancel_job (
    from_status, from_version, lane_id, parent_id, namespace_id, lineage_root_id, definition_id, tenant_id,
    execution_number, worker_id, audit_level_code, job_ref, applied)
SELECT
    r.status_code, r.version, r.lane_id, j.parent_id, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id,
    r.execution_number, r.leased_by_worker_id, j.audit_level_code, j.job_ref,
    (@p_expected_version IS NULL OR r.version = @p_expected_version)
    AND r.status_code IN (
        30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */,
        15 /* JobStatusCode.Blocked */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
    )
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_id
FOR UPDATE;

UPDATE {{schema}}.runtimes AS r
SET
    status_code = 220 /* JobStatusCode.Cancelled */,
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = now() + make_interval(secs => jd.retention_seconds_effective),
    modified_at_utc = now(),
    version = r.version + 1
FROM _cancel_job s
JOIN {{schema}}.definitions jd ON jd.id = s.definition_id
WHERE r.job_id = @p_id AND s.applied;

-- A cancelled head hands its lane on; a cancelled Blocked follower leaves the head where it is. A restarted
-- member can wait Blocked below a running one, so the oldest unfinished member is promoted only while no
-- member runs.
WITH promoted AS (
    UPDATE {{schema}}.runtimes pr
    SET
        status_code = 10 /* JobStatusCode.Ready */,
        next_run_at_utc = GREATEST(pr.next_run_at_utc, now()),
        modified_at_utc = now(),
        version = pr.version + 1
    FROM _cancel_job s
    WHERE
        s.applied
        AND s.lane_id IS NOT NULL
        AND pr.status_code = 15 /* JobStatusCode.Blocked */
        AND pr.job_id = (
            SELECT m.job_id
            FROM {{schema}}.runtimes m
            WHERE
                m.lane_id = s.lane_id
                AND m.lane_id IS NOT NULL
                AND m.status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1
        )
        AND NOT EXISTS (
            SELECT 1
            FROM {{schema}}.runtimes o
            WHERE
                o.lane_id = s.lane_id
                AND o.lane_id IS NOT NULL
                AND o.status_code IN (
                    10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                    40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                )
        )
    RETURNING pr.job_id
)
INSERT INTO _cancel_job_promoted (job_id)
SELECT p.job_id FROM promoted p;

-- An executing job's attempt ends Cancelled first, then the job does.
INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, worker_id, from_status_code, to_status_code, execution_status_code,
    reason_code, reason_message)
SELECT
    41 /* EventCode.JobExecutionFinished */, now(), s.namespace_id, @p_actor_code, @p_actor_key, @p_id, s.job_ref,
    s.execution_number, COALESCE(s.lineage_root_id, @p_id), s.definition_id, s.tenant_id, s.worker_id,
    50 /* JobStatusCode.Executing */, 220 /* JobStatusCode.Cancelled */, 220 /* ExecutionStatusCode.Cancelled */,
    @p_reason_code, @p_reason_message
FROM _cancel_job s
WHERE
    s.applied
    AND s.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
    AND s.from_status = 50 /* JobStatusCode.Executing */;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, from_status_code, to_status_code, reason_code, reason_message)
SELECT
    70 /* EventCode.JobCancelled */, now(), s.namespace_id, @p_actor_code, @p_actor_key, @p_id, s.job_ref,
    s.execution_number, COALESCE(s.lineage_root_id, @p_id), s.definition_id, s.tenant_id, s.from_status,
    220 /* JobStatusCode.Cancelled */, @p_reason_code, @p_reason_message
FROM _cancel_job s
WHERE s.applied AND s.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */;

SELECT
    CASE
        WHEN s.from_status IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN NOT s.applied THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    CASE WHEN s.applied THEN 220 /* JobStatusCode.Cancelled */ ELSE s.from_status END::SMALLINT AS status_code,
    s.parent_id AS parent_id,
    CASE WHEN s.applied THEN s.from_version + 1 ELSE s.from_version END AS version,
    CASE WHEN EXISTS (SELECT 1 FROM _cancel_job_promoted) THEN 1 ELSE 0 END::SMALLINT AS lane_promoted
FROM (SELECT 1) one
LEFT JOIN _cancel_job s ON true;
