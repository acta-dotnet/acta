CREATE OR ALTER PROCEDURE {{schema}}.update_tenant_metadata
    @p_tenant_key       VARCHAR(128),
    @p_display_name     NVARCHAR(128),
    @p_description      NVARCHAR(512),
    @p_expected_version INT,
    @p_actor_code       TINYINT,
    @p_actor_key         VARCHAR(128),
    @p_reason_message   NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @id INT, @version INT;
    BEGIN TRY
        BEGIN TRANSACTION;
        SELECT @id = t.id, @version = t.version
          FROM {{schema}}.tenants t WITH (UPDLOCK, ROWLOCK)
         WHERE t.tenant_key = @p_tenant_key;

        IF @id IS NULL
        BEGIN
            COMMIT TRANSACTION;
            SELECT CAST(2 /* AdminControlAction.NotFound */ AS SMALLINT) AS action, CAST(NULL AS INT) AS version;
            RETURN;
        END;

        IF @version <> @p_expected_version
        BEGIN
            COMMIT TRANSACTION;
            SELECT CAST(4 /* AdminControlAction.VersionConflict */ AS SMALLINT) AS action, @version AS version;
            RETURN;
        END;

        UPDATE {{schema}}.tenants
           SET display_name = @p_display_name, description = @p_description, modified_at_utc = @now, version = version + 1
         WHERE id = @id;
        SET @version = @version + 1;

        -- namespace_id 1 is the seeded sys namespace (M001).
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id, actor_code, actor_key,
            job_id, job_ref, execution_number, lineage_root_id, definition_id, tenant_id, worker_id,
            from_status_code, to_status_code, execution_status_code, duration_ms, reason_code, reason_message)
        VALUES (
            12 /* JobEventCode.TenantMetadataChanged */, @now, 1, @p_actor_code, @p_actor_key,
            NULL, NULL, NULL, NULL, NULL, @id, NULL,
            NULL, NULL, NULL, NULL, NULL, @p_reason_message);

        COMMIT TRANSACTION;
        SELECT CAST(1 /* AdminControlAction.Applied */ AS SMALLINT) AS action, @version AS version;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
GO
