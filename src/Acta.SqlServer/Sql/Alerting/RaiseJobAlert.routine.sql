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
                dedupe_key, occurrence_count, last_projected_event_id,
                delivery_status_code, retry_count,
                created_at_utc, modified_at_utc, version
            )
            VALUES (
                @v_ns, @p_alert_ref, @p_job_id, @v_job_ref,
                @p_origin_code, @p_severity_code, @p_kind_code, @p_title, @p_message, @p_channel_name,
                NULL, 1, @p_source_event_id,
                @p_delivery_status_code, 0,
                @now, @now, 0
            );
            SELECT 1, @p_source_event_id;
            RETURN;
        END

    DECLARE @updated TABLE (occurrence_count INT NOT NULL, last_projected_event_id BIGINT NULL);

    BEGIN TRY
        BEGIN TRANSACTION;

        -- The identity's one OPEN row absorbs the repeat; resolution being terminal, a resolved row must
        -- be left for the insert arm below. UPDLOCK/HOLDLOCK over the equality predicate serializes
        -- concurrent raisers - no named-index hint, which a rename in JobAlert.cs would leave stale.
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
            last_projected_event_id = COALESCE(@p_source_event_id, ja.last_projected_event_id),
            modified_at_utc = @now,
            version = ja.version + 1
        OUTPUT INSERTED.occurrence_count, INSERTED.last_projected_event_id INTO @updated
        FROM {{schema}}.alerts AS ja WITH (UPDLOCK, HOLDLOCK)
        WHERE
            ja.namespace_id = @v_ns
            AND ja.dedupe_key = @p_dedupe_key
            AND ja.resolved_at_utc IS NULL
            AND (
                @p_source_event_id IS NULL
                OR ja.last_projected_event_id IS NULL
                OR @p_source_event_id > ja.last_projected_event_id
            );

        IF NOT EXISTS (SELECT 1 FROM @updated)
            BEGIN
                -- No open row took it, or one is there and held a replayed event back. The insert opens a
                -- fresh incident only when neither holds: no open row at all, AND - the ghost guard - no
                -- row of this identity already marked at or past this event.
                INSERT INTO {{schema}}.alerts (
                    namespace_id, alert_ref, job_id, job_ref,
                    origin_code, severity_code, kind_code, title, message, channel_name,
                    dedupe_key, occurrence_count, last_projected_event_id,
                    delivery_status_code, retry_count,
                    created_at_utc, modified_at_utc, version
                )
                OUTPUT INSERTED.occurrence_count, INSERTED.last_projected_event_id INTO @updated
                SELECT
                    @v_ns, @p_alert_ref, @p_job_id, @v_job_ref,
                    @p_origin_code, @p_severity_code, @p_kind_code, @p_title, @p_message, @p_channel_name,
                    @p_dedupe_key, 1, @p_source_event_id,
                    @p_delivery_status_code, 0,
                    @now, @now, 0
                -- No lock hint: this reads the whole identity, resolved rows included, which the
                -- unresolved-only index cannot serve - hinting it would hold a scan's worth of locks
                -- and the UPDATE above already serializes raisers of this identity.
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM {{schema}}.alerts AS g
                    WHERE
                        g.namespace_id = @v_ns
                        AND g.dedupe_key = @p_dedupe_key
                        AND (g.resolved_at_utc IS NULL OR g.last_projected_event_id >= @p_source_event_id)
                );

                IF NOT EXISTS (SELECT 1 FROM @updated)
                    BEGIN
                        -- Nothing written: a replay-held update or a ghost-blocked insert. The threshold
                        -- reads the count the identity's newest row carries, and that row's mark - never
                        -- the incoming event id - is what keeps this raise from escalating.
                        INSERT INTO @updated (occurrence_count, last_projected_event_id)
                        SELECT TOP (1) ja.occurrence_count, ja.last_projected_event_id
                        FROM {{schema}}.alerts AS ja
                        WHERE
                            ja.namespace_id = @v_ns
                            AND ja.dedupe_key = @p_dedupe_key
                        ORDER BY ja.id DESC;
                    END
            END

        COMMIT TRANSACTION;

        SELECT TOP (1) occurrence_count, last_projected_event_id FROM @updated;
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
