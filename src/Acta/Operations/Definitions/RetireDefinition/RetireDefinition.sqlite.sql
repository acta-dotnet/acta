DROP TABLE IF EXISTS temp._retire_job_definition;

CREATE TEMP TABLE _retire_job_definition AS
SELECT jd.definition_id, jd.namespace_id, jd.definition_status_code, jd.version, jd.retention_seconds_effective
FROM {{schema}}.definitions jd
WHERE jd.definition_id = @p_definition_id;

DROP TABLE IF EXISTS temp._retire_parked_jobs;

-- Snapshot of the rows the sweep is about to cancel, taken before the UPDATE moves them out of the
-- statuses that select them. SQLite admits one writer at a time, so nothing can join or leave the
-- set between the snapshot and the UPDATE.
CREATE TEMP TABLE _retire_parked_jobs AS
SELECT
    j.job_id,
    j.parent_job_id,
    j.namespace_id,
    j.tenant_id,
    j.definition_id,
    j.event_recording_code,
    COALESCE(j.lineage_root_job_id, j.job_id) AS lineage_root_job_id,
    r.execution_number,
    r.lane_id
FROM {{schema}}.jobs j
JOIN {{schema}}.jobs_state r ON r.job_id = j.job_id
WHERE
    j.definition_id IN (
        SELECT s.definition_id
        FROM temp._retire_job_definition s
        WHERE s.version = @p_version AND s.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
    )
    AND r.job_status_code IN (
        10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */, 30 /* JobStatusCode.Paused */
    );

UPDATE {{schema}}.definitions
SET
    definition_status_code = 240 /* DefinitionStatusCode.Retired */,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    definition_id = @p_definition_id
    AND version = @p_version
    AND definition_status_code <> 240 /* DefinitionStatusCode.Retired */;

UPDATE {{schema}}.jobs_state
SET
    job_status_code = 220 /* JobStatusCode.Cancelled */,
    leased_by_worker_id = NULL,
    lease_expires_at_utc = NULL,
    retention_until_utc = {{now}} + (SELECT s.retention_seconds_effective FROM temp._retire_job_definition s) * 1000,
    modified_at_utc = {{now}},
    version = version + 1
WHERE job_id IN (SELECT p.job_id FROM temp._retire_parked_jobs p);

UPDATE {{schema}}.schedules_state AS st
SET
    next_occurrence_at_utc = NULL,
    last_advanced_occurrence_at_utc = NULL,
    modified_at_utc = {{now}}
FROM {{schema}}.schedules s
WHERE
    st.job_id = s.job_id
    AND st.schedule_name = s.schedule_name
    AND s.definition_id IN (
        SELECT d.definition_id
        FROM temp._retire_job_definition d
        WHERE d.version = @p_version AND d.definition_status_code <> 240 /* DefinitionStatusCode.Retired */
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
    70 /* EventCode.JobCancelled */,
    {{now}},
    p.namespace_id,
    50 /* EntityCode.Job */,
    p.job_id,
    p.definition_id,
    p.tenant_id,
    p.lineage_root_job_id,
    p.execution_number,
    @p_event_message,
    1 /* JobPayloadFormat.Json */,
    @p_job_details
FROM temp._retire_parked_jobs p
WHERE p.event_recording_code = 20 /* EventRecordingCode.Audit */;

INSERT INTO {{schema}}.events (
    event_code,
    created_at_utc,
    namespace_id,
    entity_code,
    entity_id,
    definition_id,
    event_message,
    details_format_id,
    details)
SELECT
    31 /* EventCode.JobDefinitionRetired */,
    {{now}},
    s.namespace_id,
    30 /* EntityCode.Definition */,
    s.definition_id,
    s.definition_id,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._retire_job_definition s
WHERE s.version = @p_version AND s.definition_status_code <> 240 /* DefinitionStatusCode.Retired */;

-- Once every parked member is cancelled, each of their lanes hands on to its lowest-id unfinished
-- member if that member is Blocked and no member still runs. The immediate transaction is the lanes'
-- mutex on SQLite, and this is the last write, so the final SELECT's changes() counts its promotions.
UPDATE {{schema}}.jobs_state
SET
    job_status_code = 10 /* JobStatusCode.Ready */,
    next_run_at_utc = MAX(next_run_at_utc, {{now}}),
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    job_status_code = 15 /* JobStatusCode.Blocked */
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.jobs_state o
        WHERE
            o.lane_id = jobs_state.lane_id
            AND o.lane_id IS NOT NULL
            AND o.job_status_code IN (
                10 /* JobStatusCode.Ready */, 20 /* JobStatusCode.Suspended */,
                50 /* JobStatusCode.Executing */
            )
    )
    AND job_id IN (
        SELECT (
            SELECT m.job_id
            FROM {{schema}}.jobs_state m
            WHERE
                m.lane_id = l.lane_id
                AND m.lane_id IS NOT NULL
                AND m.job_status_code IN (
                    10 /* JobStatusCode.Ready */, 15 /* JobStatusCode.Blocked */, 20 /* JobStatusCode.Suspended */,
                    30 /* JobStatusCode.Paused */, 50 /* JobStatusCode.Executing */
                )
            ORDER BY m.job_id
            LIMIT 1
        )
        FROM (SELECT DISTINCT p.lane_id FROM temp._retire_parked_jobs p WHERE p.lane_id IS NOT NULL) l
    );

SELECT
    CASE
        WHEN s.definition_id IS NULL THEN 2 /* DefinitionOverrideAction.NotFound */
        WHEN s.version <> @p_version THEN 3 /* DefinitionOverrideAction.VersionConflict */
        ELSE 1 /* DefinitionOverrideAction.Applied */
    END AS action,
    CASE
        WHEN s.version = @p_version AND s.definition_status_code <> 240 /* DefinitionStatusCode.Retired */ THEN s.version + 1
        ELSE s.version
    END AS version,
    p.job_id,
    p.parent_job_id,
    CASE WHEN changes() > 0 THEN 1 ELSE 0 END AS lane_promoted
FROM (SELECT @p_definition_id AS qid) q
LEFT JOIN temp._retire_job_definition s ON s.definition_id = q.qid
LEFT JOIN temp._retire_parked_jobs p ON 1 = 1;
