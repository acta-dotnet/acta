-- One batch, one implicit transaction; only the final SELECT returns rows. Lock order: the lane, then the
-- job's rows (docs/internals/sql-execution-policy.md, "Lane lock order"); lane_id never changes, so the
-- unlocked read that finds it is safe.
DROP TABLE IF EXISTS pg_temp._resume_job_lane;
DROP TABLE IF EXISTS pg_temp._resume_job;
CREATE TEMP TABLE _resume_job_lane (id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _resume_job (
    from_status SMALLINT NOT NULL,
    from_version INT NOT NULL,
    lane_id BIGINT,
    namespace_id INT NOT NULL,
    lineage_root_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    audit_level_code SMALLINT NOT NULL,
    job_ref UUID NOT NULL,
    to_status SMALLINT NOT NULL
) ON COMMIT DROP;

INSERT INTO _resume_job_lane (id)
SELECT l.id
FROM {{schema}}.lanes l
WHERE l.id = (SELECT r.lane_id FROM {{schema}}.runtimes r WHERE r.job_id = @p_id)
FOR UPDATE;

INSERT INTO _resume_job (
    from_status, from_version, lane_id, namespace_id, lineage_root_id, definition_id, tenant_id, execution_number,
    audit_level_code, job_ref, to_status)
SELECT
    r.status_code, r.version, r.lane_id, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number,
    j.audit_level_code, j.job_ref, 10 /* JobStatusCode.Ready */
FROM {{schema}}.runtimes r
JOIN {{schema}}.jobs j ON j.id = r.job_id
WHERE r.job_id = @p_id
FOR UPDATE OF r;

-- A laned job resumes Blocked while another member runs or an older member is unfinished.
UPDATE _resume_job s
SET to_status = 15 /* JobStatusCode.Blocked */
WHERE
    s.lane_id IS NOT NULL
    AND EXISTS (
        SELECT 1
        FROM {{schema}}.runtimes o
        WHERE
            o.lane_id = s.lane_id
            AND o.lane_id IS NOT NULL
            AND o.status_code IN (
                10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                30 /* JobStatusCode.Paused */, 40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
            )
            AND o.job_id <> @p_id
            AND (
                o.status_code IN (
                    10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                    40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */
                )
                OR o.job_id < @p_id
            )
    );

UPDATE {{schema}}.runtimes AS r
SET
    status_code = s.to_status,
    next_run_at_utc = COALESCE(@p_next_run_at_utc, now()),
    modified_at_utc = now(),
    version = r.version + 1
FROM _resume_job s
WHERE
    r.job_id = @p_id
    AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    AND s.from_status = 30 /* JobStatusCode.Paused */;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor_key, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, from_status_code, to_status_code, reason_code, reason_message)
SELECT
    72 /* EventCode.JobResumed */, now(), s.namespace_id, @p_actor_code, @p_actor_key, @p_id, s.job_ref,
    s.execution_number, COALESCE(s.lineage_root_id, @p_id), s.definition_id, s.tenant_id, 30 /* JobStatusCode.Paused */,
    s.to_status, @p_reason_code, @p_reason_message
FROM _resume_job s
WHERE
    s.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
    AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    AND s.from_status = 30 /* JobStatusCode.Paused */;

SELECT
    CASE
        WHEN s.from_status IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.from_status <> 30 /* JobStatusCode.Paused */ THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    CASE
        WHEN s.from_status IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_status
        WHEN s.from_status <> 30 /* JobStatusCode.Paused */ THEN s.from_status
        ELSE s.to_status
    END AS status_code,
    CASE
        WHEN s.from_status IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status <> 30 /* JobStatusCode.Paused */ THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN _resume_job s ON true;
