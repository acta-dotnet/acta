SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE
        @from_status TINYINT, @namespace_id INT,
        @lineage_root_job_id BIGINT, @definition_id INT, @tenant_id INT, @execution_number INT, @event_recording TINYINT,
        @old_format_id TINYINT, @old_input VARBINARY(MAX);
    DECLARE @old_members VARCHAR(100);
    DECLARE @details VARBINARY(MAX) = @p_details;
    DECLARE @details_format_id TINYINT = @p_details_format_id;

    SELECT
        @from_status = r.job_status_code,
        @namespace_id = j.namespace_id,
        @lineage_root_job_id = j.lineage_root_job_id,
        @definition_id = j.definition_id,
        @tenant_id = j.tenant_id,
        @execution_number = r.execution_number,
        @event_recording = j.event_recording_code,
        @old_format_id = j.input_format_id,
        @old_input = j.input
    FROM {{schema}}.jobs_state r WITH (UPDLOCK, ROWLOCK)
    INNER JOIN {{schema}}.jobs j ON j.job_id = r.job_id
    WHERE r.job_id = @p_job_id;

    IF @from_status IS NULL
        BEGIN

            SELECT
                CAST(2 /* ControlAction.NotFound */ AS TINYINT) AS action,
                CAST(NULL AS TINYINT) AS status;
            GOTO Finish;
        END;

    IF
        @from_status IN (
            50 /* JobStatusCode.Executing */
        )
        BEGIN

            SELECT
                CAST(3 /* ControlAction.Rejected */ AS TINYINT) AS action,
                @from_status AS status;
            GOTO Finish;
        END;

    UPDATE {{schema}}.jobs
    SET
        input = @p_input,
        input_format_id = @p_input_format_id
    WHERE job_id = @p_job_id;

    IF @event_recording = 20 /* EventRecordingCode.Audit */
        BEGIN
        -- The details are the operator's, plus bounded JSON metadata about the previous payload (format name
        -- and byte count), never the payload itself. The writer's details are a compact JSON object, so the
        -- metadata joins it as more members before its closing brace. Pure ASCII, so VARCHAR -> VARBINARY is UTF-8.
            IF @old_format_id <> 0 /* JobPayloadFormat.None */
                BEGIN
                    SET @old_members =
                        '"format":"'
                        + CASE @old_format_id
                            WHEN 1 /* JobPayloadFormat.Json */ THEN 'json'
                            WHEN 2 /* JobPayloadFormat.Bytes */ THEN 'bytes'
                            WHEN 3 /* JobPayloadFormat.Text */ THEN 'text'
                            ELSE 'custom-' + CONVERT(VARCHAR(3), @old_format_id)
                        END
                        + '","bytes":' + CONVERT(VARCHAR(20), DATALENGTH(@old_input));
                    SET @details_format_id = 1 /* JobPayloadFormat.Json */;
                    SET @details =
                        CASE
                            WHEN @p_details IS NULL THEN CONVERT(VARBINARY(MAX), '{' + @old_members + '}')
                            ELSE
                                SUBSTRING(@p_details, 1, DATALENGTH(@p_details) - 1)
                                + CONVERT(VARBINARY(MAX), ',' + @old_members + '}')
                        END;
                END;

            INSERT INTO {{schema}}.events (
                event_code, created_at_utc, namespace_id,
                entity_code, entity_id,
                definition_id, tenant_id, lineage_root_job_id,
                execution_number,
                event_message, details_format_id, details
            )
            VALUES (
                76 /* EventCode.JobInputAmended */, @now, @namespace_id,
                50 /* EntityCode.Job */, @p_job_id,
                @definition_id, @tenant_id, COALESCE(@lineage_root_job_id, @p_job_id),
                @execution_number,
                @p_event_message, @details_format_id, @details
            );
        END

    SELECT
        CAST(1 /* ControlAction.Applied */ AS TINYINT) AS action,
        @from_status AS status;

Finish:

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
