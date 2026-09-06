CREATE OR ALTER PROCEDURE {{schema}}.update_namespace
    @p_namespace_name VARCHAR(128),
    @p_owner_team NVARCHAR(512),
    @p_description NVARCHAR(512),
    @p_expected_version INT,
    @p_actor_code TINYINT,
    @p_actor_key NVARCHAR(128),
    @p_reason_message NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @id INT, @version INT;

        SELECT
            @id = n.id,
            @version = n.version
        FROM {{schema}}.namespaces n WITH (UPDLOCK, ROWLOCK)
        WHERE n.name = @p_namespace_name;

        IF @id IS NULL
            BEGIN

                SELECT
                    CAST(2 /* AdminControlAction.NotFound */ AS SMALLINT) AS action,
                    CAST(NULL AS INT) AS version;
                GOTO Finish;
            END;

        IF @version <> @p_expected_version
            BEGIN

                SELECT
                    CAST(4 /* AdminControlAction.VersionConflict */ AS SMALLINT) AS action,
                    @version AS version;
                GOTO Finish;
            END;

        UPDATE {{schema}}.namespaces
        SET owner_team = @p_owner_team, description = @p_description, modified_at_utc = @now, version = version + 1
        WHERE id = @id;
        SET @version = @version + 1;

        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key,
            job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id, worker_id,
            from_status_code, to_status_code, execution_status_code, duration_ms, reason_code, reason_message
        )
        VALUES (
            22 /* EventCode.NamespaceUpdated */, @now, @id, @p_actor_code, @p_actor_key,
            NULL, NULL, NULL, NULL, NULL, NULL, NULL,
            NULL, NULL, NULL, NULL, NULL, @p_reason_message
        );

        SELECT
            CAST(1 /* AdminControlAction.Applied */ AS SMALLINT) AS action,
            @version AS version;

    Finish:

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
