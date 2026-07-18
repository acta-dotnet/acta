CREATE OR ALTER PROCEDURE {{schema}}.register_tenant
    @p_tenant_key   VARCHAR(128),
    @p_display_name NVARCHAR(128),
    @p_description  NVARCHAR(512),
    @p_status_code  TINYINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @tenant_id INT;

    BEGIN TRY
        BEGIN TRANSACTION;

        UPDATE {{schema}}.tenants
           SET display_name    = @p_display_name,
               description     = @p_description,
               status_code     = @p_status_code,
               modified_at_utc = @now,
               version         = version + 1
         WHERE tenant_key = @p_tenant_key
           AND (status_code <> @p_status_code
                OR COALESCE(display_name, '') <> COALESCE(@p_display_name, '')
                OR COALESCE(description, '') <> COALESCE(@p_description, ''));

        INSERT INTO {{schema}}.tenants
            (tenant_key, display_name, description, status_code, created_at_utc, modified_at_utc, version)
        SELECT @p_tenant_key, @p_display_name, @p_description, @p_status_code, @now, @now, 0
         WHERE NOT EXISTS (SELECT 1 FROM {{schema}}.tenants WHERE tenant_key = @p_tenant_key);

        SELECT @tenant_id = id FROM {{schema}}.tenants WHERE tenant_key = @p_tenant_key;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;

    SELECT @tenant_id AS tenant_id;
END;
GO
