-- One batch, one implicit transaction; only the final SELECT returns rows. The job row is locked before any of
-- its alert rows, the order purge_job takes with FOR UPDATE (docs/internals/sql-execution-policy.md, "Alert lock
-- order"), so a raise waits out a purge of its job. FOR SHARE, not FOR KEY SHARE: see that section.
DROP TABLE IF EXISTS pg_temp._raise_alert_job;
DROP TABLE IF EXISTS pg_temp._raise_alert_check;
CREATE TEMP TABLE _raise_alert_job (job_ref UUID NOT NULL) ON COMMIT DROP;
CREATE TEMP TABLE _raise_alert_check (unknown_namespace BOOLEAN NOT NULL, unknown_job BOOLEAN NOT NULL) ON COMMIT DROP;

INSERT INTO _raise_alert_job (job_ref)
SELECT j.job_ref
FROM {{schema}}.jobs j
WHERE j.id = @p_job_id
FOR SHARE;

-- An unknown namespace or job is refused before anything is written; the anonymous block only raises.
INSERT INTO _raise_alert_check (unknown_namespace, unknown_job)
SELECT
    NOT EXISTS (SELECT 1 FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name),
    @p_job_id::BIGINT IS NOT NULL AND NOT EXISTS (SELECT 1 FROM _raise_alert_job);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_temp._raise_alert_check WHERE unknown_namespace) THEN
        RAISE EXCEPTION 'raise_alert: unknown namespace';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_temp._raise_alert_check WHERE unknown_job) THEN
        RAISE EXCEPTION 'ACTA:ALERT_UNKNOWN_JOB:raise_alert: unknown job id' USING ERRCODE = 'P0001';
    END IF;
END
$$;

INSERT INTO {{schema}}.alerts (
    namespace_id,
    alert_ref,
    job_id,
    job_ref,
    origin_code,
    severity_code,
    kind_code,
    title,
    message,
    channel_name,
    deduplication_key,
    occurrence_count,
    last_projected_event_id,
    delivery_status_code,
    retry_count,
    created_at_utc,
    modified_at_utc,
    version)
SELECT
    ns.id,
    @p_alert_ref,
    @p_job_id,
    (SELECT jr.job_ref FROM _raise_alert_job jr),
    @p_origin_code,
    @p_severity_code,
    @p_kind_code,
    @p_title,
    @p_message,
    @p_channel_name,
    @p_deduplication_key,
    1,
    @p_source_event_id,
    @p_delivery_status_code,
    0,
    now(),
    now(),
    0
FROM {{schema}}.namespaces ns
WHERE
    ns.name = @p_namespace_name
    -- Ghost guard: a failure replayed after its incident closed must not open one behind the events
    -- this identity has already absorbed - which is why it compares against every row of the identity,
    -- not just an open one. NULL, hence never blocking, for a raise carrying no event or no key.
    AND NOT EXISTS (
        SELECT 1
        FROM {{schema}}.alerts g
        WHERE
            g.namespace_id = ns.id
            AND g.deduplication_key = @p_deduplication_key
            AND g.last_projected_event_id >= @p_source_event_id
    )
-- One OPEN row per (namespace_id, deduplication_key) - the incident. The conflict target is the filtered unique
-- index, so a resolved row is invisible to it and the next failure opens a fresh incident instead of re-opening
-- the closed one; a keyless raise never conflicts.
ON CONFLICT (namespace_id, deduplication_key) WHERE deduplication_key IS NOT NULL AND resolved_at_utc IS NULL
DO UPDATE SET
    job_id = EXCLUDED.job_id,
    job_ref = EXCLUDED.job_ref,
    origin_code = EXCLUDED.origin_code,
    severity_code = EXCLUDED.severity_code,
    kind_code = EXCLUDED.kind_code,
    title = EXCLUDED.title,
    message = EXCLUDED.message,
    channel_name = EXCLUDED.channel_name,
    occurrence_count = {{schema}}.alerts.occurrence_count + 1,
    last_projected_event_id = COALESCE(EXCLUDED.last_projected_event_id, {{schema}}.alerts.last_projected_event_id),
    modified_at_utc = now(),
    version = {{schema}}.alerts.version + 1
WHERE
    @p_source_event_id::BIGINT IS NULL
    OR {{schema}}.alerts.last_projected_event_id IS NULL
    OR @p_source_event_id > {{schema}}.alerts.last_projected_event_id;

-- Read back rather than RETURNed: the identity's newest row is the open incident, or the last-resolved one when
-- a guard held the write back, so its count and mark let the caller re-assert only the escalation this event
-- earned. A keyless raise reads the row it just minted.
SELECT a.occurrence_count, a.last_projected_event_id
FROM {{schema}}.alerts a
WHERE
    (@p_deduplication_key::VARCHAR IS NULL AND a.alert_ref = @p_alert_ref)
    OR (
        @p_deduplication_key::VARCHAR IS NOT NULL
        AND a.id = (
            SELECT MAX(b.id)
            FROM {{schema}}.alerts b
            WHERE
                b.namespace_id = (SELECT n.id FROM {{schema}}.namespaces n WHERE n.name = @p_namespace_name)
                AND b.deduplication_key = @p_deduplication_key
        )
    );
