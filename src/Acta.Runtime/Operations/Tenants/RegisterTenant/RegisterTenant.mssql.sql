CREATE OR ALTER PROCEDURE {{schema}}.register_tenant
    @p_tenant_key VARCHAR(128),
    @p_display_name NVARCHAR(128),
    @p_description NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @entry_trancount INT = @@TRANCOUNT;
    BEGIN TRY
        IF @entry_trancount = 0
            BEGIN TRANSACTION;

        DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
        DECLARE @tenant_id INT;

        -- Hold the indexed key (or gap) until the operation commits; no unique-error recovery in an aborted transaction.
        SELECT @tenant_id = id
        FROM {{schema}}.tenants WITH (UPDLOCK, HOLDLOCK)
        WHERE tenant_key = @p_tenant_key;

        IF @tenant_id IS NULL
        BEGIN
            INSERT INTO {{schema}}.tenants
                (tenant_key, display_name, description, status_code, created_at_utc, modified_at_utc, version)
            VALUES (@p_tenant_key, @p_display_name, @p_description, 10 /* TenantStatusCode.Active */, @now, @now, 0);
            SET @tenant_id = CAST(SCOPE_IDENTITY() AS INT);
        END;

        SELECT @tenant_id AS tenant_id;

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
