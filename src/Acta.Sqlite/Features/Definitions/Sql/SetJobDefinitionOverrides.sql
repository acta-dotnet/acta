DROP TABLE IF EXISTS temp._set_job_def_overrides;

CREATE TEMP TABLE _set_job_def_overrides AS
SELECT jd.id, jd.namespace_id, jd.version
FROM {{schema}}.definitions jd
WHERE jd.id = @p_id;

INSERT INTO {{schema}}.events (
    event_code, created_at_utc, namespace_id,
    actor_code, actor_key,
    job_id, job_ref, execution_number,
    lineage_root_id, definition_id,
    worker_id,
    from_status_code, to_status_code,
    execution_status_code, duration_ms,
    reason_code, reason_message)
SELECT
    30 /* JobEventCode.JobDefinitionPolicyChanged */, {{now}}, s.namespace_id,
    @p_actor_code, @p_actor_key,
    NULL, NULL, NULL,
    NULL, s.id,
    NULL,
    NULL, NULL,
    NULL, NULL,
    @p_reason_code, @p_reason_message
FROM temp._set_job_def_overrides s
WHERE s.version = @p_version;

UPDATE {{schema}}.definitions
   SET priority_code_override = @p_priority_code_override,
       max_attempts_override = @p_max_attempts_override,
       backoff_override = @p_backoff_override,
       execution_timeout_seconds_override = @p_execution_timeout_seconds_override,
       deadline_seconds_override = @p_deadline_seconds_override,
       deadline_behavior_code_override = @p_deadline_behavior_code_override,
       retention_seconds_override = @p_retention_seconds_override,
       audit_level_code_override = @p_audit_level_code_override,
       alert_profile_code_override = @p_alert_profile_code_override,
       alert_channel_name_override = @p_alert_channel_name_override,
       runbook_url_override = @p_runbook_url_override,
       display_name_override = @p_display_name_override,
       description_override = @p_description_override,
       modified_at_utc = {{now}},
       version = version + 1
 WHERE id = @p_id AND version = @p_version;

SELECT
    CASE
        WHEN s.id IS NULL THEN 2 /* DefinitionOverrideAction.NotFound */
        WHEN s.version <> @p_version THEN 3 /* DefinitionOverrideAction.VersionConflict */
        ELSE 1 /* DefinitionOverrideAction.Applied */
    END AS action
FROM (SELECT @p_id AS qid) q
LEFT JOIN temp._set_job_def_overrides s ON s.id = q.qid;
