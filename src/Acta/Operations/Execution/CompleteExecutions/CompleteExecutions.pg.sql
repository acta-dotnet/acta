CREATE OR REPLACE FUNCTION {{schema}}.complete_executions(
    p_leased_by_worker_id INT,
    p_job_ids BIGINT[],
    p_execution_numbers INT[],
    p_succeeded BOOLEAN[],
    p_result_format_ids SMALLINT[],
    p_results BYTEA[],
    p_reason_codes SMALLINT[],
    p_reason_messages VARCHAR[],
    p_duration_ms INT[],
    p_failure_counts INT[],
    p_retention_seconds INT[]
)
RETURNS TABLE (job_id BIGINT, execution_number INT, action SMALLINT, final_status_code SMALLINT, db_now TIMESTAMPTZ)
LANGUAGE sql
AS $$
    WITH completions AS (
        SELECT *
        FROM unnest(
            p_job_ids, p_execution_numbers, p_succeeded, p_result_format_ids, p_results,
            p_reason_codes, p_reason_messages, p_duration_ms, p_failure_counts, p_retention_seconds
        ) AS c (
            job_id, execution_number, succeeded, result_format_id, result,
            reason_code, reason_message, duration_ms, failure_count, retention_seconds
        )
    ),
    -- The runtimes rows lock in job_id order. A job with a parent or a lane is never locked, so the batch takes no
    -- lane or parent lock (docs/internals/sql-execution-policy.md, "Lane lock order").
    eligible AS (
        SELECT r.job_id, r.execution_number
        FROM {{schema}}.runtimes r
        INNER JOIN completions c ON c.job_id = r.job_id AND c.execution_number = r.execution_number
        INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
        WHERE
            r.status_code = 50 /* JobStatusCode.Executing */
            AND r.leased_by_worker_id = p_leased_by_worker_id
            AND r.lane_id IS NULL
            AND j.parent_id IS NULL
        ORDER BY r.job_id
        FOR UPDATE OF r
    ),
    done AS (
        UPDATE {{schema}}.runtimes r
        SET
            status_code = CASE WHEN c.succeeded THEN 100 /* JobStatusCode.Succeeded */ ELSE 200 /* JobStatusCode.Failed */ END,
            failure_count = COALESCE(c.failure_count, r.failure_count),
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = COALESCE(now() + c.retention_seconds * INTERVAL '1 second', r.retention_until_utc),
            modified_at_utc = now(),
            version = r.version + 1
        FROM eligible e
        INNER JOIN completions c ON c.job_id = e.job_id AND c.execution_number = e.execution_number
        INNER JOIN {{schema}}.jobs j ON j.id = e.job_id
        WHERE r.job_id = e.job_id
        RETURNING
            r.job_id,
            r.execution_number,
            r.status_code,
            c.succeeded,
            c.result_format_id,
            c.result,
            c.reason_code,
            c.reason_message,
            c.duration_ms,
            j.namespace_id,
            j.lineage_root_id,
            j.definition_id,
            j.tenant_id,
            j.audit_level_code,
            j.job_ref
    ),
    stored AS (
        INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
        SELECT d.job_id, d.execution_number, d.result_format_id, d.result, now()
        FROM done d
        WHERE d.result_format_id <> 0 /* JobPayloadFormat.None */
    ),
    finished AS (
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
            worker_id,
            from_status_code,
            to_status_code,
            execution_status_code,
            duration_ms,
            reason_code,
            reason_message)
        SELECT
            41 /* EventCode.JobExecutionFinished */,
            now(),
            d.namespace_id,
            70 /* ActorCode.Worker */,
            NULL,
            d.job_id,
            d.job_ref,
            d.execution_number,
            COALESCE(d.lineage_root_id, d.job_id),
            d.definition_id,
            d.tenant_id,
            p_leased_by_worker_id,
            50 /* JobStatusCode.Executing */,
            d.status_code,
            CASE WHEN d.succeeded THEN 100 /* ExecutionStatusCode.Succeeded */ ELSE 200 /* ExecutionStatusCode.Failed */ END,
            d.duration_ms,
            d.reason_code,
            d.reason_message
        FROM done d
        -- At Failures a failure is written, and a success only when it answers a recorded failure: the job's newest
        -- finished event is not a success. One seek on the timeline index per row.
        WHERE
            d.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
            OR (
                d.audit_level_code = 10 /* DefinitionAuditLevelCode.Failures */
                AND (
                    NOT d.succeeded
                    OR COALESCE((
                        SELECT e.execution_status_code
                        FROM {{schema}}.events e
                        WHERE e.job_id = d.job_id AND e.event_code = 41 /* EventCode.JobExecutionFinished */
                        ORDER BY e.created_at_utc DESC, e.id DESC
                        LIMIT 1), 100) <> 100 /* ExecutionStatusCode.Succeeded */
                )
            )
    )
    -- runtimes reads as it stood before the update, so every row of a job this call completed takes the status from done.
    SELECT
        c.job_id,
        c.execution_number,
        CAST(CASE
            WHEN d.execution_number = c.execution_number THEN 1 /* CompleteExecutionsAction.Completed */
            WHEN
                r.status_code IS NULL
                OR r.status_code IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
            WHEN r.leased_by_worker_id IS DISTINCT FROM p_leased_by_worker_id THEN 2 /* CompleteExecutionsAction.NotOwner */
            WHEN r.lane_id IS NOT NULL OR j.parent_id IS NOT NULL THEN 4 /* CompleteExecutionsAction.PerJob */
            ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
        END AS SMALLINT),
        COALESCE(d.status_code, r.status_code),
        now()
    FROM completions c
    LEFT JOIN done d ON d.job_id = c.job_id
    LEFT JOIN {{schema}}.runtimes r ON r.job_id = c.job_id
    LEFT JOIN {{schema}}.jobs j ON j.id = c.job_id;
$$;
