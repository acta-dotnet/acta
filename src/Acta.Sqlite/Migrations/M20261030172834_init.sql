-- SQLite has no schema container; tables are created in the attached database ({{schema}}).

-- alerts
CREATE TABLE IF NOT EXISTS {{schema}}.alerts (
    alert_id integer PRIMARY KEY AUTOINCREMENT,
    alert_ref text NOT NULL,
    namespace_id integer NOT NULL,
    job_id integer NULL,
    job_ref text NULL,
    alert_origin_code integer NOT NULL,
    alert_severity_code integer NOT NULL,
    alert_kind_code integer NOT NULL,
    title text NOT NULL,
    alert_message text NOT NULL,
    channel_name text NOT NULL,
    deduplication_key text NULL,
    occurrence_count integer NOT NULL,
    last_projected_event_id integer NULL,
    resolved_at_utc integer NULL,
    acknowledged_at_utc integer NULL,
    delivery_status_code integer NOT NULL,
    retry_count integer NOT NULL,
    retry_after_utc integer NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    version integer DEFAULT 0 NOT NULL
    , CONSTRAINT ck_alerts_job_ref_pair CHECK ((job_id IS NULL AND job_ref IS NULL) OR (job_id IS NOT NULL AND job_ref IS NOT NULL))
    , CONSTRAINT ck_alerts_occurrence_count CHECK (occurrence_count >= 1)
) STRICT;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_alerts_deduplication_key_identity ON alerts (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_alerts_delivery_due ON alerts (namespace_id, delivery_status_code, retry_after_utc, alert_id);
CREATE INDEX IF NOT EXISTS {{schema}}.ix_alerts_namespace_created ON alerts (namespace_id, created_at_utc DESC, alert_id DESC);
CREATE INDEX IF NOT EXISTS {{schema}}.ix_alerts_namespace_unresolved ON alerts (namespace_id, created_at_utc DESC, alert_id DESC) WHERE resolved_at_utc IS NULL;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_alerts_job ON alerts (job_id, alert_id) WHERE job_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_alerts_ref ON alerts (alert_ref);
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_alerts_deduplication_key ON alerts (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND resolved_at_utc IS NULL;

-- checkpoints
CREATE TABLE IF NOT EXISTS {{schema}}.checkpoints (
    job_id integer NOT NULL,
    checkpoint_kind_code integer NOT NULL,
    checkpoint_name text NOT NULL,
    checkpoint_status_code integer NULL,
    due_at_utc integer NULL,
    value_format_id integer DEFAULT 0 NOT NULL,
    value blob NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    version integer DEFAULT 0 NOT NULL
    , CONSTRAINT pk_checkpoints PRIMARY KEY (job_id, checkpoint_kind_code, checkpoint_name)
    , CONSTRAINT ck_checkpoints_value_pair CHECK ((value_format_id = 0 AND value IS NULL) OR (value_format_id <> 0 AND value IS NOT NULL))
    , CONSTRAINT ck_checkpoints_kind_shape CHECK ((checkpoint_kind_code NOT IN (10, 40) OR (checkpoint_status_code IS NULL AND due_at_utc IS NULL)) AND (checkpoint_kind_code NOT IN (20, 30, 50, 60) OR checkpoint_status_code IS NOT NULL) AND (checkpoint_kind_code <> 30 OR due_at_utc IS NOT NULL))
    , CONSTRAINT ck_checkpoints_value_format_id_byte CHECK (value_format_id BETWEEN 0 AND 255)
) STRICT;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_checkpoints_pending_latches ON checkpoints (job_id, checkpoint_name) WHERE checkpoint_kind_code = 50 AND checkpoint_status_code = 10;

-- definitions
CREATE TABLE IF NOT EXISTS {{schema}}.definitions (
    definition_id integer PRIMARY KEY AUTOINCREMENT,
    namespace_id integer NOT NULL,
    definition_name text NOT NULL,
    definition_status_code integer NOT NULL,
    definition_hash text NOT NULL,
    manifest_generation_at_utc integer NOT NULL,
    input_type_name text NOT NULL,
    input_format_id integer NOT NULL,
    input_format_name text NOT NULL,
    output_type_name text NULL,
    output_format_id integer NOT NULL,
    output_format_name text NOT NULL,
    tenant_requirement_code integer NOT NULL,
    job_priority_code integer NOT NULL,
    job_priority_code_override integer NULL,
    job_priority_code_effective integer AS (COALESCE(job_priority_code_override, job_priority_code)) STORED,
    max_attempts integer NOT NULL,
    max_attempts_override integer NULL,
    max_attempts_effective integer AS (COALESCE(max_attempts_override, max_attempts)) STORED,
    concurrency_limit integer NULL,
    concurrency_limit_override integer NULL,
    concurrency_limit_effective integer AS (COALESCE(concurrency_limit_override, concurrency_limit)) STORED,
    rate_limit text NULL,
    rate_limit_override text NULL,
    rate_limit_effective text AS (COALESCE(rate_limit_override, rate_limit)) STORED,
    rate_name text NULL,
    lane_name text NULL,
    backoff text NOT NULL,
    backoff_override text NULL,
    backoff_effective text AS (COALESCE(backoff_override, backoff)) STORED,
    execution_timeout_seconds integer NOT NULL,
    execution_timeout_seconds_override integer NULL,
    execution_timeout_seconds_effective integer AS (COALESCE(execution_timeout_seconds_override, execution_timeout_seconds)) STORED,
    deadline_seconds integer NOT NULL,
    deadline_seconds_override integer NULL,
    deadline_seconds_effective integer AS (COALESCE(deadline_seconds_override, deadline_seconds)) STORED,
    deadline_behavior_code integer NOT NULL,
    deadline_behavior_code_override integer NULL,
    deadline_behavior_code_effective integer AS (COALESCE(deadline_behavior_code_override, deadline_behavior_code)) STORED,
    retention_seconds integer NOT NULL,
    retention_seconds_override integer NULL,
    retention_seconds_effective integer AS (COALESCE(retention_seconds_override, retention_seconds)) STORED,
    event_recording_code integer NOT NULL,
    event_recording_code_override integer NULL,
    event_recording_code_effective integer AS (COALESCE(event_recording_code_override, event_recording_code)) STORED,
    alert_profile_code integer NOT NULL,
    alert_profile_code_override integer NULL,
    alert_profile_code_effective integer AS (COALESCE(alert_profile_code_override, alert_profile_code)) STORED,
    firing_failure_policy_code integer NOT NULL,
    scheduled_delivery_code integer NOT NULL,
    default_input_format_id integer DEFAULT 0 NOT NULL,
    default_input blob NULL,
    alert_channel_name text NULL,
    alert_channel_name_override text NULL,
    alert_channel_name_effective text AS (COALESCE(alert_channel_name_override, alert_channel_name)) STORED,
    runbook_url text NULL,
    runbook_url_override text NULL,
    runbook_url_effective text AS (COALESCE(runbook_url_override, runbook_url)) STORED,
    display_name text NULL,
    display_name_override text NULL,
    display_name_effective text AS (COALESCE(display_name_override, display_name)) STORED,
    description text NULL,
    description_override text NULL,
    description_effective text AS (COALESCE(description_override, description)) STORED,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    version integer DEFAULT 0 NOT NULL
    , CONSTRAINT ck_definitions_max_attempts CHECK (max_attempts >= 1)
    , CONSTRAINT ck_definitions_max_attempts_override CHECK (max_attempts_override IS NULL OR max_attempts_override >= 1)
    , CONSTRAINT ck_definitions_execution_timeout CHECK (execution_timeout_seconds > 0)
    , CONSTRAINT ck_definitions_execution_timeout_override CHECK (execution_timeout_seconds_override IS NULL OR execution_timeout_seconds_override > 0)
    , CONSTRAINT ck_definitions_deadline CHECK (deadline_seconds >= 0)
    , CONSTRAINT ck_definitions_deadline_override CHECK (deadline_seconds_override IS NULL OR deadline_seconds_override >= 0)
    , CONSTRAINT ck_definitions_retention CHECK (retention_seconds >= 0)
    , CONSTRAINT ck_definitions_retention_override CHECK (retention_seconds_override IS NULL OR retention_seconds_override >= 0)
    , CONSTRAINT ck_definitions_input_format_id_byte CHECK (input_format_id BETWEEN 0 AND 255)
    , CONSTRAINT ck_definitions_output_format_id_byte CHECK (output_format_id BETWEEN 0 AND 255)
    , CONSTRAINT ck_definitions_default_input_format_id_byte CHECK (default_input_format_id BETWEEN 0 AND 255)
) STRICT;
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_definitions_namespace_name ON definitions (namespace_id, definition_name);

-- events
CREATE TABLE IF NOT EXISTS {{schema}}.events (
    event_id integer PRIMARY KEY AUTOINCREMENT,
    namespace_id integer NOT NULL,
    worker_id integer DEFAULT 0 NOT NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    entity_code integer NOT NULL,
    entity_id integer NOT NULL,
    event_code integer NOT NULL,
    definition_id integer NULL,
    tenant_id integer NULL,
    lineage_root_job_id integer NULL,
    execution_number integer NULL,
    duration_ms integer NULL,
    event_message text NULL,
    details_format_id integer DEFAULT 0 NOT NULL,
    details blob NULL
    , CONSTRAINT ck_events_details_pair CHECK ((details_format_id = 0 AND details IS NULL) OR (details_format_id <> 0 AND details IS NOT NULL))
    , CONSTRAINT ck_events_details_format_id_byte CHECK (details_format_id BETWEEN 0 AND 255)
) STRICT;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_events_lineage_timeline ON events (lineage_root_job_id, created_at_utc, event_id) WHERE lineage_root_job_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_events_namespace_timeline ON events (namespace_id, event_code, created_at_utc, event_id);
CREATE INDEX IF NOT EXISTS {{schema}}.ix_events_entity_timeline ON events (entity_code, entity_id, created_at_utc, event_id);
CREATE INDEX IF NOT EXISTS {{schema}}.ix_events_namespace_created ON events (namespace_id, created_at_utc DESC, event_id DESC);
CREATE INDEX IF NOT EXISTS {{schema}}.ix_events_duration ON events (namespace_id, definition_id, created_at_utc) WHERE event_code IN (40, 41, 44) AND duration_ms IS NOT NULL;

-- jobs
CREATE TABLE IF NOT EXISTS {{schema}}.jobs (
    job_id integer PRIMARY KEY AUTOINCREMENT,
    job_ref text NOT NULL,
    job_kind_code integer NOT NULL,
    namespace_id integer NOT NULL,
    definition_id integer NOT NULL,
    lineage_root_job_id integer NULL,
    parent_job_id integer NULL,
    tenant_id integer NULL,
    schedule_id integer NULL,
    deduplication_key text NULL,
    correlation_key text NULL,
    concurrency_key text NULL,
    input_format_id integer NOT NULL,
    input blob NULL,
    event_recording_code integer NOT NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL
    , CONSTRAINT ck_jobs_input_pair CHECK ((input_format_id = 0 AND input IS NULL) OR (input_format_id <> 0 AND input IS NOT NULL))
    , CONSTRAINT ck_jobs_input_format_id_byte CHECK (input_format_id BETWEEN 0 AND 255)
) STRICT;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_jobs_parent ON jobs (parent_job_id) WHERE parent_job_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_jobs_namespace_created ON jobs (namespace_id, created_at_utc DESC, job_id DESC);
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_jobs_ref ON jobs (job_ref);
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_jobs_deduplication_key_root ON jobs (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_job_id IS NULL;
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_jobs_deduplication_key_child ON jobs (parent_job_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND parent_job_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_jobs_slot ON jobs (definition_id) WHERE job_kind_code = 20;

-- lanes
CREATE TABLE IF NOT EXISTS {{schema}}.lanes (
    lane_id integer PRIMARY KEY AUTOINCREMENT,
    namespace_id integer NOT NULL,
    lane_name text NOT NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL
) STRICT;
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_lanes_namespace_name ON lanes (namespace_id, lane_name);

-- jobs_state
CREATE TABLE IF NOT EXISTS {{schema}}.jobs_state (
    job_id integer NOT NULL,
    namespace_id integer NOT NULL,
    lane_id integer NULL,
    job_status_code integer NOT NULL,
    job_priority_code integer NOT NULL,
    next_run_at_utc integer NULL,
    execution_number integer NOT NULL,
    failure_count integer NOT NULL,
    leased_by_worker_id integer NULL,
    lease_expires_at_utc integer NULL,
    retention_until_utc integer NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    result_format_id integer DEFAULT 0 NOT NULL,
    result blob NULL,
    version integer DEFAULT 0 NOT NULL
    , CONSTRAINT pk_jobs_state PRIMARY KEY (job_id)
    , CONSTRAINT ck_jobs_state_blocked_lane CHECK (job_status_code <> 15 OR lane_id IS NOT NULL)
    , CONSTRAINT ck_jobs_state_lease_consistency CHECK ((leased_by_worker_id IS NULL AND lease_expires_at_utc IS NULL) OR (leased_by_worker_id IS NOT NULL AND lease_expires_at_utc IS NOT NULL))
    , CONSTRAINT ck_jobs_state_counters CHECK (execution_number >= 0 AND failure_count >= 0)
    , CONSTRAINT ck_jobs_state_status_lease CHECK (job_status_code = 50 OR leased_by_worker_id IS NULL)
    , CONSTRAINT ck_jobs_state_inflight_leased CHECK (job_status_code <> 50 OR leased_by_worker_id IS NOT NULL)
    , CONSTRAINT ck_jobs_state_ready_due CHECK (job_status_code <> 10 OR next_run_at_utc IS NOT NULL)
    , CONSTRAINT ck_jobs_state_idle CHECK (job_status_code <> 40 OR next_run_at_utc IS NULL)
    , CONSTRAINT ck_jobs_state_result_pair CHECK ((result_format_id = 0 AND result IS NULL) OR (result_format_id <> 0 AND result IS NOT NULL))
    , CONSTRAINT ck_jobs_state_result_idle CHECK (result_format_id = 0 OR job_status_code <> 50)
    , CONSTRAINT ck_jobs_state_result_format_id_byte CHECK (result_format_id BETWEEN 0 AND 255)
    , CONSTRAINT fk_jobs_state_lanes FOREIGN KEY (lane_id) REFERENCES lanes (lane_id)
) STRICT;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_jobs_state_claim_ready ON jobs_state (namespace_id, job_priority_code DESC, next_run_at_utc, job_id, job_status_code) WHERE job_status_code IN (10, 20) AND next_run_at_utc IS NOT NULL;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_jobs_state_worker_inflight ON jobs_state (leased_by_worker_id, job_id) WHERE leased_by_worker_id IS NOT NULL AND job_status_code = 50;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_jobs_state_lane ON jobs_state (lane_id, job_id) WHERE lane_id IS NOT NULL AND job_status_code IN (10, 15, 20, 30, 50);
CREATE INDEX IF NOT EXISTS {{schema}}.ix_jobs_state_failed ON jobs_state (namespace_id, job_id DESC) WHERE job_status_code = 200;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_jobs_state_lane_member ON jobs_state (lane_id) WHERE lane_id IS NOT NULL;

-- locks
CREATE TABLE IF NOT EXISTS {{schema}}.locks (
    lock_key text NOT NULL,
    job_id integer NOT NULL,
    expires_at_utc integer NOT NULL,
    hold_token text NOT NULL
    , CONSTRAINT pk_locks PRIMARY KEY (lock_key)
) STRICT;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_locks_reclaim_expired ON locks (expires_at_utc);

-- namespaces
CREATE TABLE IF NOT EXISTS {{schema}}.namespaces (
    namespace_id integer PRIMARY KEY AUTOINCREMENT,
    namespace_name text NOT NULL,
    owner_team text NULL,
    description text NULL,
    namespace_status_code integer NOT NULL,
    catalog_hash text NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    version integer DEFAULT 0 NOT NULL,
    recovery_due_at_utc integer NULL
) STRICT;
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_namespaces_name ON namespaces (namespace_name);

-- Seed: reserved system namespace (namespace_id=1, 'sys') for cross-namespace audit events. Collation-neutral.
INSERT OR IGNORE INTO {{schema}}.namespaces (namespace_id, namespace_name, namespace_status_code, description, created_at_utc, modified_at_utc)
VALUES (1, 'sys', 10, 'Reserved system namespace for cross-namespace audit events.', CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER), CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER));

-- calendars
CREATE TABLE IF NOT EXISTS {{schema}}.calendars (
    calendar_id integer PRIMARY KEY AUTOINCREMENT,
    namespace_id integer NOT NULL,
    calendar_name text NOT NULL,
    version integer NOT NULL,
    calendar_role_code integer NOT NULL,
    definition_format_id integer DEFAULT 0 NOT NULL,
    definition blob NOT NULL,
    content_hash text NOT NULL,
    calendar_status_code integer NOT NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    published_at_utc integer NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL
    , CONSTRAINT ck_calendars_version CHECK (version > 0)
    , CONSTRAINT ck_calendars_definition_format_id_byte CHECK (definition_format_id BETWEEN 0 AND 255)
    , CONSTRAINT fk_calendars_namespaces FOREIGN KEY (namespace_id) REFERENCES namespaces (namespace_id)
) STRICT;
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_calendars_namespace_name_version ON calendars (namespace_id, calendar_name, version);
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_calendars_current ON calendars (namespace_id, calendar_name) WHERE calendar_status_code = 20;

-- schedules
CREATE TABLE IF NOT EXISTS {{schema}}.schedules (
    schedule_id integer PRIMARY KEY AUTOINCREMENT,
    namespace_id integer NOT NULL,
    job_id integer NULL,
    definition_id integer NOT NULL,
    schedule_name text NOT NULL,
    schedule_origin_code integer NOT NULL,
    expression text NOT NULL,
    time_zone_id text NOT NULL,
    expression_kind_code integer NOT NULL,
    misfire_strategy_code integer NOT NULL,
    interval_anchor_at_utc integer NULL,
    misfire_grace_seconds integer NOT NULL,
    timing_fingerprint text NOT NULL,
    max_outstanding integer NOT NULL,
    full_behavior_code integer NOT NULL,
    expire_after_seconds integer NULL,
    expire_action_code integer NULL,
    availability_calendar_name text NULL,
    schedule_status_code integer NOT NULL,
    paused_until_utc integer NULL,
    description text NULL,
    pause_message text NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    version integer DEFAULT 0 NOT NULL
    , CONSTRAINT ck_schedules_paused_pair CHECK (schedule_status_code = 30 OR paused_until_utc IS NULL)
    , CONSTRAINT ck_schedules_orphan_origin CHECK (schedule_origin_code = 40 OR schedule_status_code <> 230)
    , CONSTRAINT ck_schedules_max_outstanding CHECK (max_outstanding >= 1)
    , CONSTRAINT ck_schedules_expire_pair CHECK ((expire_after_seconds IS NULL AND expire_action_code IS NULL) OR (expire_after_seconds IS NOT NULL AND expire_action_code IS NOT NULL))
    , CONSTRAINT ck_schedules_expire_after CHECK (expire_after_seconds IS NULL OR expire_after_seconds > 0)
    , CONSTRAINT fk_schedules_definitions FOREIGN KEY (definition_id) REFERENCES definitions (definition_id)
) STRICT;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_schedules_job ON schedules (job_id, schedule_id) WHERE job_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_schedules_definition_name ON schedules (definition_id, schedule_name);

-- schedules_state
CREATE TABLE IF NOT EXISTS {{schema}}.schedules_state (
    schedule_id integer NOT NULL,
    namespace_id integer NOT NULL,
    next_occurrence_at_utc integer NULL,
    next_evaluation_at_utc integer NULL,
    evaluation_status_code integer NOT NULL,
    outstanding_count integer DEFAULT 0 NOT NULL,
    version integer DEFAULT 0 NOT NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL
    , CONSTRAINT pk_schedules_state PRIMARY KEY (schedule_id)
    , CONSTRAINT ck_schedules_state_outstanding_count CHECK (outstanding_count >= 0)
) STRICT;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_schedules_state_namespace_next ON schedules_state (namespace_id, next_occurrence_at_utc, schedule_id) WHERE next_occurrence_at_utc IS NOT NULL;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_schedules_state_due ON schedules_state (namespace_id, next_evaluation_at_utc, schedule_id) WHERE next_evaluation_at_utc IS NOT NULL;

-- steps
CREATE TABLE IF NOT EXISTS {{schema}}.steps (
    job_id integer NOT NULL,
    step_name text NOT NULL,
    step_status_code integer NOT NULL,
    attempt_number integer NOT NULL,
    next_retry_at_utc integer NULL,
    failure_code integer NULL,
    failure_message text NULL,
    result_format_id integer NOT NULL,
    result blob NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    version integer DEFAULT 0 NOT NULL
    , CONSTRAINT pk_steps PRIMARY KEY (job_id, step_name)
    , CONSTRAINT ck_steps_result_pair CHECK ((result_format_id = 0 AND result IS NULL) OR (result_format_id <> 0 AND result IS NOT NULL))
    , CONSTRAINT ck_steps_attempt_number CHECK (attempt_number >= 1)
    , CONSTRAINT ck_steps_terminal_no_retry CHECK (step_status_code NOT IN (100, 200, 230) OR next_retry_at_utc IS NULL)
    , CONSTRAINT ck_steps_result_succeeded CHECK (result_format_id = 0 OR step_status_code = 100)
    , CONSTRAINT ck_steps_failure_pair CHECK (failure_message IS NULL OR failure_code IS NOT NULL)
    , CONSTRAINT ck_steps_result_format_id_byte CHECK (result_format_id BETWEEN 0 AND 255)
) STRICT;

-- tags
CREATE TABLE IF NOT EXISTS {{schema}}.tags (
    entity_code integer NOT NULL,
    entity_id integer NOT NULL,
    tag_name text NOT NULL,
    tag_value text NULL,
    tag_value_search text NULL
    , CONSTRAINT pk_tags PRIMARY KEY (entity_code, entity_id, tag_name)
    , CONSTRAINT ck_tags_entity_id CHECK (entity_id > 0)
    , CONSTRAINT ck_tags_value_search CHECK ((tag_value IS NULL AND tag_value_search IS NULL) OR (tag_value IS NOT NULL AND tag_value_search IS NOT NULL))
) STRICT;

-- tenants
CREATE TABLE IF NOT EXISTS {{schema}}.tenants (
    tenant_id integer PRIMARY KEY AUTOINCREMENT,
    tenant_name text NOT NULL,
    tenant_status_code integer NOT NULL,
    display_name text NULL,
    description text NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    version integer DEFAULT 0 NOT NULL
) STRICT;
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_tenants_name ON tenants (tenant_name);

-- workers
CREATE TABLE IF NOT EXISTS {{schema}}.workers (
    worker_id integer PRIMARY KEY AUTOINCREMENT,
    worker_ref text NOT NULL,
    namespace_id integer NOT NULL,
    worker_status_code integer NOT NULL,
    deployment_version text NOT NULL,
    host_name text NOT NULL,
    engine_version text NULL,
    dotnet_version text NULL,
    process_id integer NULL,
    max_concurrent_executors integer NOT NULL,
    last_seen_at_utc integer NOT NULL,
    created_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    modified_at_utc integer DEFAULT (CAST(unixepoch('now', 'subsec') * 1000 AS INTEGER)) NOT NULL,
    version integer DEFAULT 0 NOT NULL
    , CONSTRAINT ck_workers_max_concurrent_executors CHECK (max_concurrent_executors > 0)
    , CONSTRAINT fk_workers_namespaces FOREIGN KEY (namespace_id) REFERENCES namespaces (namespace_id)
) STRICT;
CREATE INDEX IF NOT EXISTS {{schema}}.ix_workers_namespace_status_last_seen ON workers (namespace_id, worker_status_code, last_seen_at_utc);
CREATE INDEX IF NOT EXISTS {{schema}}.ix_workers_status_last_seen ON workers (worker_status_code, last_seen_at_utc);
CREATE INDEX IF NOT EXISTS {{schema}}.ix_workers_namespace_last_seen ON workers (namespace_id, last_seen_at_utc DESC, worker_id DESC);
CREATE UNIQUE INDEX IF NOT EXISTS {{schema}}.ux_workers_ref ON workers (worker_ref);

DROP VIEW IF EXISTS {{schema}}.alerts_view;
CREATE VIEW {{schema}}.alerts_view AS
SELECT
    a.alert_id,
    a.alert_ref,
    ns.namespace_name AS namespace,
    a.job_id,
    a.job_ref,
    j.definition_id,
    d.definition_name AS job_name,
    CASE a.alert_origin_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'automatic' WHEN 20 THEN 'manual' END AS origin,
    a.alert_origin_code,
    CASE a.alert_severity_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'info' WHEN 20 THEN 'warning' WHEN 30 THEN 'error' WHEN 40 THEN 'critical' END AS severity,
    a.alert_severity_code,
    CASE a.alert_kind_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'first-failure' WHEN 20 THEN 'threshold-reached' WHEN 30 THEN 'final-failure' WHEN 40 THEN 'manual' END AS kind,
    a.alert_kind_code,
    CASE a.delivery_status_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'pending' WHEN 20 THEN 'retry-after' WHEN 30 THEN 'suppressed' WHEN 100 THEN 'delivered' WHEN 200 THEN 'failed' END AS delivery_status,
    a.delivery_status_code,
    a.title,
    a.alert_message,
    a.channel_name,
    a.deduplication_key,
    a.occurrence_count,
    a.resolved_at_utc,
    a.acknowledged_at_utc,
    a.retry_count,
    a.retry_after_utc,
    a.created_at_utc,
    a.modified_at_utc,
    a.version
FROM {{schema}}.alerts AS a
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = a.namespace_id
LEFT JOIN {{schema}}.jobs AS j ON j.job_id = a.job_id
LEFT JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id;

DROP VIEW IF EXISTS {{schema}}.calendars_view;
CREATE VIEW {{schema}}.calendars_view AS
SELECT
    c.calendar_id,
    ns.namespace_name AS namespace,
    c.calendar_name,
    c.version,
    CASE c.calendar_role_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'occurrence' WHEN 20 THEN 'availability' END AS role,
    c.calendar_role_code,
    CASE c.calendar_status_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'draft' WHEN 20 THEN 'published' WHEN 30 THEN 'superseded' WHEN 240 THEN 'archived' END AS status,
    c.calendar_status_code,
    c.content_hash,
    c.created_at_utc,
    c.published_at_utc,
    c.modified_at_utc
FROM {{schema}}.calendars AS c
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = c.namespace_id;

DROP VIEW IF EXISTS {{schema}}.checkpoints_view;
CREATE VIEW {{schema}}.checkpoints_view AS
SELECT
    c.job_id,
    j.job_ref,
    ns.namespace_name AS namespace,
    d.definition_name AS job_name,
    c.checkpoint_name,
    CASE c.checkpoint_kind_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'variable' WHEN 20 THEN 'signal' WHEN 30 THEN 'timer' WHEN 40 THEN 'progress' WHEN 50 THEN 'child-latch' WHEN 60 THEN 'firing' END AS kind,
    c.checkpoint_kind_code,
    CASE c.checkpoint_status_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'pending' WHEN 20 THEN 'set' WHEN 30 THEN 'expired' WHEN 100 THEN 'consumed' WHEN 110 THEN 'succeeded' WHEN 120 THEN 'failed' WHEN 130 THEN 'cancelled' END AS status,
    c.checkpoint_status_code,
    c.due_at_utc,
    CASE c.value_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE 'custom-' || CAST(c.value_format_id AS TEXT)
    END AS value_format,
    CASE WHEN c.value_format_id = 1 /* JobPayloadFormat.Json */ THEN CAST(c.value AS TEXT) END AS value_text,
    c.created_at_utc,
    c.modified_at_utc,
    c.version
FROM {{schema}}.checkpoints AS c
JOIN {{schema}}.jobs AS j ON j.job_id = c.job_id
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = j.namespace_id
JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id;

DROP VIEW IF EXISTS {{schema}}.definitions_view;
CREATE VIEW {{schema}}.definitions_view AS
SELECT
    d.definition_id,
    ns.namespace_name AS namespace,
    d.definition_name,
    CASE d.definition_status_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'active' WHEN 240 THEN 'retired' END AS status,
    d.definition_status_code,
    d.definition_hash,
    d.manifest_generation_at_utc,
    d.input_type_name,
    d.input_format_name,
    d.output_type_name,
    d.output_format_name,
    CASE d.job_priority_code_effective WHEN 0 THEN 'bulk' WHEN 50 THEN 'normal' WHEN 70 THEN 'high' WHEN 85 THEN 'critical' WHEN 100 THEN 'realtime' END AS priority,
    d.job_priority_code,
    d.job_priority_code_override,
    d.job_priority_code_effective,
    d.max_attempts,
    d.max_attempts_override,
    d.max_attempts_effective,
    d.concurrency_limit,
    d.concurrency_limit_override,
    d.concurrency_limit_effective,
    d.rate_limit,
    d.rate_limit_override,
    d.rate_limit_effective,
    d.rate_name,
    d.backoff,
    d.backoff_override,
    d.backoff_effective,
    d.execution_timeout_seconds,
    d.execution_timeout_seconds_override,
    d.execution_timeout_seconds_effective,
    d.deadline_seconds,
    d.deadline_seconds_override,
    d.deadline_seconds_effective,
    CASE d.deadline_behavior_code_effective WHEN 0 THEN 'unspecified' WHEN 10 THEN 'strict' WHEN 20 THEN 'advisory' END AS deadline_behavior,
    d.deadline_behavior_code,
    d.deadline_behavior_code_override,
    d.deadline_behavior_code_effective,
    d.retention_seconds,
    d.retention_seconds_override,
    d.retention_seconds_effective,
    CASE d.event_recording_code_effective WHEN 0 THEN 'off' WHEN 10 THEN 'failures' WHEN 20 THEN 'audit' END AS event_recording,
    d.event_recording_code,
    d.event_recording_code_override,
    d.event_recording_code_effective,
    CASE d.alert_profile_code_effective WHEN 0 THEN 'none' WHEN 10 THEN 'on-failure' WHEN 20 THEN 'info' WHEN 30 THEN 'on-terminal' WHEN 40 THEN 'sys-critical' END AS alert_profile,
    d.alert_profile_code,
    d.alert_profile_code_override,
    d.alert_profile_code_effective,
    CASE d.firing_failure_policy_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'continue' WHEN 20 THEN 'pause-scheduling' END AS firing_failure_policy,
    d.firing_failure_policy_code,
    CASE d.scheduled_delivery_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'permanent' WHEN 20 THEN 'per-occurrence' END AS scheduled_delivery,
    d.scheduled_delivery_code,
    d.alert_channel_name,
    d.alert_channel_name_override,
    d.alert_channel_name_effective,
    d.runbook_url,
    d.runbook_url_override,
    d.runbook_url_effective,
    d.created_at_utc,
    d.modified_at_utc,
    d.version
FROM {{schema}}.definitions AS d
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = d.namespace_id;

DROP VIEW IF EXISTS {{schema}}.events_view;
CREATE VIEW {{schema}}.events_view AS
SELECT
    e.event_id,
    e.created_at_utc,
    ns.namespace_name AS namespace,
    e.namespace_id,
    CASE e.entity_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'alert' WHEN 15 THEN 'calendar' WHEN 20 THEN 'checkpoint' WHEN 30 THEN 'definition' WHEN 40 THEN 'event' WHEN 50 THEN 'job' WHEN 60 THEN 'lane' WHEN 70 THEN 'lock' WHEN 80 THEN 'namespace' WHEN 90 THEN 'schedule' WHEN 100 THEN 'step' WHEN 110 THEN 'tag' WHEN 120 THEN 'tenant' WHEN 130 THEN 'worker' END AS entity_kind,
    e.entity_code,
    e.entity_id,
    CASE e.event_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'tenant.suspended' WHEN 11 THEN 'tenant.resumed' WHEN 12 THEN 'tenant.updated' WHEN 20 THEN 'namespace.suspended' WHEN 21 THEN 'namespace.resumed' WHEN 22 THEN 'namespace.updated' WHEN 30 THEN 'definition.overrides-updated' WHEN 31 THEN 'definition.retired' WHEN 40 THEN 'job.execution-succeeded' WHEN 41 THEN 'job.execution-failed' WHEN 42 THEN 'job.execution-orphaned' WHEN 43 THEN 'job.execution-yielded' WHEN 44 THEN 'job.failed' WHEN 60 THEN 'job.suspended' WHEN 61 THEN 'job.rescheduled' WHEN 70 THEN 'job.cancelled' WHEN 71 THEN 'job.paused' WHEN 72 THEN 'job.resumed' WHEN 73 THEN 'job.restarted' WHEN 74 THEN 'job.reprioritized' WHEN 75 THEN 'job.purged' WHEN 76 THEN 'job.input-amended' WHEN 80 THEN 'job.signal-raised' WHEN 81 THEN 'job.state-reset' WHEN 90 THEN 'job.note-recorded' WHEN 100 THEN 'schedule.paused' WHEN 101 THEN 'schedule.resumed' WHEN 102 THEN 'schedule.pause-expired' WHEN 104 THEN 'schedule.triggered' WHEN 105 THEN 'schedule.admitted' WHEN 106 THEN 'schedule.skipped' WHEN 107 THEN 'schedule.retimed' WHEN 110 THEN 'calendar.draft-saved' WHEN 111 THEN 'calendar.published' WHEN 112 THEN 'calendar.archived' WHEN 120 THEN 'worker.started' WHEN 121 THEN 'worker.stopped' WHEN 122 THEN 'worker.died' WHEN 140 THEN 'alert.acknowledged' WHEN 141 THEN 'alert.resolved' WHEN 180 THEN 'outbox.requeued' WHEN 181 THEN 'outbox.discarded' END AS event,
    e.event_code,
    e.definition_id,
    d.definition_name AS job_name,
    e.tenant_id,
    t.tenant_name,
    e.lineage_root_job_id,
    root.job_ref AS lineage_root_job_ref,
    e.worker_id,
    wkr.worker_ref,
    e.execution_number,
    e.duration_ms,
    e.event_message,
    CASE e.details_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE 'custom-' || CAST(e.details_format_id AS TEXT)
    END AS details_format,
    CASE WHEN e.details_format_id = 1 /* JobPayloadFormat.Json */ THEN CAST(e.details AS TEXT) END AS details_text
FROM {{schema}}.events AS e
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = e.namespace_id
LEFT JOIN {{schema}}.definitions AS d ON d.definition_id = e.definition_id
LEFT JOIN {{schema}}.tenants AS t
    ON t.tenant_id = COALESCE(e.tenant_id, CASE WHEN e.entity_code = 120 /* EntityCode.Tenant */ THEN e.entity_id END)
LEFT JOIN {{schema}}.jobs AS root ON root.job_id = e.lineage_root_job_id
LEFT JOIN {{schema}}.workers AS wkr ON wkr.worker_id = e.worker_id;

DROP VIEW IF EXISTS {{schema}}.jobs_view;
CREATE VIEW {{schema}}.jobs_view AS
SELECT
    j.job_id,
    j.job_ref,
    ns.namespace_name AS namespace,
    d.definition_name AS job_name,
    CASE j.job_kind_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'ordinary' WHEN 20 THEN 'permanent-slot' END AS job_kind,
    j.job_kind_code,
    CASE r.job_status_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'ready' WHEN 15 THEN 'blocked' WHEN 20 THEN 'suspended' WHEN 30 THEN 'paused' WHEN 40 THEN 'idle' WHEN 50 THEN 'executing' WHEN 100 THEN 'succeeded' WHEN 200 THEN 'failed' WHEN 220 THEN 'cancelled' END AS status,
    r.job_status_code,
    j.deduplication_key,
    j.correlation_key,
    j.concurrency_key,
    j.parent_job_id,
    COALESCE(j.lineage_root_job_id, j.job_id) AS lineage_root_job_id,
    j.schedule_id,
    root.job_ref AS lineage_root_job_ref,
    j.tenant_id,
    t.tenant_name,
    CASE r.job_priority_code WHEN 0 THEN 'bulk' WHEN 50 THEN 'normal' WHEN 70 THEN 'high' WHEN 85 THEN 'critical' WHEN 100 THEN 'realtime' END AS priority,
    r.job_priority_code,
    CASE j.input_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE 'custom-' || CAST(j.input_format_id AS TEXT)
    END AS input_format,
    CASE WHEN j.input_format_id = 1 /* JobPayloadFormat.Json */ THEN CAST(j.input AS TEXT) END AS input_text,
    CASE r.result_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE 'custom-' || CAST(r.result_format_id AS TEXT)
    END AS result_format,
    CASE WHEN r.result_format_id = 1 /* JobPayloadFormat.Json */ THEN CAST(r.result AS TEXT) END AS result_text,
    r.next_run_at_utc,
    r.execution_number,
    r.failure_count,
    r.leased_by_worker_id,
    w.host_name AS leased_by_worker_host_name,
    r.lease_expires_at_utc,
    r.retention_until_utc,
    j.created_at_utc,
    r.modified_at_utc,
    r.version,
    lane.lane_name AS lane
FROM {{schema}}.jobs AS j
JOIN {{schema}}.jobs_state AS r ON r.job_id = j.job_id
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = j.namespace_id
JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id
LEFT JOIN {{schema}}.jobs AS root ON root.job_id = COALESCE(j.lineage_root_job_id, j.job_id)
LEFT JOIN {{schema}}.tenants AS t ON t.tenant_id = j.tenant_id
LEFT JOIN {{schema}}.workers AS w ON w.worker_id = r.leased_by_worker_id
LEFT JOIN {{schema}}.lanes AS lane ON lane.lane_id = r.lane_id;

DROP VIEW IF EXISTS {{schema}}.schedules_view;
CREATE VIEW {{schema}}.schedules_view AS
SELECT
    s.schedule_id,
    ns.namespace_name AS namespace,
    s.job_id,
    j.job_ref,
    s.definition_id,
    d.definition_name AS job_name,
    s.schedule_name,
    CASE s.schedule_origin_code WHEN 0 THEN 'unspecified' WHEN 20 THEN 'operator' WHEN 40 THEN 'definition' END AS origin,
    s.schedule_origin_code,
    CASE s.schedule_status_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'active' WHEN 30 THEN 'paused' WHEN 230 THEN 'orphaned' END AS status,
    s.schedule_status_code,
    s.expression,
    s.time_zone_id,
    CASE s.expression_kind_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'cron' WHEN 20 THEN 'interval' WHEN 30 THEN 'calendar' END AS expression_kind,
    s.expression_kind_code,
    CASE s.misfire_strategy_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'catch-up-once' WHEN 20 THEN 'skip' WHEN 30 THEN 'catch-up-latest' WHEN 40 THEN 'catch-up-all' END AS misfire_strategy,
    s.misfire_strategy_code,
    st.next_occurrence_at_utc,
    st.next_evaluation_at_utc,
    CASE st.evaluation_status_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'scheduled' WHEN 20 THEN 'waiting-for-capacity' WHEN 30 THEN 'exhausted' WHEN 40 THEN 'evaluation-error' WHEN 50 THEN 'slot-unavailable' WHEN 60 THEN 'resumed' END AS evaluation_status,
    st.evaluation_status_code,
    CASE d.scheduled_delivery_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'permanent' WHEN 20 THEN 'per-occurrence' END AS scheduled_delivery,
    d.scheduled_delivery_code,
    s.max_outstanding,
    CASE s.full_behavior_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'wait' WHEN 20 THEN 'drop' END AS full_behavior,
    s.full_behavior_code,
    s.expire_after_seconds,
    CASE s.expire_action_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'discard' WHEN 20 THEN 'pause' END AS expire_action,
    s.expire_action_code,
    s.availability_calendar_name,
    st.outstanding_count,
    s.paused_until_utc,
    s.description,
    s.pause_message,
    s.created_at_utc,
    s.modified_at_utc,
    s.version
FROM {{schema}}.schedules AS s
JOIN {{schema}}.schedules_state AS st ON st.schedule_id = s.schedule_id
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = s.namespace_id
JOIN {{schema}}.definitions AS d ON d.definition_id = s.definition_id
LEFT JOIN {{schema}}.jobs AS j ON j.job_id = s.job_id;

DROP VIEW IF EXISTS {{schema}}.steps_view;
CREATE VIEW {{schema}}.steps_view AS
SELECT
    s.job_id,
    j.job_ref,
    ns.namespace_name AS namespace,
    d.definition_name AS job_name,
    s.step_name,
    CASE s.step_status_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'pending' WHEN 100 THEN 'succeeded' WHEN 200 THEN 'exhausted' WHEN 230 THEN 'interrupted' END AS status,
    s.step_status_code,
    s.attempt_number,
    s.next_retry_at_utc,
    CASE s.failure_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'unhandled-exception' WHEN 20 THEN 'interrupted' END AS failure,
    s.failure_code,
    s.failure_message,
    CASE s.result_format_id
        WHEN 0 /* JobPayloadFormat.None */ THEN 'none'
        WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
        WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
        ELSE 'custom-' || CAST(s.result_format_id AS TEXT)
    END AS result_format,
    CASE WHEN s.result_format_id = 1 /* JobPayloadFormat.Json */ THEN CAST(s.result AS TEXT) END AS result_text,
    s.created_at_utc,
    s.modified_at_utc,
    s.version
FROM {{schema}}.steps AS s
JOIN {{schema}}.jobs AS j ON j.job_id = s.job_id
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = j.namespace_id
JOIN {{schema}}.definitions AS d ON d.definition_id = j.definition_id;

DROP VIEW IF EXISTS {{schema}}.tags_view;
CREATE VIEW {{schema}}.tags_view AS
SELECT
    CASE t.entity_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'alert' WHEN 15 THEN 'calendar' WHEN 20 THEN 'checkpoint' WHEN 30 THEN 'definition' WHEN 40 THEN 'event' WHEN 50 THEN 'job' WHEN 60 THEN 'lane' WHEN 70 THEN 'lock' WHEN 80 THEN 'namespace' WHEN 90 THEN 'schedule' WHEN 100 THEN 'step' WHEN 110 THEN 'tag' WHEN 120 THEN 'tenant' WHEN 130 THEN 'worker' END AS entity_kind,
    t.entity_code,
    t.entity_id,
    t.tag_name,
    t.tag_value,
    t.tag_value_search
FROM {{schema}}.tags AS t;

DROP VIEW IF EXISTS {{schema}}.workers_view;
CREATE VIEW {{schema}}.workers_view AS
SELECT
    w.worker_id,
    w.worker_ref,
    ns.namespace_name AS namespace,
    CASE w.worker_status_code WHEN 0 THEN 'unspecified' WHEN 10 THEN 'active' WHEN 80 THEN 'draining' WHEN 100 THEN 'stopped' WHEN 200 THEN 'dead' END AS status,
    w.worker_status_code,
    w.deployment_version,
    w.host_name,
    w.engine_version,
    w.dotnet_version,
    w.process_id,
    w.max_concurrent_executors,
    w.last_seen_at_utc,
    w.created_at_utc,
    w.modified_at_utc,
    w.version
FROM {{schema}}.workers AS w
JOIN {{schema}}.namespaces AS ns ON ns.namespace_id = w.namespace_id;


INSERT INTO {{schema}}.migrations (name)
VALUES ('M20261030172834_init')
ON CONFLICT (name) DO NOTHING;
