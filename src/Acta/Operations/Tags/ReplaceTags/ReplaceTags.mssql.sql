SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @entry_trancount INT = @@TRANCOUNT;
DECLARE @scope_id BIGINT;
DECLARE @items TABLE (name VARCHAR(128) NOT NULL PRIMARY KEY, value NVARCHAR(128) NULL, value_search NVARCHAR(128) NULL);

INSERT INTO @items (name, value, value_search)
SELECT name, value, value_search
FROM OPENJSON (@p_items_json) WITH (name VARCHAR(128) '$.name', value NVARCHAR(128) '$.value', value_search NVARCHAR(128) '$.value_search');

BEGIN TRY
    IF @entry_trancount = 0
        BEGIN TRANSACTION;

    IF @p_scope_code = 20 /* TagScopeCode.Tenant */
        SELECT @scope_id = CONVERT(BIGINT, t.id)
        FROM {{schema}}.tenants t WITH (UPDLOCK, ROWLOCK)
        WHERE t.tenant_name = @p_lookup_name;
    ELSE IF @p_scope_code = 30 /* TagScopeCode.Namespace */
        SELECT @scope_id = CONVERT(BIGINT, n.id)
        FROM {{schema}}.namespaces n WITH (UPDLOCK, ROWLOCK)
        WHERE n.name = @p_lookup_name;
    ELSE IF @p_scope_code = 40 /* TagScopeCode.Definition */
        SELECT @scope_id = CONVERT(BIGINT, d.id)
        FROM {{schema}}.definitions d WITH (UPDLOCK, ROWLOCK)
        WHERE d.id = @p_lookup_id;
    ELSE IF @p_scope_code = 50 /* TagScopeCode.Job */
        SELECT @scope_id = CONVERT(BIGINT, j.id)
        FROM {{schema}}.jobs j WITH (UPDLOCK, ROWLOCK)
        WHERE j.id = @p_lookup_id;

    IF @scope_id IS NOT NULL
    BEGIN
        DELETE FROM {{schema}}.tags WHERE scope_code = @p_scope_code AND scope_id = @scope_id;

        INSERT INTO {{schema}}.tags (scope_code, scope_id, name, value, value_search)
        SELECT @p_scope_code, @scope_id, name, value, value_search
        FROM @items;
    END;

    SELECT CAST(CASE WHEN @scope_id IS NULL THEN 2 /* ControlAction.NotFound */ ELSE 1 /* ControlAction.Applied */ END AS TINYINT) AS action;

    IF @entry_trancount = 0
        COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @entry_trancount = 0 AND XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
