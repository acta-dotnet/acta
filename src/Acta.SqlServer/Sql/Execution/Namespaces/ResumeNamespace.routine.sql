CREATE OR ALTER PROCEDURE {{schema}}.resume_namespace
    @p_namespace_name VARCHAR(128),
    @p_actor_code TINYINT,
    @p_actor_key VARCHAR(128),
    @p_reason_message NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @id SMALLINT, @status TINYINT, @version INT;
    BEGIN TRY
        BEGIN TRANSACTION;
        SELECT
            @id = n.id,
            @status = n.status_code,
            @version = n.version
        FROM {{schema}}.namespaces n WITH (UPDLOCK, ROWLOCK)
        WHERE n.name = @p_namespace_name;

        IF @id IS NULL
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(2 /* AdminControlAction.NotFound */ AS SMALLINT) AS action,
                    CAST(NULL AS INT) AS version;
                RETURN;
            END;

        IF @status = 10 /* NamespaceStatusCode.Active */
            BEGIN
                COMMIT TRANSACTION;
                SELECT
                    CAST(3 /* AdminControlAction.AlreadyInState */ AS SMALLINT) AS action,
                    @version AS version;
                RETURN;
            END;

        UPDATE {{schema}}.namespaces
        SET status_code = 10 /* NamespaceStatusCode.Active */, modified_at_utc = @now, version = version + 1
        WHERE id = @id;
        SET @version = @version + 1;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key,
            job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id, worker_id,
            from_status_code, to_status_code, execution_status_code, duration_ms, reason_code, reason_message
        )
        VALUES (
            21 /* EventCode.NamespaceResumed */, @now, @id, @p_actor_code, @p_actor_key,
            NULL, NULL, NULL, NULL, NULL, NULL, NULL,
            NULL, NULL, NULL, NULL, NULL, @p_reason_message
        );

        COMMIT TRANSACTION;
        SELECT
            CAST(1 /* AdminControlAction.Applied */ AS SMALLINT) AS action,
            @version AS version;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
