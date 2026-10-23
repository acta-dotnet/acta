SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @tenant_id INT, @status TINYINT, @version INT;

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    SELECT @tenant_id = t.tenant_id, @status = t.tenant_status_code, @version = t.version
    FROM {{schema}}.tenants t WITH (UPDLOCK, ROWLOCK)
    WHERE t.tenant_name = @p_tenant_name;

    IF @tenant_id IS NOT NULL AND @status <> 10 /* TenantStatusCode.Active */
    BEGIN
        UPDATE {{schema}}.tenants
        SET tenant_status_code = 10 /* TenantStatusCode.Active */, modified_at_utc = @now, version = version + 1
        WHERE tenant_id = @tenant_id;
        SET @version = @version + 1;

        -- namespace_id 1 is the sys namespace the baseline seeds.
        INSERT INTO {{schema}}.events (
            event_code, created_at_utc, namespace_id,
            entity_code, entity_id,
            event_message, details_format_id, details
        )
        VALUES (
            11 /* EventCode.TenantResumed */, @now, 1,
            120 /* EntityCode.Tenant */, @tenant_id,
            @p_event_message, @p_details_format_id, @p_details
        );
    END;

    SELECT
        CAST(
            CASE
                WHEN @tenant_id IS NULL THEN 2 /* ControlAction.NotFound */
                WHEN @status = 10 /* TenantStatusCode.Active */ THEN 6 /* ControlAction.AlreadyInState */
                ELSE 1 /* ControlAction.Applied */
            END AS SMALLINT
        ) AS action,
        @version AS version;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
