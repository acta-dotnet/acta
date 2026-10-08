SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @now DATETIME2(7) = SYSUTCDATETIME();
DECLARE @id INT, @version INT, @applied BIT = 0;

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    SELECT @id = n.id, @version = n.version
    FROM {{schema}}.namespaces n WITH (UPDLOCK, ROWLOCK)
    WHERE n.name = @p_namespace_name;

    IF @id IS NOT NULL AND @version = @p_expected_version
    BEGIN
        UPDATE {{schema}}.namespaces
        SET owner_team = @p_owner_team, description = @p_description, modified_at_utc = @now, version = version + 1
        WHERE id = @id;
        SET @version = @version + 1;
        SET @applied = 1;

        INSERT INTO {{schema}}.events (event_code, created_at_utc, namespace_id, actor_code, actor_key, reason_message)
        VALUES (22 /* EventCode.NamespaceUpdated */, @now, @id, @p_actor_code, @p_actor_key, @p_reason_message);
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
