SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @id INT, @version INT, @applied BIT = 0;

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    SELECT @id = t.id, @version = t.version
    FROM {{schema}}.tenants t WITH (UPDLOCK, ROWLOCK)
    WHERE t.tenant_name = @p_tenant_name;

    IF @id IS NOT NULL AND @version = @p_expected_version
    BEGIN
        UPDATE {{schema}}.tenants
        SET display_name = @p_display_name, description = @p_description, modified_at_utc = @now, version = version + 1
        WHERE id = @id;
        SET @version = @version + 1;
        SET @applied = 1;

        -- namespace_id 1 is the sys namespace the baseline seeds.
        INSERT INTO {{schema}}.events (event_code, created_at_utc, namespace_id, actor_code, actor, tenant_id, reason_message)
        VALUES (12 /* EventCode.TenantUpdated */, @now, 1, @p_actor_code, @p_actor, @id, @p_reason_message);
    END;

    SELECT
        CAST(
            CASE
                WHEN @id IS NULL THEN 2 /* ControlAction.NotFound */
                WHEN @applied = 1 THEN 1 /* ControlAction.Applied */
                ELSE 5 /* ControlAction.VersionConflict */
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
