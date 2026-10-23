DROP TABLE IF EXISTS temp._reprioritize_job;

CREATE TEMP TABLE _reprioritize_job AS
SELECT j.job_id, r.job_status_code AS from_status, r.version AS from_version
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE j.job_id = @p_job_id;

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
    74 /* EventCode.JobReprioritized */,
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
WHERE
    j.job_id = @p_job_id
    AND j.event_recording_code = 20 /* EventRecordingCode.Audit */
    AND (@p_expected_version IS NULL OR r.version = @p_expected_version)
    AND r.job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

UPDATE {{schema}}.jobs_state
SET
    job_priority_code = @p_job_priority_code,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_id = @p_job_id
    AND (@p_expected_version IS NULL OR version = @p_expected_version)
    AND job_status_code NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */);

SELECT
    CASE
        WHEN s.job_id IS NULL THEN 2 /* ControlAction.NotFound */
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN 5 /* ControlAction.VersionConflict */
        WHEN s.from_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN 1 /* ControlAction.Applied */
        ELSE 3 /* ControlAction.Rejected */
    END AS action,
    s.from_status AS status,
    CASE
        WHEN s.job_id IS NULL THEN NULL
        WHEN @p_expected_version IS NOT NULL AND s.from_version <> @p_expected_version THEN s.from_version
        WHEN s.from_status NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) THEN s.from_version + 1
        ELSE s.from_version
    END AS version
FROM (SELECT @p_job_id AS qid) q
LEFT JOIN temp._reprioritize_job s ON s.job_id = q.qid;
