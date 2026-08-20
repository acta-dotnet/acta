CREATE OR REPLACE FUNCTION {{schema}}.raise_job_alert(
    p_namespace_name VARCHAR,
    p_job_id BIGINT,
    p_origin_code SMALLINT,
    p_severity_code SMALLINT,
    p_kind_code SMALLINT,
    p_title VARCHAR,
    p_message VARCHAR,
    p_channel_name VARCHAR,
    p_delivery_status_code SMALLINT,
    p_dedupe_key VARCHAR,
    p_source_event_id BIGINT,
    p_alert_ref UUID
)
-- out_-prefixed (RegisterScheduledJobs precedent): RETURNS TABLE names become plpgsql variables, and
-- bare column names would make every occurrence_count / last_projected_event_id reference ambiguous.
RETURNS TABLE (out_occurrence_count INT, out_last_projected_event_id BIGINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_occurrence_count INT;
    v_last_projected_event_id BIGINT;
    v_ns SMALLINT;
    v_job_ref UUID;
BEGIN

    SELECT id INTO v_ns FROM {{schema}}.namespaces WHERE name = p_namespace_name;
    IF v_ns IS NULL THEN
        RAISE EXCEPTION 'raise_job_alert: unknown namespace ''%''', p_namespace_name;
    END IF;

    SELECT job_ref INTO v_job_ref FROM {{schema}}.jobs WHERE id = p_job_id;

    IF p_job_id IS NOT NULL AND v_job_ref IS NULL THEN
        RAISE EXCEPTION 'ACTA:ALERT_UNKNOWN_JOB:raise_job_alert: unknown job id'
            USING ERRCODE = 'P0001';
    END IF;

    IF p_dedupe_key IS NULL THEN
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
            dedupe_key,
            occurrence_count,
            last_projected_event_id,
            delivery_status_code,
            retry_count,
            created_at_utc,
            modified_at_utc,
            version)
        VALUES (
            v_ns,
            p_alert_ref,
            p_job_id,
            v_job_ref,
            p_origin_code,
            p_severity_code,
            p_kind_code,
            p_title,
            p_message,
            p_channel_name,
            NULL,
            1,
            p_source_event_id,
            p_delivery_status_code,
            0,
            now(),
            now(),
            0);
        RETURN QUERY SELECT 1, p_source_event_id;
        RETURN;
    END IF;

    -- One OPEN row per (namespace_id, dedupe_key) - the incident. INSERT ... SELECT rather than VALUES
    -- so the insert arm can carry the ghost guard below.
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
        dedupe_key,
        occurrence_count,
        last_projected_event_id,
        delivery_status_code,
        retry_count,
        created_at_utc,
        modified_at_utc,
        version)
    SELECT
        v_ns,
        p_alert_ref,
        p_job_id,
        v_job_ref,
        p_origin_code,
        p_severity_code,
        p_kind_code,
        p_title,
        p_message,
        p_channel_name,
        p_dedupe_key,
        1,
        p_source_event_id,
        p_delivery_status_code,
        0,
        now(),
        now(),
        0
    -- Ghost guard: a failure replayed after its incident closed must not open one behind the events
    -- this identity has already absorbed - which is why it compares against every row of the identity,
    -- not just an open one. NULL, hence never blocking, for a manual raise, which carries no event.
    WHERE NOT EXISTS (
        SELECT 1
        FROM {{schema}}.alerts g
        WHERE
            g.namespace_id = v_ns
            AND g.dedupe_key = p_dedupe_key
            AND g.last_projected_event_id >= p_source_event_id
    )
    -- The conflict target is the filtered unique index, so a resolved row is invisible to it and the
    -- next failure opens a fresh incident instead of re-opening the closed one.
    ON CONFLICT (namespace_id, dedupe_key) WHERE dedupe_key IS NOT NULL AND resolved_at_utc IS NULL
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
        p_source_event_id IS NULL
        OR {{schema}}.alerts.last_projected_event_id IS NULL
        OR p_source_event_id > {{schema}}.alerts.last_projected_event_id
    RETURNING occurrence_count, last_projected_event_id INTO v_occurrence_count, v_last_projected_event_id;

    -- Nothing written: a replay-held update or a ghost-blocked insert. The identity's newest row has
    -- already absorbed this event or a newer one, so its count and mark let the caller re-assert only
    -- the escalation this very event earned - never one for a neighbour the incident never counted.
    IF v_occurrence_count IS NULL THEN
        SELECT a.occurrence_count, a.last_projected_event_id
        INTO v_occurrence_count, v_last_projected_event_id
        FROM {{schema}}.alerts a
        WHERE
            a.namespace_id = v_ns
            AND a.dedupe_key = p_dedupe_key
        ORDER BY a.id DESC
        LIMIT 1;
    END IF;

    RETURN QUERY SELECT v_occurrence_count, v_last_projected_event_id;
END;
$$;

-- CREATE OR REPLACE across arities creates an overload instead of replacing; drop the retired
-- signature (with p_dedupe_window_start_utc) so pre-existing installs cannot resolve the stale form.
DROP FUNCTION IF EXISTS {{schema}}.raise_job_alert(
    VARCHAR, BIGINT, SMALLINT, SMALLINT, SMALLINT, VARCHAR, VARCHAR, VARCHAR, SMALLINT, VARCHAR,
    TIMESTAMPTZ, BIGINT, UUID
);
