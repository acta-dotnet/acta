-- The unknown-jobId guard lives in the job_ref CASE below, not a preceding statement: this file's
-- outcome is read from its LAST result set, and a standalone guard SELECT would displace it.
INSERT INTO {{schema}}.alerts (
    namespace_id, alert_ref, job_id, job_ref,
    origin_code, severity_code, kind_code, title, message, channel_name,
    dedupe_key, dedupe_window_start_utc, occurrence_count, last_projected_event_id,
    delivery_status_code, retry_count
)
SELECT
    ns.id,
    @p_alert_ref,
    @p_job_id,
    CASE
        WHEN @p_job_id IS NOT NULL AND jr.job_ref IS NULL
            THEN ACTA_ERROR('ACTA:ALERT_UNKNOWN_JOB:raise_job_alert: unknown job id')
        ELSE jr.job_ref
    END,
    @p_origin_code,
    @p_severity_code,
    @p_kind_code,
    @p_title,
    @p_message,
    @p_channel_name,
    @p_dedupe_key,
    @p_dedupe_window_start_utc,
    1,
    @p_source_event_id,
    @p_delivery_status_code,
    0
FROM {{schema}}.namespaces ns
LEFT JOIN {{schema}}.jobs jr ON jr.id = @p_job_id
WHERE ns.name = @p_namespace_name
ON CONFLICT (namespace_id, dedupe_key, dedupe_window_start_utc) WHERE dedupe_key IS NOT NULL
DO UPDATE SET
    job_id = excluded.job_id,
    job_ref = excluded.job_ref,
    origin_code = excluded.origin_code,
    severity_code = excluded.severity_code,
    kind_code = excluded.kind_code,
    title = excluded.title,
    message = excluded.message,
    channel_name = excluded.channel_name,
    occurrence_count = {{schema}}.alerts.occurrence_count + 1,
    resolved_at_utc = NULL,
    last_projected_event_id = COALESCE(excluded.last_projected_event_id, {{schema}}.alerts.last_projected_event_id),
    modified_at_utc = {{now}},
    version = {{schema}}.alerts.version + 1
WHERE
    @p_source_event_id IS NULL
    OR {{schema}}.alerts.last_projected_event_id IS NULL
    OR @p_source_event_id > {{schema}}.alerts.last_projected_event_id;

-- Read back rather than RETURNed: a replay the guard holds back writes nothing, so RETURNING yields no
-- row while the caller's failure threshold still needs the stored count. One row either way - the
-- insert arm by the ref it just minted, the conflict arm by its dedupe coordinates, both one row.
SELECT a.occurrence_count
FROM {{schema}}.alerts a
WHERE
    a.alert_ref = @p_alert_ref
    OR (
        @p_dedupe_key IS NOT NULL
        AND a.namespace_id = (SELECT ns.id FROM {{schema}}.namespaces ns WHERE ns.name = @p_namespace_name)
        AND a.dedupe_key = @p_dedupe_key
        AND a.dedupe_window_start_utc = @p_dedupe_window_start_utc
    );
