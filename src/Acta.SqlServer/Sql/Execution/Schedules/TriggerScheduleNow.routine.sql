CREATE OR ALTER PROCEDURE {{schema}}.trigger_schedule_now
    @p_job_id          BIGINT,
    @p_name            VARCHAR(128),
    @p_actor_code      TINYINT,
    @p_actor_key        VARCHAR(128),
    @p_reason_message  NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @schedule_id BIGINT, @status TINYINT, @paused DATETIME2(7), @next DATETIME2(7), @version INT;
    DECLARE @slot_status TINYINT;
    DECLARE @ns SMALLINT, @def INT, @lineage BIGINT, @en INT, @audit TINYINT, @job_ref UNIQUEIDENTIFIER;

    BEGIN TRY
        BEGIN TRANSACTION;

        /* Lock the slot's runtimes row before the schedules row: register_scheduled_jobs writes
           runtimes then schedules, so every writer of both must take runtimes first. The schedules
           row is guard-only here (never updated): a manual trigger moves only the slot's cursor. */
        SELECT @slot_status = r.status_code, @en = r.execution_number
          FROM {{schema}}.runtimes r WITH (UPDLOCK, ROWLOCK)
         WHERE r.job_id = @p_job_id;

        SELECT @schedule_id = js.id, @status = js.status_code, @paused = js.paused_until_utc,
               @next = js.next_run_at_utc, @version = js.version
          FROM {{schema}}.schedules js WITH (UPDLOCK, ROWLOCK)
         WHERE js.job_id = @p_job_id
           AND js.name = @p_name
           AND js.status_code <> 230 /* ScheduleStatusCode.Orphaned */;

        IF @schedule_id IS NULL
        BEGIN
            COMMIT TRANSACTION;
            SELECT CAST(2 /* JobControlAction.NotFound */ AS TINYINT) AS action, CAST(NULL AS TINYINT) AS status_code,
                   CAST(NULL AS DATETIME2(7)) AS paused_until_utc, CAST(NULL AS DATETIME2(7)) AS next_run_at_utc,
                   CAST(NULL AS INT) AS version;
            RETURN;
        END;

        IF @status = 30 /* ScheduleStatusCode.Paused */
        BEGIN
            COMMIT TRANSACTION;
            SELECT CAST(3 /* JobControlAction.Rejected */ AS TINYINT) AS action, @status AS status_code, @paused AS paused_until_utc,
                   @next AS next_run_at_utc, @version AS version;
            RETURN;
        END;

        IF @slot_status IN (40 /* JobStatusCode.Dispatched */, 50 /* JobStatusCode.Executing */)
        BEGIN
            COMMIT TRANSACTION;
            SELECT CAST(3 /* JobControlAction.Rejected */ AS TINYINT) AS action, @status AS status_code, @paused AS paused_until_utc,
                   @next AS next_run_at_utc, @version AS version;
            RETURN;
        END;

        IF @slot_status IN (100 /* JobStatusCode.Succeeded */, 200 /* JobStatusCode.Failed */, 220 /* JobStatusCode.Cancelled */)
        BEGIN
            COMMIT TRANSACTION;
            SELECT CAST(3 /* JobControlAction.Rejected */ AS TINYINT) AS action, @status AS status_code, @paused AS paused_until_utc,
                   @next AS next_run_at_utc, @version AS version;
            RETURN;
        END;

        SELECT @ns = j.namespace_id, @def = j.definition_id, @lineage = j.lineage_root_id,
               @audit = j.audit_level_code, @job_ref = j.job_ref
          FROM {{schema}}.jobs j
         WHERE j.id = @p_job_id;

        UPDATE {{schema}}.runtimes
           SET next_run_at_utc = @now,
               status_code     = 10 /* JobStatusCode.Ready */,
               modified_at_utc = @now,
               version         = version + 1
         WHERE job_id = @p_job_id
           AND status_code IN (30 /* JobStatusCode.Paused */, 20 /* JobStatusCode.Suspended */, 10 /* JobStatusCode.Ready */);

        IF @audit = 20 /* JobAuditLevelCode.Audit */
        BEGIN
            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                actor_code, actor_key,
                job_id, job_ref, execution_number,
                lineage_root_id, definition_id,
                worker_id,
                from_status_code, to_status_code,
                execution_status_code, duration_ms,
                reason_code, reason_message)
            VALUES (
                104 /* JobEventCode.ScheduleTriggered */, @now, @ns,
                @p_actor_code, @p_actor_key,
                @p_job_id, @job_ref, @en,
                COALESCE(@lineage, @p_job_id), @def,
                NULL,
                NULL, NULL,
                NULL, NULL,
                NULL, @p_reason_message);
        END

        COMMIT TRANSACTION;
        SELECT CAST(1 /* JobControlAction.Applied */ AS TINYINT) AS action, @status AS status_code, @paused AS paused_until_utc,
               @next AS next_run_at_utc, @version AS version;
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
