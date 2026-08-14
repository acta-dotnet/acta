-- Version-CAS consume of an applied operator command: a miss means a newer command superseded the row
-- mid-apply and survives for the next tick. See IOutboxSignalStore.ConsumeAsync.
CREATE OR REPLACE FUNCTION {{schema}}.consume_outbox_signal(p_job_id BIGINT, p_name VARCHAR, p_expected_version INT)
RETURNS TABLE (out_consumed BIGINT)
LANGUAGE plpgsql
AS $$
DECLARE
    v_count BIGINT;
BEGIN
    DELETE FROM {{schema}}.checkpoints c
    WHERE
        c.job_id = p_job_id
        AND c.kind_code = 20 /* JobCheckpointKindCode.Signal */
        AND c.name = p_name
        AND c.version = p_expected_version;

    GET DIAGNOSTICS v_count = ROW_COUNT;
    RETURN QUERY SELECT v_count;
END;
$$;
