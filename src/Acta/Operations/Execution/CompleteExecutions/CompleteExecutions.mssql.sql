CREATE OR ALTER PROCEDURE {{schema}}.complete_executions
    @p_leased_by_worker_id INT,
    @p_batch {{schema}}.complete_executions_batch READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

        /* Read the ids first and without locks, since lane_id and parent_id never change: a job with a parent or a
           lane is never locked, so the batch takes no lane or parent lock (docs/internals/sql-execution-policy.md,
           "Lane lock order"). */
        DECLARE @plain TABLE (job_id BIGINT NOT NULL, execution_number INT NOT NULL, PRIMARY KEY (job_id, execution_number));
        INSERT INTO @plain (job_id, execution_number)
        SELECT c.job_id, c.execution_number
        FROM @p_batch c
        INNER JOIN {{schema}}.runtimes r ON r.job_id = c.job_id
        INNER JOIN {{schema}}.jobs j ON j.id = c.job_id
        WHERE r.lane_id IS NULL AND j.parent_id IS NULL;

        -- The update locks each row through its clustered key in job_id order, as complete_execution does.
        DECLARE @done TABLE (job_id BIGINT NOT NULL PRIMARY KEY, execution_number INT NOT NULL, status_code TINYINT NOT NULL);
        UPDATE r
        SET
            status_code = CASE WHEN c.succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */ ELSE 200 /* JobStatusCode.Failed */ END,
            failure_count = COALESCE(c.failure_count, r.failure_count),
            leased_by_worker_id = NULL,
            lease_expires_at_utc = NULL,
            retention_until_utc = COALESCE(DATEADD(SECOND, c.retention_seconds, @now), r.retention_until_utc),
            modified_at_utc = @now,
            version = r.version + 1
        OUTPUT INSERTED.job_id, INSERTED.execution_number, INSERTED.status_code INTO @done (job_id, execution_number, status_code)
        FROM @plain p
        INNER JOIN {{schema}}.runtimes r WITH (INDEX (pk_runtimes), FORCESEEK, ROWLOCK) ON r.job_id = p.job_id
        INNER JOIN @p_batch c ON c.job_id = p.job_id AND c.execution_number = p.execution_number
        WHERE
            r.execution_number = p.execution_number
            AND r.status_code = 50 /* JobStatusCode.Executing */
            AND r.leased_by_worker_id = @p_leased_by_worker_id;

        -- fk_results_jobs seeks each job by key (docs/internals/sql-execution-policy.md, "Foreign keys in set-based writes").
        INSERT INTO {{schema}}.results (job_id, execution_number, result_format_id, result, created_at_utc)
        SELECT d.job_id, d.execution_number, c.result_format_id, c.result, @now
        FROM @done d
        INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
        WHERE c.result_format_id <> 0 /* JobPayloadFormat.None */
        OPTION (LOOP JOIN);

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            actor_code, actor,
            job_id, job_ref, execution_number,
            lineage_root_id, definition_id, tenant_id,
            worker_id,
            from_status_code, to_status_code,
            execution_status_code, duration_ms,
            reason_code, reason_message
        )
        SELECT
            41 /* EventCode.JobExecutionFinished */, @now, j.namespace_id,
            70 /* ActorCode.Worker */, NULL,
            d.job_id, j.job_ref, d.execution_number,
            COALESCE(j.lineage_root_id, d.job_id), j.definition_id, j.tenant_id,
            @p_leased_by_worker_id,
            50 /* JobStatusCode.Executing */, d.status_code,
            CASE WHEN c.succeeded = 1 THEN 100 /* ExecutionStatusCode.Succeeded */ ELSE 200 /* ExecutionStatusCode.Failed */ END,
            c.duration_ms,
            c.reason_code, c.reason_message
        FROM @done d
        INNER JOIN @p_batch c ON c.job_id = d.job_id AND c.execution_number = d.execution_number
        INNER JOIN {{schema}}.jobs j ON j.id = d.job_id
        -- At Failures a failure is written, and a success only when it answers a recorded failure: the job's newest
        -- finished event is not a success. One seek on the timeline index per row.
        WHERE
            j.audit_level_code = 20 /* DefinitionAuditLevelCode.Audit */
            OR (
                j.audit_level_code = 10 /* DefinitionAuditLevelCode.Failures */
                AND (
                    c.succeeded = 0
                    OR COALESCE((
                        SELECT TOP (1) e.execution_status_code FROM {{schema}}.events e
                        WHERE e.job_id = d.job_id AND e.event_code = 41 /* EventCode.JobExecutionFinished */
                        ORDER BY e.created_at_utc DESC, e.id DESC), 100) <> 100 /* ExecutionStatusCode.Succeeded */
                )
            );

        SELECT
            c.job_id,
            c.execution_number,
            CASE
                WHEN d.job_id IS NOT NULL THEN 1 /* CompleteExecutionsAction.Completed */
                WHEN
                    r.status_code IS NULL
                    OR r.status_code IN (
                        100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */
                    )
                    THEN 3 /* CompleteExecutionsAction.AlreadyTerminal */
                WHEN r.leased_by_worker_id IS NULL OR r.leased_by_worker_id <> @p_leased_by_worker_id
                    THEN 2 /* CompleteExecutionsAction.NotOwner */
                WHEN r.lane_id IS NOT NULL OR j.parent_id IS NOT NULL THEN 4 /* CompleteExecutionsAction.PerJob */
                ELSE 3 /* CompleteExecutionsAction.AlreadyTerminal */
            END AS action,
            r.status_code AS final_status_code,
            @now AS db_now
        FROM @p_batch c
        LEFT JOIN @done d ON d.job_id = c.job_id AND d.execution_number = c.execution_number
        LEFT JOIN {{schema}}.runtimes r ON r.job_id = c.job_id
        LEFT JOIN {{schema}}.jobs j ON j.id = c.job_id;

        IF @entry_trancount = 0
            COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @entry_trancount = 0 AND XACT_STATE() <> 0
            ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
