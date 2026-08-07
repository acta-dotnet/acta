CREATE OR ALTER PROCEDURE {{schema}}.register_tenant
    @p_tenant_key VARCHAR(128),
    @p_display_name NVARCHAR(128),
    @p_description NVARCHAR(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now DATETIME2(7) = SYSUTCDATETIME();

    BEGIN TRY
        INSERT INTO {{schema}}.tenants
        (tenant_key, display_name, description, status_code, created_at_utc, modified_at_utc, version)
        SELECT
            @p_tenant_key,
            @p_display_name,
            @p_description,
            10 /* TenantStatusCode.Active */,
            @now,
            @now,
            0
        WHERE NOT EXISTS (
            SELECT 1 FROM {{schema}}.tenants
            WHERE tenant_key = @p_tenant_key
        );
    END TRY
    BEGIN CATCH
        -- A same-key race loses the guarded INSERT to a unique violation; the winner's row is the result.
        IF ERROR_NUMBER() NOT IN (2627, 2601)
            BEGIN
                THROW;
            END;
    END CATCH;

    SELECT id AS tenant_id FROM {{schema}}.tenants
    WHERE tenant_key = @p_tenant_key;
END;
GO
