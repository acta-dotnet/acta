-- Its own leading statement (the EnqueueOne guard-stack shape), because the insert below is filtered
-- by the ghost guard: a raise whose row that guard removes never evaluates its SELECT list, so an
-- unknown job would pass silently where the other two dialects reject it before any guard runs.
SELECT ACTA_ERROR('ACTA:ALERT_UNKNOWN_JOB:raise_job_alert: unknown job id')
WHERE
    @p_job_id IS NOT NULL
    AND NOT EXISTS (SELECT 1 FROM {{schema}}.jobs j WHERE j.id = @p_job_id);

INSERT INTO {{schema}}.alerts (
    namespace_id, alert_ref, job_id, job_ref,
    origin_code, severity_code, kind_code, title, message, channel_name,
    dedupe_key, occurrence_count, last_projected_event_id,
    delivery_status_code, retry_count
)
SELECT
    ns.id,
    @p_alert_ref,
    @p_job_id,
    jr.job_ref,
    @p_origin_code,
    @p_severity_code,
    @p_kind_code,
    @p_title,
    @p_message,
    @p_channel_name,
    @p_dedupe_key,
    1,
    @p_source_event_id,
    @p_delivery_status_code,
    0
FROM {{schema}}.namespaces ns
LEFT JOIN {{schema}}.jobs jr ON jr.id = @p_job_id
WHERE
    ns.name = @p_namespace_name
    -- Ghost guard: a failure replayed after its incident closed must not open one behind the events
    -- this identity has already absorbed - which is why it compares against every row of the identity,
    -- not just an open one. NULL, hence never blocking, for a raise carrying no event.
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.alerts g
        WHERE
            g.namespace_id = ns.id
            AND g.dedupe_key = @p_dedupe_key
            AND g.last_projected_event_id >= @p_source_event_id
    )
-- One OPEN row per (namespace_id, dedupe_key) - the incident. The conflict target is the filtered
-- unique index, so a resolved row is invisible to it and the next failure opens a fresh incident
-- instead of re-opening the closed one.
ON CONFLICT (namespace_id, dedupe_key) WHERE dedupe_key IS NOT NULL AND resolved_at_utc IS NULL
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
    last_projected_event_id = COALESCE(excluded.last_projected_event_id, {{schema}}.alerts.last_projected_event_id),
    modified_at_utc = {{now}},
    version = {{schema}}.alerts.version + 1
WHERE
    @p_source_event_id IS NULL
    OR {{schema}}.alerts.last_projected_event_id IS NULL
    OR @p_source_event_id > {{schema}}.alerts.last_projected_event_id;

-- Read back rather than RETURNed, and unconditionally: SQLite has no procedural branch and the caller
-- takes this file's LAST result set, so a read producing rows only when the write was held back would
-- leave the applied path with nothing to return. The two arms below are mutually exclusive.
SELECT a.occurrence_count, a.last_projected_event_id
FROM {{schema}}.alerts a
WHERE
    (@p_dedupe_key IS NULL AND a.alert_ref = @p_alert_ref) -- keyless: the row just minted
    OR (
        @p_dedupe_key IS NOT NULL
        -- MAX(id), not ORDER BY id DESC LIMIT 1: both name the identity's newest row - the open
        -- incident, or the last-resolved one when a guard held the write back - but a top-level OR is
        -- served by a multi-index union that cannot also satisfy an ORDER BY, so sorting cost a b-tree.
        AND a.id = (
            SELECT MAX(b.id)
            FROM {{schema}}.alerts b
            WHERE
                b.namespace_id = (SELECT ns.id FROM {{schema}}.namespaces ns WHERE ns.name = @p_namespace_name)
                AND b.dedupe_key = @p_dedupe_key
        )
    );
