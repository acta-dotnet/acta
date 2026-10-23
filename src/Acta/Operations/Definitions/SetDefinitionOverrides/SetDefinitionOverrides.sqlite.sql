DROP TABLE IF EXISTS temp._set_job_def_overrides;

CREATE TEMP TABLE _set_job_def_overrides AS
SELECT jd.definition_id, jd.namespace_id, jd.version, jd.rate_limit, LOWER(COALESCE(jd.rate_name, jd.definition_name)) AS meter
FROM {{schema}}.definitions jd
WHERE jd.definition_id = @p_definition_id;

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
    30 /* EventCode.JobDefinitionOverridesUpdated */,
    {{now}},
    s.namespace_id,
    30 /* EntityCode.Definition */,
    s.definition_id,
    s.definition_id,
    @p_event_message,
    @p_details_format_id,
    @p_details
FROM temp._set_job_def_overrides s
WHERE s.version = @p_version;

UPDATE {{schema}}.definitions
SET
    job_priority_code_override = @p_job_priority_code_override,
    max_attempts_override = @p_max_attempts_override,
    concurrency_limit_override = @p_concurrency_limit_override,
    rate_limit_override = @p_rate_limit_override,
    backoff_override = @p_backoff_override,
    execution_timeout_seconds_override = @p_execution_timeout_seconds_override,
    deadline_seconds_override = @p_deadline_seconds_override,
    deadline_behavior_code_override = @p_deadline_behavior_code_override,
    retention_seconds_override = @p_retention_seconds_override,
    event_recording_code_override = @p_event_recording_code_override,
    alert_profile_code_override = @p_alert_profile_code_override,
    alert_channel_name_override = @p_alert_channel_name_override,
    runbook_url_override = @p_runbook_url_override,
    display_name_override = @p_display_name_override,
    description_override = @p_description_override,
    modified_at_utc = {{now}},
    version = version + 1
WHERE definition_id = @p_definition_id AND version = @p_version;

-- A definition on a meter carries every sibling that already has a rate (declared or overridden) on
-- the same key along with it here, so the meter never carries two rates. This batch has no
-- early-return, so the version is re-checked against the pre-write snapshot, the same guard row locking gets for free.
UPDATE {{schema}}.definitions
SET
    rate_limit_override = @p_rate_limit_override,
    modified_at_utc = {{now}},
    version = version + 1
WHERE
    definition_id <> @p_definition_id
    AND (rate_limit IS NOT NULL OR rate_limit_override IS NOT NULL)
    AND namespace_id = (SELECT s.namespace_id FROM temp._set_job_def_overrides s)
    AND LOWER(COALESCE(rate_name, definition_name)) = (
        SELECT s.meter FROM temp._set_job_def_overrides s WHERE s.version = @p_version
    );

SELECT
    CASE
        WHEN s.definition_id IS NULL THEN 2 /* DefinitionOverrideAction.NotFound */
        WHEN s.version <> @p_version THEN 3 /* DefinitionOverrideAction.VersionConflict */
        ELSE 1 /* DefinitionOverrideAction.Applied */
    END AS action,
    CASE WHEN s.version = @p_version THEN s.version + 1 ELSE s.version END AS version
FROM (SELECT @p_definition_id AS qid) q
LEFT JOIN temp._set_job_def_overrides s ON s.definition_id = q.qid;
