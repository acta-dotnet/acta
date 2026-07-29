CREATE OR REPLACE FUNCTION {{schema}}.complete_executions_batch(
    p_b_ordinal            INT[],
    p_b_id                 BIGINT[],
    p_b_worker_id          INT[],
    p_b_execution_number   INT[],
    p_b_succeeded          BOOLEAN[],
    p_b_duration_ms        INT[],
    p_b_reason_code        SMALLINT[],
    p_b_reason_message     VARCHAR[],
    p_b_result_format_id   SMALLINT[],
    p_b_result             BYTEA[],
    p_b_failure_count      SMALLINT[],
    p_b_retention_seconds  INT[]
)
RETURNS TABLE(ordinal INT, finalized SMALLINT)
LANGUAGE plpgsql
AS $$
BEGIN
    RETURN QUERY
    WITH batch AS (
        SELECT b.ordinal, b.id, b.worker_id, b.execution_number, b.succeeded, b.duration_ms,
               b.reason_code, b.reason_message, b.result_format_id, b.result, b.failure_count,
               b.retention_seconds
          FROM unnest(
            p_b_ordinal, p_b_id, p_b_worker_id, p_b_execution_number, p_b_succeeded, p_b_duration_ms,
            p_b_reason_code, p_b_reason_message, p_b_result_format_id, p_b_result, p_b_failure_count,
            p_b_retention_seconds
          ) AS b(ordinal, id, worker_id, execution_number, succeeded, duration_ms,
                 reason_code, reason_message, result_format_id, result, failure_count, retention_seconds)
    ),
    updated AS (
        UPDATE {{schema}}.runtimes r
           SET status_code          = CASE WHEN b.succeeded THEN 100 /* JobStatusCode.Done */ ELSE 200 /* JobStatusCode.Failed */ END,
               failure_count        = COALESCE(b.failure_count, r.failure_count),
               leased_by_worker_id  = NULL,
               lease_expires_at_utc = NULL,
               retention_until_utc  = CASE WHEN b.retention_seconds IS NOT NULL
                                           THEN now() + make_interval(secs => b.retention_seconds)
                                           ELSE r.retention_until_utc END,
               modified_at_utc      = now(),
               version              = r.version + 1
          FROM batch b
          JOIN {{schema}}.jobs j ON j.id = b.id
         WHERE r.job_id             = b.id
           AND r.execution_number    = b.execution_number
           AND r.status_code         = 50 /* JobStatusCode.Executing */
           AND j.parent_id        IS NULL
           AND r.leased_by_worker_id = b.worker_id
        RETURNING b.ordinal, b.id, b.worker_id, b.execution_number, b.succeeded, b.duration_ms,
                  b.reason_code, b.reason_message, b.result_format_id, b.result,
                  j.job_ref, j.namespace_id, j.lineage_root_id, j.definition_id, j.tenant_id, j.audit_level_code
    ),
    result_insert AS (
        INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
        SELECT u.id, u.execution_number, u.result_format_id, u.result, now()
          FROM updated u
         WHERE u.result_format_id <> 0 /* JobPayloadFormat.None */
        RETURNING 1
    ),
    event_insert AS (
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key,
            job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id,
            worker_id, from_status_code, to_status_code, execution_status_code, duration_ms,
            reason_code, reason_message)
        SELECT 41 /* JobEventCode.JobExecutionFinished */, now(), u.namespace_id, 70 /* JobActorCode.Worker */, NULL,
               u.id, u.job_ref, u.execution_number, COALESCE(u.lineage_root_id, u.id), u.definition_id, u.tenant_id,
               u.worker_id, 50 /* JobStatusCode.Executing */,
               CASE WHEN u.succeeded THEN 100 /* JobStatusCode.Done */ ELSE 200 /* JobStatusCode.Failed */ END,
               CASE WHEN u.succeeded THEN 100 /* ExecutionStatusCode.Succeeded */ ELSE 200 /* ExecutionStatusCode.Failed */ END, u.duration_ms,
               u.reason_code, u.reason_message
          FROM updated u
         WHERE u.audit_level_code = 20 /* JobAuditLevelCode.Audit */
            OR (u.audit_level_code = 10 /* JobAuditLevelCode.Failures */ AND NOT u.succeeded)
        RETURNING 1
    )
    SELECT b.ordinal, CAST(CASE WHEN u.ordinal IS NOT NULL THEN 1 ELSE 0 END AS SMALLINT)
      FROM batch b
      LEFT JOIN updated u ON u.ordinal = b.ordinal
     ORDER BY b.ordinal;
END;
$$;
