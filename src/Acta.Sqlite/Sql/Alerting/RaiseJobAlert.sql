-- The unknown-jobId guard lives in the job_ref CASE below, not a preceding statement: ExecuteScalarAsync
-- reads only the first result set with columns, and a standalone guard SELECT would itself become that
-- (empty on the common path) result set.
INSERT INTO {{schema}}.alerts (
    namespace_id, job_id, job_ref,
    origin_code, severity_code, kind_code, title, message, channel_name,
    dedupe_key, dedupe_window_start_utc, occurrence_count,
    delivery_status_code, retry_count
)
SELECT
    ns.id,
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
    modified_at_utc = {{now}},
    version = {{schema}}.alerts.version + 1
RETURNING occurrence_count;
