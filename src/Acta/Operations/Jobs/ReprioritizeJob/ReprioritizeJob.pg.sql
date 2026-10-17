-- One batch, one implicit transaction; only the final SELECT returns rows. Lock order: the lane, then the
-- job's rows (docs/sql-execution-policy.md, "Lane lock order"); lane_id never changes, so the
-- unlocked read that finds it is safe.
DROP TABLE IF EXISTS pg_temp._reprioritize_job_lane;
DROP TABLE IF EXISTS pg_temp._reprioritize_job;
CREATE TEMP TABLE _reprioritize_job_lane (id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _reprioritize_job (
    from_status SMALLINT NOT NULL,
    from_version INT NOT NULL,
    namespace_id INT NOT NULL,
    lineage_root_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    audit_level_code SMALLINT NOT NULL,
    job_ref UUID NOT NULL
) ON COMMIT DROP;

INSERT INTO _reprioritize_job_lane (id)
SELECT l.id
FROM {{schema}}.lanes l
WHERE l.id = (SELECT r.lane_id FROM {{schema}}.runtimes r WHERE r.job_id = @p_id)
FOR UPDATE;

INSERT INTO _reprioritize_job (
    from_status, from_version, namespace_id, lineage_root_id, definition_id, tenant_id, execution_number,
    audit_level_code, job_ref)
SELECT
    r.status_code, r.version, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, r.execution_number,
    j.audit_level_code, j.job_ref
FROM {{schema}}.jobs j
JOIN {{schema}}.runtimes r ON r.job_id = j.id
WHERE j.id = @p_id
FOR UPDATE;

UPDATE {{schema}}.runtimes AS r
SET
    priority_code = @p_priority_code,
    modified_at_utc = now(),
    version = r.version + 1
FROM _reprioritize_job s
WHERE
    r.job_id = @p_id
    AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    AND s.from_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id, actor_code, actor, job_id, job_ref, execution_number,
    lineage_root_id, definition_id, tenant_id, reason_code, reason_message)
SELECT
    74 /* EventCode.JobReprioritized */, now(), s.namespace_id, @p_actor_code, @p_actor, @p_id, s.job_ref,
    s.execution_number, COALESCE(s.lineage_root_id, @p_id), s.definition_id, s.tenant_id, @p_reason_code, @p_reason_message
FROM _reprioritize_job s
WHERE
    s.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
    AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    AND s.from_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

SELECT
    CASE
        WHEN s.from_status IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN 3 /* ControlAction.Rejected */
        ELSE 1 /* ControlAction.Applied */
    END::SMALLINT AS action,
    s.from_status AS status_code,
    CASE
        WHEN s.from_status IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN _reprioritize_job s ON true;
