CREATE OR ALTER PROCEDURE {{schema}}.complete_execution
    @p_id                   BIGINT,
    @p_leased_by_worker_id  INT,
    @p_execution_number     INT,
    @p_reason_code          TINYINT,
    @p_reason_message       NVARCHAR(512),
    @p_result_format_id     TINYINT,
    @p_result               VARBINARY(MAX),
    @p_execution_succeeded  BIT,
    @p_duration_ms          INT,
    @p_final_status         TINYINT = NULL,
    @p_job_next_run_at_utc  DATETIME2(3) = NULL,
    @p_failure_count        SMALLINT = NULL,
    @p_recurring_result_cap INT = 0,
    @p_reschedule_status_code   TINYINT = NULL,
    @p_reschedule_delay_seconds INT = NULL,
    @p_reschedule_resume_at_utc DATETIME2(3) = NULL,
    @p_wait_signal_name     NVARCHAR(128) = NULL,
    @p_handler_status_code  TINYINT = NULL,
    @p_retention_seconds    INT = NULL,
    @p_schedule_advances    {{schema}}.job_schedule_advance_batch READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @action TINYINT;
    DECLARE @recurring BIT = CASE WHEN @p_final_status IS NOT NULL THEN 1 ELSE 0 END;

    DECLARE @rearm BIT = CASE WHEN @p_reschedule_status_code IS NOT NULL THEN 1 ELSE 0 END;

    DECLARE @signal_suspend BIT = CASE WHEN @rearm = 1 AND @p_wait_signal_name IS NOT NULL THEN 1 ELSE 0 END;

    DECLARE @handler BIT = CASE WHEN @p_handler_status_code IS NOT NULL THEN 1 ELSE 0 END;
    DECLARE @sig_state TINYINT = NULL;
    DECLARE @to_status TINYINT;
    DECLARE @final_status TINYINT;
    DECLARE @final_next_run DATETIME2(3);
    DECLARE @parent_id BIGINT;
    DECLARE @parent_released TINYINT = 0;

    DECLARE @matched BIT = 0;
    DECLARE @c_ref UNIQUEIDENTIFIER, @c_ns SMALLINT, @c_lineage BIGINT, @c_def INT, @c_tenant INT, @c_exec INT,
            @c_audit TINYINT,
            @c_next_existing DATETIME2(3), @c_retention_existing DATETIME2(3), @c_failcount_existing SMALLINT;
    DECLARE @c_next DATETIME2(3), @c_retention DATETIME2(3), @c_failcount SMALLINT;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @signal_suspend = 1
        BEGIN
            SELECT @sig_state = status_code
              FROM {{schema}}.checkpoints WITH (UPDLOCK, HOLDLOCK)
             WHERE job_id = @p_id
               AND kind_code IN (20 /* JobCheckpointKindCode.Signal */, 50 /* JobCheckpointKindCode.ChildLatch */)
               AND name = @p_wait_signal_name;
        END

        SET @to_status = CASE
            WHEN @signal_suspend = 1 AND @sig_state = 20 /* JobCheckpointStatusCode.Set */ THEN 10 /* JobStatusCode.Ready */
            WHEN @signal_suspend = 1 THEN 20 /* JobStatusCode.Suspended */
            WHEN @rearm = 1 THEN 10 /* JobStatusCode.Ready */
            WHEN @handler = 1 THEN @p_handler_status_code
            WHEN @recurring = 1 THEN @p_final_status
            WHEN @p_execution_succeeded = 1 THEN 100 /* JobStatusCode.Succeeded */
            ELSE 200 /* JobStatusCode.Failed */
        END;

        SELECT @matched               = 1,
               @c_ref                 = j.job_ref,
               @c_ns                  = j.namespace_id,
               @c_lineage             = j.lineage_root_id,
               @c_def                 = j.definition_id,
               @c_tenant              = j.tenant_id,
               @c_exec                = r.execution_number,
               @c_audit               = j.audit_level_code,
               @parent_id             = j.parent_id,
               @c_next_existing       = r.next_run_at_utc,
               @c_retention_existing  = r.retention_until_utc,
               @c_failcount_existing  = r.failure_count
          FROM {{schema}}.runtimes r WITH (UPDLOCK, ROWLOCK)
          INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
         WHERE r.job_id             = @p_id
           AND r.execution_number   = @p_execution_number
           AND r.status_code        = 50 /* JobStatusCode.Executing */
           AND r.leased_by_worker_id = @p_leased_by_worker_id;

        IF @matched = 1
        BEGIN
            SET @action = 1 /* CompleteExecutionAction.Completed */;

            SET @c_next = CASE
                WHEN @signal_suspend = 1 AND @sig_state = 20 /* JobCheckpointStatusCode.Set */ THEN @now
                WHEN @signal_suspend = 1 THEN NULL
                WHEN @rearm = 1 THEN COALESCE(@p_reschedule_resume_at_utc, DATEADD(SECOND, @p_reschedule_delay_seconds, @now))
                WHEN @handler = 1 THEN NULL
                WHEN @recurring = 1 THEN @p_job_next_run_at_utc
                ELSE @c_next_existing END;

            SET @c_retention = CASE
                WHEN @to_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                     AND @p_retention_seconds IS NOT NULL
                THEN DATEADD(SECOND, @p_retention_seconds, @now)
                ELSE @c_retention_existing END;

            SET @c_failcount = COALESCE(@p_failure_count, @c_failcount_existing);

            UPDATE {{schema}}.runtimes
               SET status_code          = @to_status,
                   next_run_at_utc      = @c_next,
                   failure_count        = @c_failcount,
                   leased_by_worker_id  = NULL,
                   lease_expires_at_utc = NULL,
                   retention_until_utc  = @c_retention,
                   modified_at_utc      = @now,
                   version              = version + 1
             WHERE job_id = @p_id;

            SET @final_status = @to_status;
            SET @final_next_run = @c_next;

            IF @p_result_format_id <> 0 /* JobPayloadFormat.None */
            BEGIN
                INSERT INTO {{schema}}.results (
                    job_id, execution_number,
                    result_format_id, result,
                    created_at_utc)
                VALUES (@p_id, @c_exec, @p_result_format_id, @p_result, @now);
            END

            IF @recurring = 1
            BEGIN
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor_key,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    reason_code, reason_message)
                SELECT
                    102 /* JobEventCode.SchedulePauseExpired */, @now, @c_ns,
                    10 /* JobActorCode.Sys */, NULL,
                    @p_id, @c_ref, @c_exec,
                    COALESCE(@c_lineage, @p_id), @c_def, @c_tenant,
                    NULL,
                    NULL, NULL,
                    NULL, NULL,
                    NULL, js.name
                  FROM {{schema}}.schedules js
                  INNER JOIN @p_schedule_advances adv ON adv.schedule_id = js.id
                 WHERE @c_audit = 20 /* JobAuditLevelCode.Audit */
                   AND js.status_code = 30 /* ScheduleStatusCode.Paused */;

                UPDATE js
                   SET js.next_run_at_utc  = adv.next_run_at_utc,
                       js.last_occurrence_at_utc = js.next_run_at_utc,
                       js.status_code      = 10 /* ScheduleStatusCode.Active */,
                       js.paused_until_utc = NULL,
                       js.modified_at_utc  = @now,
                       js.version          = js.version + 1
                  FROM {{schema}}.schedules js
                  INNER JOIN @p_schedule_advances adv ON adv.schedule_id = js.id;

                IF @p_recurring_result_cap > 0
                BEGIN
                    DELETE r
                      FROM {{schema}}.results r
                     WHERE r.job_id = @p_id
                       AND r.execution_number NOT IN (
                           SELECT TOP (@p_recurring_result_cap) execution_number
                             FROM {{schema}}.results
                            WHERE job_id = @p_id
                            ORDER BY execution_number DESC);
                END
            END

            IF @c_audit = 20 /* JobAuditLevelCode.Audit */
               OR (@c_audit = 10 /* JobAuditLevelCode.Failures */ AND @p_execution_succeeded = 0 AND @rearm = 0
                   AND NOT (@handler = 1 AND @p_handler_status_code IN (220 /* JobStatusCode.Cancelled */, 30 /* JobStatusCode.Paused */)))
            BEGIN
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor_key,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    reason_code, reason_message)
                VALUES (
                    41 /* JobEventCode.JobExecutionFinished */, @now, @c_ns,
                    70 /* JobActorCode.Worker */, NULL,
                    @p_id, @c_ref, @c_exec,
                    COALESCE(@c_lineage, @p_id), @c_def, @c_tenant,
                    @p_leased_by_worker_id,
                    50 /* JobStatusCode.Executing */, @to_status,
                    CASE WHEN @rearm = 1 THEN @p_reschedule_status_code
                         WHEN @handler = 1 AND @p_handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 220 /* ExecutionStatusCode.Cancelled */
                         WHEN @handler = 1 AND @p_handler_status_code = 30 /* JobStatusCode.Paused */ THEN 152 /* ExecutionStatusCode.Paused */
                         WHEN @p_execution_succeeded = 1 THEN 100 /* ExecutionStatusCode.Succeeded */
                         ELSE 200 /* ExecutionStatusCode.Failed */ END,
                    @p_duration_ms,
                    @p_reason_code,
                    @p_reason_message);
            END

            IF @recurring = 1 AND @c_audit = 20 /* JobAuditLevelCode.Audit */
               AND @p_final_status IN (10 /* JobStatusCode.Ready */, 30 /* JobStatusCode.Paused */)
            BEGIN
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor_key,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    reason_code, reason_message)
                VALUES (
                    CASE WHEN @p_final_status = 10 /* JobStatusCode.Ready */ THEN 50 /* JobEventCode.JobRecurringRolledOver */
                         ELSE 71 /* JobEventCode.JobPaused */ END,
                    @now, @c_ns,
                    70 /* JobActorCode.Worker */, NULL,
                    @p_id, @c_ref, @c_exec,
                    COALESCE(@c_lineage, @p_id), @c_def, @c_tenant,
                    @p_leased_by_worker_id,
                    50 /* JobStatusCode.Executing */, @to_status,
                    NULL, NULL,
                    @p_reason_code, @p_reason_message);
            END

            IF @rearm = 1 AND @c_audit = 20 /* JobAuditLevelCode.Audit */
               AND NOT (@signal_suspend = 1 AND @to_status = 10 /* JobStatusCode.Ready */)
            BEGIN
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor_key,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    reason_code, reason_message)
                VALUES (
                    CASE WHEN @p_reschedule_status_code = 151 /* ExecutionStatusCode.Suspended */ THEN 60 /* JobEventCode.JobSuspended */
                         ELSE 61 /* JobEventCode.JobRescheduled */ END,
                    @now, @c_ns,
                    70 /* JobActorCode.Worker */, NULL,
                    @p_id, @c_ref, @c_exec,
                    COALESCE(@c_lineage, @p_id), @c_def, @c_tenant,
                    @p_leased_by_worker_id,
                    50 /* JobStatusCode.Executing */, @to_status,
                    NULL, NULL,
                    @p_reason_code, @p_reason_message);
            END

            IF @handler = 1 AND @c_audit = 20 /* JobAuditLevelCode.Audit */
               AND @p_handler_status_code IN (220 /* JobStatusCode.Cancelled */, 30 /* JobStatusCode.Paused */)
            BEGIN
                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor_key,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    reason_code, reason_message)
                VALUES (
                    CASE WHEN @p_handler_status_code = 220 /* JobStatusCode.Cancelled */ THEN 70 /* JobEventCode.JobCancelled */
                         ELSE 71 /* JobEventCode.JobPaused */ END,
                    @now, @c_ns,
                    70 /* JobActorCode.Worker */, NULL,
                    @p_id, @c_ref, @c_exec,
                    COALESCE(@c_lineage, @p_id), @c_def, @c_tenant,
                    @p_leased_by_worker_id,
                    50 /* JobStatusCode.Executing */, @to_status,
                    NULL, NULL,
                    @p_reason_code, @p_reason_message);
            END

            IF @to_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */) AND @parent_id IS NOT NULL
            BEGIN
                DECLARE @sig VARCHAR(128) = 'sys.child.' + CAST(@p_id AS VARCHAR(20));
                DECLARE @psig TINYINT, @pstatus TINYINT, @pns SMALLINT, @plineage BIGINT, @pdef INT, @ptenant INT, @pexec INT, @paudit TINYINT;
                DECLARE @parent_ref UNIQUEIDENTIFIER;

                SELECT @psig = status_code
                  FROM {{schema}}.checkpoints WITH (UPDLOCK, HOLDLOCK)
                 WHERE job_id = @parent_id AND kind_code = 50 /* JobCheckpointKindCode.ChildLatch */ AND name = @sig;

                SELECT @pstatus = pr.status_code,
                       @pns     = j.namespace_id,
                       @plineage = j.lineage_root_id,
                       @pdef    = j.definition_id,
                       @ptenant = j.tenant_id,
                       @pexec   = pr.execution_number,
                       @paudit  = j.audit_level_code,
                       @parent_ref = j.job_ref
                  FROM {{schema}}.runtimes pr WITH (UPDLOCK, ROWLOCK)
                  INNER JOIN {{schema}}.jobs j ON j.id = pr.job_id
                 WHERE pr.job_id = @parent_id;

                IF @pstatus IS NOT NULL AND @pstatus NOT IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
                BEGIN
                    DECLARE @envelope NVARCHAR(MAX) =
                          N'{"childJobId":' + CAST(@p_id AS NVARCHAR(20))
                        + N',"status":' + CAST(@to_status AS NVARCHAR(3))
                        + N'}';
                    DECLARE @envelope_bytes VARBINARY(MAX) =
                        CAST(CAST(@envelope AS VARCHAR(MAX)) COLLATE Latin1_General_100_BIN2_UTF8 AS VARBINARY(MAX));

                    IF @psig IS NULL
                    BEGIN
                        INSERT INTO {{schema}}.checkpoints (
                            job_id, kind_code, name, status_code, value_format_id, value,
                            created_at_utc, modified_at_utc, version)
                        VALUES (@parent_id, 50 /* JobCheckpointKindCode.ChildLatch */, @sig, 20 /* JobCheckpointStatusCode.Set */, 1 /* JobPayloadFormat.Json */, @envelope_bytes, @now, @now, 0);
                    END
                    ELSE
                    BEGIN
                        UPDATE {{schema}}.checkpoints
                           SET status_code      = 20 /* JobCheckpointStatusCode.Set */,
                               value_format_id = 1 /* JobPayloadFormat.Json */,
                               value           = @envelope_bytes,
                               modified_at_utc = @now,
                               version         = version + 1
                         WHERE job_id = @parent_id AND kind_code = 50 /* JobCheckpointKindCode.ChildLatch */ AND name = @sig;
                    END

                    IF @paudit = 20 /* JobAuditLevelCode.Audit */
                    BEGIN
                        INSERT INTO {{schema}}.events (
                            event_code, created_at_utc, namespace_id,
                            actor_code, actor_key,
                            job_id, job_ref, execution_number,
                            lineage_root_id, definition_id, tenant_id,
                            worker_id,
                            from_status_code, to_status_code,
                            execution_status_code, duration_ms,
                            reason_code, reason_message)
                        VALUES (
                            80 /* JobEventCode.JobSignalRaised */, @now, @pns,
                            10 /* JobActorCode.Sys */, NULL,
                            @parent_id, @parent_ref, @pexec,
                            COALESCE(@plineage, @parent_id), @pdef, @ptenant,
                            NULL,
                            @pstatus, @pstatus,
                            NULL, NULL,
                            @p_reason_code, @p_reason_message);
                    END

                    IF @pstatus = 20 /* JobStatusCode.Suspended */
                    BEGIN
                        UPDATE {{schema}}.runtimes
                           SET status_code     = 10 /* JobStatusCode.Ready */,
                               next_run_at_utc = @now,
                               modified_at_utc = @now,
                               version         = version + 1
                         WHERE job_id = @parent_id;

                        IF @paudit = 20 /* JobAuditLevelCode.Audit */
                        BEGIN
                            INSERT INTO {{schema}}.events (
                                event_code, created_at_utc, namespace_id,
                                actor_code, actor_key,
                                job_id, job_ref, execution_number,
                                lineage_root_id, definition_id, tenant_id,
                                worker_id,
                                from_status_code, to_status_code,
                                execution_status_code, duration_ms,
                                reason_code, reason_message)
                            VALUES (
                                72 /* JobEventCode.JobResumed */, @now, @pns,
                                10 /* JobActorCode.Sys */, NULL,
                                @parent_id, @parent_ref, @pexec,
                                COALESCE(@plineage, @parent_id), @pdef, @ptenant,
                                NULL,
                                20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */,
                                NULL, NULL,
                                60 /* JobEventReasonCode.JobSignalReleased */, NULL);
                        END

                        SET @parent_released = 1;
                    END
                END
            END

        END
        ELSE
        BEGIN
            DECLARE @curWorker INT, @curStatus TINYINT;
            SELECT @curStatus = status_code,
                   @final_next_run = next_run_at_utc,
                   @curWorker = leased_by_worker_id
              FROM {{schema}}.runtimes
             WHERE job_id = @p_id;

            SET @final_status = @curStatus;
            SET @action = CASE
                WHEN @curStatus IS NULL
                  OR @curStatus IN (
                        100 /* JobStatusCode.Succeeded */,
                        200 /* JobStatusCode.Failed */,
                        220 /* JobStatusCode.Cancelled */
                    )
                    THEN 3 /* CompleteExecutionAction.AlreadyTerminal */
                WHEN @curWorker <> @p_leased_by_worker_id OR @curWorker IS NULL
                    THEN 2 /* CompleteExecutionAction.NotOwner */
                ELSE 3 /* CompleteExecutionAction.AlreadyTerminal */
            END;
        END

        COMMIT TRANSACTION;

        SELECT @action, @final_status, @final_next_run, @now, @parent_released;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;
END;
GO
