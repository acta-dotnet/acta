-- One batch, one implicit transaction; only the final SELECT returns rows. Lock order: the lane, then the
-- job's rows (docs/sql-internals.md, "Lane lock order"); lane_id never changes, so the
-- unlocked read that finds it is safe.
DROP TABLE IF EXISTS pg_temp._reprioritize_job_lane;
DROP TABLE IF EXISTS pg_temp._reprioritize_job;
CREATE TEMP TABLE _reprioritize_job_lane (lane_id BIGINT NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _reprioritize_job (
    from_status SMALLINT NOT NULL,
    from_version INT NOT NULL,
    namespace_id INT NOT NULL,
    lineage_root_job_id BIGINT,
    definition_id INT NOT NULL,
    tenant_id INT,
    execution_number INT NOT NULL,
    event_recording_code SMALLINT NOT NULL
) ON COMMIT DROP;

INSERT INTO _reprioritize_job_lane (lane_id)
SELECT l.lane_id
FROM {{schema}}.lanes l
WHERE l.lane_id = (SELECT r.lane_id FROM {{schema}}.jobs_state r WHERE r.job_id = @p_job_id)
FOR UPDATE;

INSERT INTO _reprioritize_job (
    from_status, from_version, namespace_id, lineage_root_job_id, definition_id, tenant_id, execution_number,
    event_recording_code)
SELECT
    r.job_status_code, r.version, j.namespace_id, j.lineage_root_job_id, j.definition_id, j.tenant_id, r.execution_number,
    j.event_recording_code
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id
FOR UPDATE;

UPDATE {{schema}}.jobs_state AS r
SET
    job_priority_code = @p_job_priority_code,
    modified_at_utc = now(),
    version = r.version + 1
FROM _reprioritize_job s
WHERE
    r.job_id = @p_job_id
    AND (@p_expected_version IS NULL OR s.from_version = @p_expected_version)
    AND s.from_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id, tenant_id, lineage_root_job_id,
    execution_number,
    event_message, details_format_id, details
)
SELECT
    74 /* EventCode.JobReprioritized */, now(), s.namespace_id,
    50 /* EntityCode.Job */, @p_job_id,
    s.definition_id, s.tenant_id, COALESCE(s.lineage_root_job_id, @p_job_id),
    s.execution_number,
    @p_event_message, @p_details_format_id, @p_details
FROM _reprioritize_job s
WHERE
    s.event_recording_code = 20 /* EventRecordingCode.Audit */
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
    s.from_status AS status,
    CASE
        WHEN s.from_status IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
            THEN s.from_version
        ELSE s.from_version + 1
    END AS version
FROM (SELECT 1) one
LEFT JOIN _reprioritize_job s ON true;
