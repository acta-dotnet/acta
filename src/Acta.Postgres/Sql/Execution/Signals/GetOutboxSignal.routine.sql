-- The applying tick's read of one pending operator command; empty when the inbox slot is free.
CREATE OR REPLACE FUNCTION {{schema}}.get_outbox_signal(p_job_id BIGINT, p_name VARCHAR)
RETURNS TABLE (out_value_format_id SMALLINT, out_value BYTEA, out_version INT)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    SELECT c.value_format_id::SMALLINT, c.value, c.version
    FROM {{schema}}.checkpoints c
    WHERE
        c.job_id = p_job_id
        AND c.kind_code = 20 /* JobCheckpointKindCode.Signal */
        AND c.name = p_name
        AND c.status_code = 20 /* JobCheckpointStatusCode.Set */;
END;
$$;
