CREATE OR ALTER PROCEDURE {{schema}}.raise_job_alert
    @p_namespace_name VARCHAR(128),
    @p_job_id BIGINT,
    @p_origin_code TINYINT,
    @p_severity_code TINYINT,
    @p_kind_code TINYINT,
    @p_title NVARCHAR(512),
    @p_message NVARCHAR(512),
    @p_channel_name VARCHAR(128),
    @p_delivery_status_code TINYINT,
    @p_dedupe_key VARCHAR(512),
    @p_dedupe_window_start_utc DATETIME2(3),
    @p_source_event_id BIGINT,
    @p_alert_ref UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

    DECLARE @v_ns SMALLINT = (
        SELECT id FROM {{schema}}.namespaces
        WHERE name = @p_namespace_name
    );
    IF @v_ns IS NULL
        THROW 50000, 'raise_job_alert: unknown namespace', 1;

    DECLARE @v_job_ref UNIQUEIDENTIFIER = (
        SELECT job_ref FROM {{schema}}.jobs
        WHERE id = @p_job_id
    );

    IF @p_job_id IS NOT NULL AND @v_job_ref IS NULL
        THROW 50007, 'ACTA:ALERT_UNKNOWN_JOB:raise_job_alert: unknown job id', 1;

    IF @p_dedupe_key IS NULL
        BEGIN
            INSERT INTO {{schema}}.alerts (
                namespace_id, alert_ref, job_id, job_ref,
                origin_code, severity_code, kind_code, title, message, channel_name,
                dedupe_key, dedupe_window_start_utc, occurrence_count, last_projected_event_id,
                delivery_status_code, retry_count,
                created_at_utc, modified_at_utc, version
            )
            VALUES (
                @v_ns, @p_alert_ref, @p_job_id, @v_job_ref,
                @p_origin_code, @p_severity_code, @p_kind_code, @p_title, @p_message, @p_channel_name,
                NULL, NULL, 1, @p_source_event_id,
                @p_delivery_status_code, 0,
                @now, @now, 0
            );
            SELECT 1;
            RETURN;
        END

    DECLARE @updated TABLE (occurrence_count INT NOT NULL);

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE ja
        SET
            job_id = @p_job_id,
            job_ref = @v_job_ref,
            origin_code = @p_origin_code,
            severity_code = @p_severity_code,
            kind_code = @p_kind_code,
            title = @p_title,
            message = @p_message,
            channel_name = @p_channel_name,
            occurrence_count = ja.occurrence_count + 1,
            resolved_at_utc = NULL,
            last_projected_event_id = COALESCE(@p_source_event_id, ja.last_projected_event_id),
            modified_at_utc = @now,
            version = ja.version + 1
        OUTPUT INSERTED.occurrence_count INTO @updated
        FROM {{schema}}.alerts AS ja WITH (UPDLOCK, HOLDLOCK, INDEX (ux_alerts_dedupe))
        WHERE
            ja.namespace_id = @v_ns
            AND ja.dedupe_key = @p_dedupe_key
            AND ja.dedupe_window_start_utc = @p_dedupe_window_start_utc
            AND (
                @p_source_event_id IS NULL
                OR ja.last_projected_event_id IS NULL
                OR @p_source_event_id > ja.last_projected_event_id
            );

        IF NOT EXISTS (SELECT 1 FROM @updated)
            BEGIN
                -- Either no row is there yet and this raise inserts it, or one is and this is a replay
                -- of an event it already absorbed. Reading under the range lock the UPDATE already
                -- took tells the two apart without a race.
                DECLARE @v_existing_count INT = (
                    SELECT ja.occurrence_count
                    FROM {{schema}}.alerts AS ja WITH (UPDLOCK, HOLDLOCK, INDEX (ux_alerts_dedupe))
                    WHERE
                        ja.namespace_id = @v_ns
                        AND ja.dedupe_key = @p_dedupe_key
                        AND ja.dedupe_window_start_utc = @p_dedupe_window_start_utc
                );

                IF @v_existing_count IS NULL
                    BEGIN
                        INSERT INTO {{schema}}.alerts (
                            namespace_id, alert_ref, job_id, job_ref,
                            origin_code, severity_code, kind_code, title, message, channel_name,
                            dedupe_key, dedupe_window_start_utc, occurrence_count, last_projected_event_id,
                            delivery_status_code, retry_count,
                            created_at_utc, modified_at_utc, version
                        )
                        OUTPUT INSERTED.occurrence_count INTO @updated
                        VALUES (
                            @v_ns, @p_alert_ref, @p_job_id, @v_job_ref,
                            @p_origin_code, @p_severity_code, @p_kind_code, @p_title, @p_message, @p_channel_name,
                            @p_dedupe_key, @p_dedupe_window_start_utc, 1, @p_source_event_id,
                            @p_delivery_status_code, 0,
                            @now, @now, 0
                        );
                    END
                ELSE
                    BEGIN
                        -- A replay: nothing written, and the caller's failure threshold reads the count
                        -- the row already carries.
                        INSERT INTO @updated (occurrence_count) VALUES (@v_existing_count);
                    END
            END

        COMMIT TRANSACTION;

        SELECT TOP (1) occurrence_count FROM @updated;
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
