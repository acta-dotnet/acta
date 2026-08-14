-- Appends the applied-command evidence event against the sys.outbox slot job. Always emitted (no
-- audit-level gate): this is the trail for actions whose subject rows live in the producer's database.
CREATE OR REPLACE FUNCTION {{schema}}.record_outbox_event(
    p_job_id BIGINT,
    p_event_code SMALLINT,
    p_actor_code SMALLINT,
    p_actor_key VARCHAR,
    p_reason_message VARCHAR
)
RETURNS VOID
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO {{schema}}.events (
        event_code,
        created_at_utc,
        namespace_id,
        actor_code,
        actor_key,
        job_id,
        job_ref,
        execution_number,
        lineage_root_id,
        definition_id,
        tenant_id,
        reason_message)
    SELECT
        p_event_code,
        now(),
        j.namespace_id,
        p_actor_code,
        p_actor_key,
        j.id,
        j.job_ref,
        r.execution_number,
        COALESCE(j.lineage_root_id, j.id),
        j.definition_id,
        j.tenant_id,
        p_reason_message
    FROM {{schema}}.jobs j
    JOIN {{schema}}.runtimes r ON r.job_id = j.id
    WHERE j.id = p_job_id;
END;
$$;
