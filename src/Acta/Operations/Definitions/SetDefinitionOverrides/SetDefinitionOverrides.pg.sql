-- One batch, one implicit transaction; only the final SELECT returns rows. The definition row is locked and
-- updated only at the expected version; its meter key comes from base columns the update leaves alone.
DROP TABLE IF EXISTS pg_temp._set_definition_overrides;
CREATE TEMP TABLE _set_definition_overrides (
    namespace_id INT NOT NULL,
    version INT NOT NULL,
    meter VARCHAR NOT NULL
) ON COMMIT DROP;

INSERT INTO _set_definition_overrides (namespace_id, version, meter)
SELECT jd.namespace_id, jd.version, lower(COALESCE(jd.rate_name, jd.definition_name))
FROM {{schema}}.definitions jd
WHERE jd.definition_id = @p_definition_id
FOR UPDATE;

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
    modified_at_utc = now(),
    version = version + 1
WHERE definition_id = @p_definition_id AND version = @p_version;

-- A definition on a meter carries every sibling that already has a rate (declared or overridden) on the same
-- effective key with it, so the meter never carries two rates and the last write wins for every participant.
UPDATE {{schema}}.definitions sib
SET
    rate_limit_override = @p_rate_limit_override,
    modified_at_utc = now(),
    version = sib.version + 1
FROM _set_definition_overrides t
WHERE
    t.version = @p_version
    AND sib.namespace_id = t.namespace_id
    AND sib.definition_id <> @p_definition_id
    AND (sib.rate_limit IS NOT NULL OR sib.rate_limit_override IS NOT NULL)
    AND lower(COALESCE(sib.rate_name, sib.definition_name)) = t.meter;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    entity_code, entity_id,
    definition_id,
    event_message, details_format_id, details
)
SELECT
    30 /* EventCode.JobDefinitionOverridesUpdated */, now(), t.namespace_id,
    30 /* EntityCode.Definition */, @p_definition_id,
    @p_definition_id,
    @p_event_message, @p_details_format_id, @p_details
FROM _set_definition_overrides t
WHERE t.version = @p_version;

SELECT
    CASE
        WHEN t.namespace_id IS NULL THEN 2 /* DefinitionOverrideAction.NotFound */
        WHEN t.version <> @p_version THEN 3 /* DefinitionOverrideAction.VersionConflict */
        ELSE 1 /* DefinitionOverrideAction.Applied */
    END::SMALLINT AS action,
    CASE WHEN t.version = @p_version THEN t.version + 1 ELSE t.version END AS version
FROM (SELECT 1) one
LEFT JOIN _set_definition_overrides t ON true;
