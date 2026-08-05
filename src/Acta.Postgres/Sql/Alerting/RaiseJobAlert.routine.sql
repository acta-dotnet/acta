CREATE OR REPLACE FUNCTION {{schema}}.raise_job_alert(
    p_namespace_name      VARCHAR,
    p_job_id                  BIGINT,
    p_origin_code             SMALLINT,
    p_severity_code           SMALLINT,
    p_kind_code             SMALLINT,
    p_title                   VARCHAR,
    p_message                 VARCHAR,
    p_channel_name            VARCHAR,
    p_delivery_status_code    SMALLINT,
    p_dedupe_key              VARCHAR,
    p_dedupe_window_start_utc TIMESTAMPTZ
)
RETURNS INT
LANGUAGE plpgsql
AS $$
DECLARE
    v_occurrence_count INT;
    v_ns               SMALLINT;
    v_job_ref          UUID;
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
            namespace_id, job_id, job_ref,
            origin_code, severity_code, kind_code, title, message, channel_name,
            dedupe_key, dedupe_window_start_utc, occurrence_count,
            delivery_status_code, retry_count,
            created_at_utc, modified_at_utc, version)
        VALUES (
            v_ns, p_job_id, v_job_ref,
            p_origin_code, p_severity_code, p_kind_code, p_title, p_message, p_channel_name,
            NULL, NULL, 1,
            p_delivery_status_code, 0,
            now(), now(), 0);
        RETURN 1;
    END IF;

    INSERT INTO {{schema}}.alerts (
        namespace_id, job_id, job_ref,
        origin_code, severity_code, kind_code, title, message, channel_name,
        dedupe_key, dedupe_window_start_utc, occurrence_count,
        delivery_status_code, retry_count,
        created_at_utc, modified_at_utc, version)
    VALUES (
        v_ns, p_job_id, v_job_ref,
        p_origin_code, p_severity_code, p_kind_code, p_title, p_message, p_channel_name,
        p_dedupe_key, p_dedupe_window_start_utc, 1,
        p_delivery_status_code, 0,
        now(), now(), 0)
    ON CONFLICT (namespace_id, dedupe_key, dedupe_window_start_utc) WHERE dedupe_key IS NOT NULL
    DO UPDATE SET
        job_id            = EXCLUDED.job_id,
        job_ref           = EXCLUDED.job_ref,
        origin_code       = EXCLUDED.origin_code,
        severity_code     = EXCLUDED.severity_code,
        kind_code       = EXCLUDED.kind_code,
        title             = EXCLUDED.title,
        message           = EXCLUDED.message,
        channel_name      = EXCLUDED.channel_name,
        occurrence_count  = {{schema}}.alerts.occurrence_count + 1,
        resolved_at_utc   = NULL,
        modified_at_utc   = now(),
        version           = {{schema}}.alerts.version + 1
    RETURNING occurrence_count INTO v_occurrence_count;

    RETURN v_occurrence_count;
END;
$$;
