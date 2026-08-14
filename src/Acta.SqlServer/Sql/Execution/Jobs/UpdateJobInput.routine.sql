CREATE OR ALTER PROCEDURE {{schema}}.update_job_input
    @p_id BIGINT,
    @p_input_format_id TINYINT,
    @p_input VARBINARY(MAX),
    @p_actor_code TINYINT,
    @p_actor_key VARCHAR(128),
    @p_reason_code TINYINT,
    @p_reason_message NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @from_status TINYINT, @namespace_id SMALLINT,
        @lineage_root_id BIGINT, @definition_id INT, @tenant_id INT, @execution_number INT, @audit_level TINYINT,
        @job_ref UNIQUEIDENTIFIER, @old_format_id TINYINT, @old_input VARBINARY(MAX);
    DECLARE @detail VARBINARY(MAX);
    DECLARE @detail_format_id TINYINT = 0 /* JobPayloadFormat.None */;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT
            @from_status = r.status_code,
            @namespace_id = j.namespace_id,
            @lineage_root_id = j.lineage_root_id,
            @definition_id = j.definition_id,
            @tenant_id = j.tenant_id,
            @execution_number = r.execution_number,
            @audit_level = j.audit_level_code,
            @job_ref = j.job_ref,
            @old_format_id = j.input_format_id,
            @old_input = j.input
        FROM {{schema}}.runtimes r WITH (UPDLOCK, ROWLOCK)
        INNER JOIN {{schema}}.jobs j ON j.id = r.job_id
        WHERE r.job_id = @p_id;

        IF @from_status IS NULL
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(2 /* JobControlAction.NotFound */ AS TINYINT) AS action,
                    CAST(NULL AS TINYINT) AS status_code;
                RETURN;
            END;

        IF
            @from_status IN (
                40 /* JobStatusCode.Dispatched */,
                50 /* JobStatusCode.Executing */
            )
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(3 /* JobControlAction.Rejected */ AS TINYINT) AS action,
                    @from_status AS status_code;
                RETURN;
            END;

        UPDATE {{schema}}.jobs
        SET
            input = @p_input,
            input_format_id = @p_input_format_id
        WHERE id = @p_id;

        IF @audit_level = 20 /* JobAuditLevelCode.Audit */
            BEGIN
            -- The event carries only bounded JSON metadata about the previous payload (format name
            -- and byte count), never the payload itself. Pure ASCII, so VARCHAR -> VARBINARY is UTF-8.
                IF @old_format_id <> 0 /* JobPayloadFormat.None */
                    BEGIN
                        SET @detail_format_id = 1 /* JobPayloadFormat.Json */;
                        SET @detail = CONVERT(
                            VARBINARY(MAX),
                            '{"format":"'
                            + CASE @old_format_id
                                WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
                                WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
                                WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
                                ELSE 'custom-' + CONVERT(VARCHAR(3), @old_format_id)
                            END
                            + '","bytes":' + CONVERT(VARCHAR(20), DATALENGTH(@old_input)) + '}'
                        );
                    END;

                INSERT INTO {{schema}}.events (
                    event_code, created_at_utc, namespace_id,
                    actor_code, actor_key,
                    job_id, job_ref, execution_number,
                    lineage_root_id, definition_id, tenant_id,
                    worker_id,
                    from_status_code, to_status_code,
                    execution_status_code, duration_ms,
                    detail_format_id, detail,
                    reason_code, reason_message
                )
                VALUES (
                    76 /* EventCode.JobInputAmended */, @now, @namespace_id,
                    @p_actor_code, @p_actor_key,
                    @p_id, @job_ref, @execution_number,
                    COALESCE(@lineage_root_id, @p_id), @definition_id, @tenant_id,
                    NULL,
                    NULL, NULL,
                    NULL, NULL,
                    @detail_format_id, @detail,
                    @p_reason_code, @p_reason_message
                );
            END

        COMMIT TRANSACTION;
        SELECT
            CAST(1 /* JobControlAction.Applied */ AS TINYINT) AS action,
            @from_status AS status_code;
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
