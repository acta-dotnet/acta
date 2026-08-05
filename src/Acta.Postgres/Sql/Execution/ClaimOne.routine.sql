CREATE OR REPLACE FUNCTION {{schema}}.claim_one(
    p_namespace_id    SMALLINT,
    p_leased_by_worker_id INT,
    p_lease_ttl_seconds   INT,
    p_id                  BIGINT,
    p_start_executing     BOOLEAN
)
RETURNS TABLE(
    id                   BIGINT,
    namespace_id     SMALLINT,
    definition_id    INT,
    execution_number     INT,
    deduplication_key           VARCHAR,
    correlation_key       VARCHAR,
    exclusive_key      VARCHAR,
    input_format_id      SMALLINT,
    input                BYTEA,
    next_run_at_utc      TIMESTAMPTZ,
    lease_expires_at_utc TIMESTAMPTZ,
    created_at_utc       TIMESTAMPTZ,
    failure_count        SMALLINT,
    version              INT,
    job_ref              UUID,
    tenant_id            INT,
    db_now               TIMESTAMPTZ,
    next_ready_at_utc    TIMESTAMPTZ
)
LANGUAGE sql
AS $$
    WITH candidates AS (
        SELECT r.job_id AS id
          FROM {{schema}}.runtimes r
         WHERE r.job_id = p_id
           AND r.namespace_id = p_namespace_id
           AND r.status_code = 10 /* JobStatusCode.Ready */
           AND (r.next_run_at_utc IS NULL OR r.next_run_at_utc <= now())
         FOR UPDATE OF r SKIP LOCKED
    ),
    updated AS (
        UPDATE {{schema}}.runtimes r
           SET status_code          = CASE WHEN p_start_executing THEN 50 /* JobStatusCode.Executing */ ELSE 40 /* JobStatusCode.Dispatched */ END,
               execution_number     = r.execution_number + 1,
               leased_by_worker_id  = p_leased_by_worker_id,
               lease_expires_at_utc = now() + (p_lease_ttl_seconds * INTERVAL '1 second'),
               modified_at_utc      = now(),
               version              = r.version + 1
          FROM candidates c
          JOIN {{schema}}.jobs j ON j.id = c.id
         WHERE r.job_id = c.id
        RETURNING
            r.job_id AS id, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id,
            r.execution_number, j.deduplication_key, j.correlation_key, j.exclusive_key,
            j.input_format_id, j.input, r.next_run_at_utc, j.created_at_utc, j.audit_level_code,
            r.failure_count, r.version, j.job_ref
    ),
    started_event AS (
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id, worker_id,
            from_status_code, to_status_code, execution_status_code, duration_ms,
            reason_code, reason_message)
        SELECT
            40 /* JobEventCode.JobExecutionStarted */, now(), u.namespace_id, 70 /* JobActorCode.Worker */, NULL,
            u.id, u.job_ref, u.execution_number,
            COALESCE(u.lineage_root_id, u.id), u.definition_id, u.tenant_id, p_leased_by_worker_id,
            10 /* JobStatusCode.Ready */, 50 /* JobStatusCode.Executing */, 50 /* ExecutionStatusCode.Executing */, NULL,
            NULL, NULL
          FROM updated u
         WHERE p_start_executing AND u.audit_level_code = 20 /* JobAuditLevelCode.Audit */
        RETURNING 1
    )
    SELECT
        u.id, u.namespace_id, u.definition_id, u.execution_number,
        u.deduplication_key, u.correlation_key, u.exclusive_key,
        u.input_format_id, u.input, u.next_run_at_utc,
        now() + (p_lease_ttl_seconds * INTERVAL '1 second') AS lease_expires_at_utc,
        u.created_at_utc, u.failure_count, u.version, u.job_ref, u.tenant_id,
        NULL::timestamptz AS db_now,
        NULL::timestamptz AS next_ready_at_utc
      FROM updated u;
$$;
