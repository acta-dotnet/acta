SELECT jd.id, ns.name, jd.name, jd.status_code, jd.definition_hash, jd.manifest_generation_at_utc,
       jd.input_type_name, jd.input_format_id, jd.input_format_name,
       jd.output_type_name, jd.output_format_id, jd.output_format_name,
       jd.priority_code, jd.priority_code_override, jd.priority_code_effective,
       jd.max_attempts, jd.max_attempts_override, jd.max_attempts_effective,
       jd.backoff, jd.backoff_override, jd.backoff_effective,
       jd.execution_timeout_seconds, jd.execution_timeout_seconds_override, jd.execution_timeout_seconds_effective,
       jd.deadline_seconds, jd.deadline_seconds_override, jd.deadline_seconds_effective,
       jd.deadline_behavior_code, jd.deadline_behavior_code_override, jd.deadline_behavior_code_effective,
       jd.retention_seconds, jd.retention_seconds_override, jd.retention_seconds_effective,
       jd.audit_level_code, jd.audit_level_code_override, jd.audit_level_code_effective,
       jd.alert_profile_code, jd.alert_profile_code_override, jd.alert_profile_code_effective,
       jd.alert_channel_name, jd.alert_channel_name_override, jd.alert_channel_name_effective,
       jd.runbook_url, jd.runbook_url_override, jd.runbook_url_effective,
       jd.display_name, jd.display_name_override, jd.display_name_effective,
       jd.description, jd.description_override, jd.description_effective,
       jd.created_at_utc, jd.modified_at_utc, jd.version
  FROM {{schema}}.definitions jd
  JOIN {{schema}}.namespaces ns ON ns.id = jd.namespace_id
 WHERE jd.id = @p_id;
